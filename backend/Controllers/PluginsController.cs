using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/plugins")]
public class PluginsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginsController> _logger;

    public PluginsController(AppDbContext context, IHttpClientFactory httpClientFactory, ILogger<PluginsController> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PluginDefinition>>> GetPlugins()
    {
        return await _context.Plugins.ToListAsync();
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PluginDefinition>> AddPlugin(PluginDefinition plugin)
    {
        _context.Plugins.Add(plugin);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetPlugins), new { id = plugin.Id }, plugin);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeletePlugin(int id)
    {
        var plugin = await _context.Plugins.FindAsync(id);
        if (plugin == null) return NotFound();

        _context.Plugins.Remove(plugin);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // Proxy Implementation
    // Route: /api/plugins/{id}/proxy/{*path}
    // Forwards request to Plugin BaseUrl + Path
    [AnyMethod] // Custom attribute workaround or just accept All
    [Route("{id}/proxy/{**path}")]
    public async Task<IActionResult> Proxy(int id, string path)
    {
        var plugin = await _context.Plugins.FindAsync(id);
        if (plugin == null) return NotFound("Plugin not found");
        if (!plugin.IsEnabled) return StatusCode(503, "Plugin is disabled");

        // Construct Target URL
        var targetUrl = $"{plugin.BaseUrl.TrimEnd('/')}/{path}";
        var query = Request.QueryString.Value; // Forward query params
        if (!string.IsNullOrEmpty(query)) targetUrl += query;

        _logger.LogInformation($"Proxying Plugin Request: {Request.Method} {path} -> {targetUrl}");

        var client = _httpClientFactory.CreateClient();
        
        // Create Request
        var requestMessage = new HttpRequestMessage();
        requestMessage.RequestUri = new Uri(targetUrl);
        requestMessage.Method = new HttpMethod(Request.Method);

        // Copy Content (if any)
        if (Request.Body != null && (Request.Method == "POST" || Request.Method == "PUT"))
        {
            var streamContent = new StreamContent(Request.Body);
            requestMessage.Content = streamContent;
            if (Request.Headers.ContainsKey("Content-Type"))
                 requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", Request.Headers["Content-Type"].ToArray());
        }

        // Copy Headers (Selectively)
        // Don't forward Host or Length
        foreach (var header in Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || 
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                
            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        try 
        {
            var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
            
            // Stream back response
            Response.StatusCode = (int)response.StatusCode;
            
            foreach (var header in response.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in response.Content.Headers)
            {
                 Response.Headers[header.Key] = header.Value.ToArray();
            }

            var stream = await response.Content.ReadAsStreamAsync();
            return File(stream, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Plugin Proxy Failed for {targetUrl}");
            return StatusCode(502, $"Plugin Proxy Error: {ex.Message}");
        }
    }
}

public class AnyMethodAttribute : RouteAttribute
{
    public AnyMethodAttribute(string template) : base(template) {}
    public AnyMethodAttribute() : base("") {}
}
