using System.Text;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace WebMusic.Backend.Services;

public class TagService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TagService> _logger;
    private readonly string[] _apiKeys;
    private int _currentKeyIndex = 0;
    private readonly HttpClient _httpClient;

    public TagService(IConfiguration config, ILogger<TagService> logger)
    {
        _config = config;
        _logger = logger;
        _apiKeys = _config.GetSection("Gemini:ApiKeys").Get<string[]>() ?? Array.Empty<string>();
        _httpClient = new HttpClient();
    }

    private string GetNextKey()
    {
        if (_apiKeys.Length == 0) throw new InvalidOperationException("No Gemini API Keys configured");
        var key = _apiKeys[_currentKeyIndex];
        _currentKeyIndex = (_currentKeyIndex + 1) % _apiKeys.Length;
        return key;
    }

    // Generic Internal Executor
    private async Task<string> ExecuteGeminiRequest(object requestBody, string model)
    {
        var attempts = 0;
        var maxAttempts = _apiKeys.Length;

        while (attempts < maxAttempts)
        {
            var key = GetNextKey();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}";

            try 
            {
                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Gemini API Error (Key ending in {key.Substring(Math.Max(0, key.Length - 4))}): {response.StatusCode} - {responseString}");
                    
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        attempts++;
                        await Task.Delay(1000); 
                        continue;
                    }
                    throw new Exception($"AI Service Error: {response.StatusCode}");
                }

                dynamic result = JsonConvert.DeserializeObject(responseString)!;
                if (result.candidates == null || result.candidates.Count == 0)
                {
                    // Blocked prompt?
                    throw new Exception("Gemini returned no candidates (safety block?)");
                }
                
                string aiText = result.candidates[0].content.parts[0].text;
                return aiText.Replace("```json", "").Replace("```lrc", "").Replace("```", "").Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Attempt {attempts + 1} failed.");
                attempts++;
                if (attempts >= maxAttempts) throw;
            }
        }
        throw new Exception("AI Service unavailable after retries");
    }

    public async Task<string> GenerateTagsAsync(string userPrompt, object songsContext, string model = "gemini-2.0-flash-exp")
    {
        var systemPrompt = @"
You are a professional music librarian assistant.
Your goal is to clean up and enrich music metadata based on the User's Instruction.
Output STRICTLY a JSON array of objects.
Each object must have: 'id' (integer, kept from input), 'title', 'artist', 'album', 'genre', 'year' (int).
If a field is unknown, keep it empty string or 0.
DO NOT output markdown code blocks, just the raw JSON array.
";
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt },
                        new { text = $"[User Instruction]: {userPrompt}" },
                        new { text = $"[Data Context]: {JsonConvert.SerializeObject(songsContext)}" }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        return await ExecuteGeminiRequest(requestBody, model);
    }

    public async Task<string> PolishLyricsAsync(string lrcContent, string contextInfo = "", string model = "gemini-2.0-flash-exp")
    {
        var systemPrompt = $@"
You are a professional lyrics editor.
Your task is to fix typos, capitalization, punctuation, and remove internal hallucination tags (like 'Subtitles by...') from the provided LRC lyrics.
[Context]: {contextInfo}
RULES:
1. STRICTLY PRESERVE all [mm:ss.xx] timestamps. Do not change the timing.
2. Maintain the exact line count if possible (only merge lines if they are broken sentences).
3. Output ONLY the corrected LRC content. No conversational text.
";
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt },
                        new { text = lrcContent }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2, 
                // Note: We do NOT use application/json here, we want plain text (LRC)
                responseMimeType = "text/plain" 
            }
        };

        return await ExecuteGeminiRequest(requestBody, model);
    }
}
