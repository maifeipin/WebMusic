using System.Text.Json;

namespace WebMusic.Backend.Services;

/// <summary>
/// Android APK 发布清单仓库（无第三方依赖，纯文件驱动）。
///
/// 目录约定（默认 {ContentRoot}/data/app-releases）：
/// <code>
/// data/app-releases/
///   manifest.json                     // 各渠道最新版本元数据
///   debug/webmusic-debug-2.apk        // 调试渠道产物
///   release/webmusic-release-3.apk    // 正式渠道产物
/// </code>
///
/// manifest.json 结构：
/// <code>
/// {
///   "channels": {
///     "debug": {
///       "versionCode": 2,
///       "versionName": "1.1.0",
///       "fileName": "webmusic-debug-2.apk",
///       "packageName": "com.maifeipin.webmusic.debug",
///       "changelog": "- 新增应用内一键更新",
///       "sha256": "…",
///       "forceUpdate": false,
///       "minSupportedVersionCode": 1,
///       "publishedAt": "2026-10-23T10:00:00+08:00"
///     }
///   }
/// }
/// </code>
///
/// 清单文件被替换后无需重启服务：以文件写入时间为版本戳自动重载。
/// </summary>
public sealed class AppReleaseStore
{
    public const string ManifestFileName = "manifest.json";
    public const string ApkContentType = "application/vnd.android.package-archive";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<AppReleaseStore> _logger;
    private readonly object _sync = new();

    private AppReleaseManifest? _cached;
    private DateTime _cachedStampUtc = DateTime.MinValue;

    public AppReleaseStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<AppReleaseStore> logger)
    {
        _logger = logger;
        RootDir = ResolveRootDir(configuration["AppRelease:RootDir"], environment);
        _logger.LogInformation("App release store root: {RootDir}", RootDir);
    }

    /// <summary>发布产物根目录（绝对路径）。</summary>
    public string RootDir { get; }

    public string ManifestPath => Path.Combine(RootDir, ManifestFileName);

    /// <summary>
    /// 归一化渠道名：仅允许小写字母/数字/下划线/短横线，最长 32 字符。
    /// 非法或为空时回退到 release，避免任何路径注入。
    /// </summary>
    public static string NormalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return "release";

        var trimmed = channel.Trim().ToLowerInvariant();
        if (trimmed.Length == 0 || trimmed.Length > 32) return "release";

        foreach (var c in trimmed)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) return "release";
        }
        return trimmed;
    }

    /// <summary>读取清单中指定渠道的最新版本；清单缺失或不含该渠道时返回 null。</summary>
    public AppReleaseEntry? GetChannel(string channel)
    {
        var channels = GetChannels();
        var normalized = NormalizeChannel(channel);
        return channels.TryGetValue(normalized, out var entry) ? entry : null;
    }

    public IReadOnlyDictionary<string, AppReleaseEntry> GetChannels()
    {
        var manifest = LoadManifest();
        return manifest?.Channels ?? new Dictionary<string, AppReleaseEntry>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析 APK 物理路径，并做严格的目录穿越与扩展名校验。
    /// 文件不存在、文件名非法、越出发布目录时返回 null。
    /// </summary>
    public string? ResolveApkPath(string channel, AppReleaseEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.FileName)) return null;

        var fileName = entry.FileName;
        if (Path.GetFileName(fileName) != fileName) return null;
        if (!fileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return null;

        var normalized = NormalizeChannel(channel);
        var directory = Path.Combine(RootDir, normalized);
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));

        var rootPrefix = Path.GetFullPath(RootDir).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected APK path outside release root: {Path}", fullPath);
            return null;
        }

        return System.IO.File.Exists(fullPath) ? fullPath : null;
    }

    private AppReleaseManifest? LoadManifest()
    {
        var path = ManifestPath;
        if (!System.IO.File.Exists(path)) return null;

        DateTime stamp;
        try
        {
            stamp = System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat release manifest {Path}", path);
            return null;
        }

        lock (_sync)
        {
            if (_cached != null && stamp == _cachedStampUtc) return _cached;

            try
            {
                using var stream = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var manifest = JsonSerializer.Deserialize<AppReleaseManifest>(stream, JsonOptions);
                if (manifest?.Channels == null) return _cached;

                // JSON 反序列化得到的字典使用默认（大小写敏感）比较器，此处统一重建
                manifest.Channels = new Dictionary<string, AppReleaseEntry>(
                    manifest.Channels, StringComparer.OrdinalIgnoreCase);

                _cached = manifest;
                _cachedStampUtc = stamp;
                return _cached;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse release manifest {Path}", path);
                return _cached;
            }
        }
    }

    private static string ResolveRootDir(string? configured, IWebHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            return Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(environment.ContentRootPath, expanded));
        }

        // 容器内：ContentRoot=/app，data 卷挂载在 /app/data
        var insideContentRoot = Path.Combine(environment.ContentRootPath, "data", "app-releases");
        var dataDir = Path.Combine(environment.ContentRootPath, "data");
        if (Directory.Exists(dataDir)) return Path.GetFullPath(insideContentRoot);

        // 本地 `dotnet run` 于 backend/ 目录时，仓库根的 data/ 在上一级
        var sibling = Path.Combine(environment.ContentRootPath, "..", "data", "app-releases");
        return Path.GetFullPath(sibling);
    }
}

public sealed class AppReleaseManifest
{
    public Dictionary<string, AppReleaseEntry> Channels { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppReleaseEntry
{
    public int VersionCode { get; set; }

    public string VersionName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>APK 的 applicationId，客户端据此在安装前做一致性校验。</summary>
    public string? PackageName { get; set; }

    public string? Changelog { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>强制更新：客户端不允许跳过。</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>低于该 versionCode 的客户端将被强制更新。</summary>
    public int? MinSupportedVersionCode { get; set; }

    public string? PublishedAt { get; set; }
}
