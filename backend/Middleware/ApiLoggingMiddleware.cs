using System.Diagnostics;
using System.Security.Claims;

namespace WebMusic.Backend.Middleware;

public class ApiLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiLoggingMiddleware> _logger;

    public ApiLoggingMiddleware(RequestDelegate next, ILogger<ApiLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 1. Capture basic info
        var method = context.Request.Method;
        var path = context.Request.Path;
        
        // Filter: only log /api requests
        // Also skip noisy endpoints like Stream and Cover Art
        if (!path.StartsWithSegments("/api") || 
            path.Value.Contains("/stream") || 
            path.Value.Contains("/cover"))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // 2. Execute the pipeline
        await _next(context);

        stopwatch.Stop();

        // 3. Extract User Info (available if Middleware is placed after Auth)
        string user = "Anonymous";
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var name = context.User.Identity.Name;
            var id = context.User.FindFirst("sub")?.Value 
                     ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            user = string.IsNullOrEmpty(name) ? $"Id:{id}" : $"{name} (Id:{id})";
        }

        var statusCode = context.Response.StatusCode;

        // 4. Log
        _logger.LogInformation("API_AUDIT: User=[{User}] | Method=[{Method}] | Path=[{Path}] | Status=[{Status}] | Duration=[{Duration}ms]", 
            user, method, path, statusCode, stopwatch.ElapsedMilliseconds);
    }
}
