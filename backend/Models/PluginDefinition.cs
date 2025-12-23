using System;

namespace WebMusic.Backend.Models;

public class PluginDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string BaseUrl { get; set; } = ""; // Internal Docker URL e.g. http://plugin-netease:3000
    public string EntryPath { get; set; } = "/"; // Relative path for UI e.g. /index.html
    public string Icon { get; set; } = "Extension"; // Lucide Icon Name
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
