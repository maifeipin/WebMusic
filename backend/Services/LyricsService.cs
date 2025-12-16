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
    private readonly ISmbService _smbService;

    public LyricsService(AppDbContext context, ILogger<LyricsService> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration, ISmbService smbService)
    {
        _context = context;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _smbService = smbService;
    }

    public async Task<Lyric?> GetLyricsAsync(int mediaId)
    {
        return await _context.Lyrics
            .Include(l => l.MediaFile)
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
        var media = await _context.MediaFiles
            .Include(m => m.ScanSource)
                .ThenInclude(s => s.StorageCredential)
            .FirstOrDefaultAsync(m => m.Id == mediaId);
            
        if (media == null) return null;

        // Health check
        if (!await CheckAiServiceHealthAsync())
        {
            _logger.LogWarning("AI Service is not healthy or AI Url not configured.");
            return null;
        }

        var aiServiceUrl = _configuration["AiLyricsUrl"] ?? "http://webmusic-ai-lyrics:5001";

        // 2. Prepare SMB Config for AI Service
        object smbConfig = null;

        if (media.ScanSource != null && !string.IsNullOrEmpty(media.ScanSource.Path))
        {
            // Parse Creds
            string username = "";
            string password = "";
            try 
            {
                using var doc = JsonDocument.Parse(media.ScanSource.StorageCredential?.AuthData ?? "{}");
                if (doc.RootElement.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
            } catch {}

            // Parse Host/Share
            string host = media.ScanSource.StorageCredential?.Host ?? "";
            if (host.StartsWith("smb://")) host = new Uri(host).Host;
            
            // Guess Share Name from Source Path or Media Path?
            // Usually Source Path is "smb://host/share/folder"
            string share = "";
            string relativePath = media.FilePath; // Assume DB path is relative to Share? Or Absolute?
            
            // Logic to verify path relativity
            // If Source.Path is "smb://1.2.3.4/DataSync", Share is "DataSync".
            // If Source.Path is "sharedata", Share is ???
            
            try {
                if (media.ScanSource.Path.StartsWith("smb://")) {
                     var uri = new Uri(media.ScanSource.Path);
                     if (uri.Segments.Length > 1) share = uri.Segments[1].Trim('/');
                } else {
                     // Assume first part of path is share if simple string?
                     // Or fallback to 'sharedata' logic?
                     // Let's rely on robust user config.
                     var parts = media.ScanSource.Path.Split(new[]{'/', '\\'}, StringSplitOptions.RemoveEmptyEntries);
                     if (parts.Length > 0) share = parts[0];
                }
            } catch {}

            // Fix legacy relative paths (e.g. "sharedata/...")
            // If path starts with "sharedata/" and share is "DataSync", we assume "sharedata" is folder INSIDE DataSync.
            // Pass the literal path string from DB to AI. AI will try Open(tree, path).
            
            smbConfig = new 
            {
                host = host,
                share = share,
                username = username,
                password = password,
                file_path = media.FilePath
            };
        }
        else
        {
            // Attempt fallback or error
            _logger.LogWarning($"Media {mediaId} has no ScanSource. AI cannot fetch file via SMB.");
            // If we are strictly "No Mount", and no source, we fail.
            // Exception: If user entered path manually without scan source? Unlikely.
             throw new InvalidOperationException("Media Source Not Found. Rescan Library as SMB Source required.");
        }

        _logger.LogInformation($"Requesting AI Transcription via SMB Handoff: Host='{((dynamic)smbConfig).host}', Share='{((dynamic)smbConfig).share}', Path='{media.FilePath}'");

        var requestBody = new 
        { 
            smb_config = smbConfig,
            language = language,
            initial_prompt = prompt
        };
        
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10); 

        try 
        {
            // Use JSON POST
            var response = await client.PostAsync($"{aiServiceUrl}/transcribe", 
                new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"));
            
            if (!response.IsSuccessStatusCode)
            {
                var errorDetails = await response.Content.ReadAsStringAsync();
                _logger.LogError($"AI Service returned error: {response.StatusCode}. Details: {errorDetails}");
            }
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

            // 5. Save to DB
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


    public async Task<Lyric> UpdateLyricsAsync(int mediaId, string newContent, string source, string version)
    {
        var lyric = await _context.Lyrics.FirstOrDefaultAsync(l => l.MediaFileId == mediaId);
        if (lyric == null)
        {
            lyric = new Lyric
            {
                MediaFileId = mediaId,
                Content = newContent,
                Source = source,
                Version = version,
                Language = "unknown",
                CreatedAt = DateTime.UtcNow
            };
            _context.Lyrics.Add(lyric);
        }
        else
        {
            lyric.Content = newContent;
            lyric.Source = source;
            lyric.Version = version;
            lyric.CreatedAt = DateTime.UtcNow; 
        }

        await _context.SaveChangesAsync();
        return lyric;
    }
}
