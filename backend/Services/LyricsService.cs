using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public class LyricsService
{
    private readonly AppDbContext _context;
    private readonly ILogger<LyricsService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public LyricsService(AppDbContext context, ILogger<LyricsService> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<Lyric?> GetLyricsAsync(int mediaId)
    {
        return await _context.Lyrics
            .FirstOrDefaultAsync(l => l.MediaFileId == mediaId);
    }

    public async Task<bool> CheckAiServiceHealthAsync()
    {
        var aiServiceUrl = _configuration["AiLyricsUrl"] ?? "http://webmusic-ai-lyrics:5001";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(2); // Fast check

        try
        {
            var response = await client.GetAsync($"{aiServiceUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Lyric> GenerateLyricsAsync(int mediaId, string language = null, string prompt = null)
    {
        // 1. Get MediaFile
        var media = await _context.MediaFiles.FindAsync(mediaId);
        if (media == null) return null;

        // Health check
        if (!await CheckAiServiceHealthAsync())
        {
            _logger.LogWarning("AI Service is not healthy or AI Url not configured.");
            return null;
        }

        // 2. Call Python AI Service
        var aiServiceUrl = _configuration["AiLyricsUrl"] ?? "http://webmusic-ai-lyrics:5001";
        
        // Path mapping logic matches existing implementation...
        var containerPath = media.FilePath;

        // Case 1: Local Data Folder
        if (containerPath.Contains("/data/"))
        {
             var relativePart = containerPath.Substring(containerPath.IndexOf("/data/")); 
             containerPath = "/app" + relativePart;
        }
        // Case 2: SMB/Volume Paths
        else if (containerPath.StartsWith("/Volumes/"))
        {
             containerPath = containerPath.Replace("/Volumes/", "/app/");
        }
        else if (containerPath.StartsWith("smb://"))
        {
             var noScheme = containerPath.Substring(6); 
             var firstSlash = noScheme.IndexOf('/');
             if (firstSlash > 0)
             {
                 containerPath = "/app" + noScheme.Substring(firstSlash);
             }
        }
        // Case 3: Relative paths (sharedata hack)
        else if (!containerPath.StartsWith("/") && !containerPath.Contains("://"))
        {
             if (containerPath.StartsWith("sharedata"))
             {
                 containerPath = "/app/DataSync/" + containerPath;
             }
        }

        _logger.LogInformation($"Generating Lyrics: Original Path='{media.FilePath}', Container Path='{containerPath}', Lang='{language}', Prompt='{prompt}'");

        var requestBody = new 
        { 
            file_path = containerPath,
            language = language,
            initial_prompt = prompt
        };
        
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10); 

        try 
        {
            var response = await client.PostAsync($"{aiServiceUrl}/transcribe", 
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));
            
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // 3. Format to LRC
            var segments = root.GetProperty("segments");
            var sb = new StringBuilder();
            sb.AppendLine($"[ti:{media.Title}]");
            sb.AppendLine($"[ar:{media.Artist}]");
            sb.AppendLine($"[al:{media.Album}]");
            sb.AppendLine($"[by:WebMusic AI]");
            
            foreach (var seg in segments.EnumerateArray())
            {
                var time = seg.GetProperty("time").GetString();
                var text = seg.GetProperty("text").GetString();
                sb.AppendLine($"{time}{text}");
            }

            var lrcContent = sb.ToString();
            var detectedLang = root.GetProperty("language").GetString() ?? "unknown";

            // 4. Save to DB
            var lyric = new Lyric
            {
                MediaFileId = media.Id,
                Content = lrcContent,
                Language = detectedLang,
                Source = "AI (Whisper-Tiny)",
                Version = "v1",
                CreatedAt = DateTime.UtcNow
            };

            _context.Lyrics.Add(lyric);
            await _context.SaveChangesAsync();

            return lyric;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate lyrics for {MediaId}", mediaId);
            throw;
        }
    }

    // FUTURE: Implement Gemini correction
    public async Task<Lyric> CorrectLyricsWithGeminiAsync(int lyricId)
    {
        // 1. Get existing lyric
        // 2. Send to Gemini with prompt "Fix typos and align lines better..."
        // 3. Save as new version (e.g. v2) or overwrite
        throw new NotImplementedException("Coming in WebMusic v2.5!");
    }
}
