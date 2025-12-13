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

    public async Task<string> GenerateTagsAsync(string userPrompt, object songsContext, string model = "gemini-2.0-flash-exp")
    {
        var attempts = 0;
        var maxAttempts = _apiKeys.Length; // Try each key once
        
        while (attempts < maxAttempts)
        {
            var key = GetNextKey();
            // Dynamic model selection
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}";

            var systemPrompt = @"
You are a professional music librarian assistant.
Your goal is to clean up and enrich music metadata based on the User's Instruction.
Output STRICTLY a JSON array of objects.
Each object must have: 'id' (integer, kept from input), 'title', 'artist', 'album', 'genre', 'year' (int).
If a field is unknown, keep it empty string or 0.
DO NOT output markdown code blocks (like ```json), just the raw JSON array.
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

            try 
            {
                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Gemini API Error (Key ending in {key.Substring(Math.Max(0, key.Length - 4))}): {response.StatusCode} - {responseString}");
                    
                    // If 429 (Too Many Requests) or 5xx, try next key
                    // Also retry if 404 (Model not found) - maybe this key has no access, or try next key effectively (though 404 is usually global)
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        attempts++;
                        await Task.Delay(1000); // Slight backoff
                        continue;
                    }
                    throw new Exception($"AI Service Error: {response.StatusCode}");
                }

                dynamic result = JsonConvert.DeserializeObject(responseString)!;
                string aiText = result.candidates[0].content.parts[0].text;
                return aiText.Replace("```json", "").Replace("```", "").Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Attempt {attempts + 1} failed.");
                attempts++;
                if (attempts >= maxAttempts) throw new Exception("All AI service keys exhausted or failed.");
            }
        }
        
        throw new Exception("AI Service unavailable after retries");
    }
}
