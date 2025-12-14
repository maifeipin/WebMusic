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

    public async Task<Lyric> GenerateLyricsAsync(int mediaId)
    {
        // 1. Get MediaFile
        var media = await _context.MediaFiles.FindAsync(mediaId);
        if (media == null) throw new KeyNotFoundException("Media file not found");

        // 2. Call Python AI Service
        // AI Service URL from config or default to internal docker name
        var aiServiceUrl = _configuration["AiLyricsUrl"] ?? "http://webmusic-ai-lyrics:5001";
        
        // For local Mac development (outside docker), we might need localhost
        // If running in container -> use service name. If running locally -> use localhost.
        // But the FilePath must be accessible to the AI container!
        // This is tricky: Local C# -> Local Python Container.
        // The Python container needs the file path. Both mount ./data to /app/data.
        // So we need to convert the DB path (which might be /app/data/...) to match.

        // Assume DB path is like /app/data/music/song.mp3 or valid SMB path
        var requestBody = new { file_path = media.FilePath };
        
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10); // Whisper takes time

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
            var language = root.GetProperty("language").GetString() ?? "unknown";

            // 4. Save to DB
            var lyric = new Lyric
            {
                MediaFileId = media.Id,
                Content = lrcContent,
                Language = language,
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
