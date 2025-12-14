using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebMusic.Backend.Services;
using System.Text;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class DataController : ControllerBase
{
    private readonly DataManagementService _dataService;

    public DataController(DataManagementService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportData()
    {
        var json = await _dataService.ExportMetadataAsync();
        var bytes = Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json", $"webmusic_metadata_{DateTime.Now:yyyyMMdd_HHmmss}.json");
    }

    [HttpPost("import")]
    public async Task<IActionResult> ImportData([FromForm] IFormFile file, [FromQuery] ImportMode mode = ImportMode.AppendOrSkip)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded");

        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync();

        var result = await _dataService.ImportMetadataAsync(json, mode);
        
        if (!result.Success) return BadRequest(result.Message);
        
        return Ok(result);
    }
}
