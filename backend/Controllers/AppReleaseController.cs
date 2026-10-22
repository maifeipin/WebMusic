using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebMusic.Backend.Services;

namespace WebMusic.Backend.Controllers;

/// <summary>
/// App 版本检查与安装包分发（应用内一键更新的服务端）。
///
/// 两个端点均为匿名可访问：版本检查在登录前就要能工作（用户可能在登录页停留），
/// 安装包下载走的是公开静态文件语义，不含任何用户私有数据。
/// </summary>
[Route("api/app")]
[ApiController]
[AllowAnonymous]
public class AppReleaseController : ControllerBase
{
    private const int MaxChangelogLength = 2000;

    private readonly AppReleaseStore _store;
    private readonly ILogger<AppReleaseController> _logger;

    public AppReleaseController(AppReleaseStore store, ILogger<AppReleaseController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 查询最新版本。
    /// GET /api/app/version?currentCode=2&amp;channel=debug&amp;packageName=com.maifeipin.webmusic.debug
    /// </summary>
    [HttpGet("version")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetVersion(
        [FromQuery] int currentCode = 0,
        [FromQuery] string? channel = null,
        [FromQuery] string? packageName = null)
    {
        var normalized = AppReleaseStore.NormalizeChannel(channel);
        var entry = _store.GetChannel(normalized);

        if (entry == null || entry.VersionCode <= 0)
        {
            _logger.LogInformation(
                "App version query: no release published for channel {Channel} (client {PackageName})",
                normalized, packageName ?? "unknown");

            return Ok(new AppVersionResponse(
                HasUpdate: false,
                ForceUpdate: false,
                Channel: normalized,
                VersionCode: 0,
                VersionName: null,
                PackageName: null,
                MinSupportedVersionCode: null,
                SizeBytes: 0,
                Sha256: null,
                Changelog: null,
                PublishedAt: null,
                DownloadUrl: null));
        }

        var apkPath = _store.ResolveApkPath(normalized, entry);
        if (apkPath == null)
        {
            // 清单声明了新版但文件缺失：明确告知客户端不可更新，避免反复弹窗
            _logger.LogWarning(
                "App release manifest declares {Channel} v{VersionCode} but APK {FileName} is missing",
                normalized, entry.VersionCode, entry.FileName);

            return Ok(new AppVersionResponse(
                HasUpdate: false,
                ForceUpdate: false,
                Channel: normalized,
                VersionCode: 0,
                VersionName: null,
                PackageName: entry.PackageName,
                MinSupportedVersionCode: null,
                SizeBytes: 0,
                Sha256: null,
                Changelog: null,
                PublishedAt: null,
                DownloadUrl: null));
        }

        var hasUpdate = entry.VersionCode > currentCode;
        var minSupported = entry.MinSupportedVersionCode ?? 0;
        // 强制更新：清单显式要求，或当前版本已低于最低支持版本
        var forceUpdate = entry.ForceUpdate || (currentCode > 0 && minSupported > 0 && currentCode < minSupported);

        long sizeBytes;
        try
        {
            sizeBytes = new FileInfo(apkPath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read APK size for {Path}", apkPath);
            sizeBytes = 0;
        }

        var changelog = entry.Changelog;
        if (!string.IsNullOrEmpty(changelog) && changelog.Length > MaxChangelogLength)
        {
            changelog = changelog[..MaxChangelogLength];
        }

        // 使用相对路径：客户端按自身配置的 serverUrl 拼接，避免反向代理下的 scheme/host 失真
        var downloadUrl = $"/api/app/download/{normalized}/{entry.VersionCode}";

        return Ok(new AppVersionResponse(
            HasUpdate: hasUpdate,
            ForceUpdate: hasUpdate && forceUpdate,
            Channel: normalized,
            VersionCode: entry.VersionCode,
            VersionName: entry.VersionName,
            PackageName: entry.PackageName,
            MinSupportedVersionCode: entry.MinSupportedVersionCode,
            SizeBytes: sizeBytes,
            Sha256: entry.Sha256,
            Changelog: changelog,
            PublishedAt: entry.PublishedAt,
            DownloadUrl: hasUpdate ? downloadUrl : null));
    }

    /// <summary>
    /// 下载指定渠道的最新 APK。支持 HTTP Range（断点续传）与 ETag/Last-Modified 缓存校验。
    /// GET /api/app/download/debug
    /// </summary>
    [HttpGet("download/{channel}")]
    [HttpGet("download/{channel}/{versionCode:int}")]
    [HttpHead("download/{channel}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Download([FromRoute] string channel, [FromRoute] int? versionCode = null)
    {
        var normalized = AppReleaseStore.NormalizeChannel(channel);
        var entry = _store.GetChannel(normalized);
        if (entry == null)
        {
            return NotFound(new { error = $"尚未为渠道 {normalized} 发布安装包" });
        }

        if (versionCode.HasValue && versionCode.Value != entry.VersionCode) return NotFound(new { error = "版本已更新，请重新检查更新" });
        var apkPath = _store.ResolveApkPath(normalized, entry);
        if (apkPath == null)
        {
            _logger.LogWarning("APK for channel {Channel} declared but not readable: {FileName}", normalized, entry.FileName);
            return NotFound(new { error = "安装包文件缺失" });
        }

        Response.Headers["X-App-Version-Code"] = entry.VersionCode.ToString();
        Response.Headers["X-App-Version-Name"] = entry.VersionName;
        if (!string.IsNullOrWhiteSpace(entry.Sha256))
        {
            Response.Headers["X-App-Sha256"] = entry.Sha256;
        }

        return PhysicalFile(
            apkPath,
            AppReleaseStore.ApkContentType,
            fileDownloadName: entry.FileName,
            enableRangeProcessing: true);
    }
}

public sealed record AppVersionResponse(
    bool HasUpdate,
    bool ForceUpdate,
    string Channel,
    int VersionCode,
    string? VersionName,
    string? PackageName,
    int? MinSupportedVersionCode,
    long SizeBytes,
    string? Sha256,
    string? Changelog,
    string? PublishedAt,
    string? DownloadUrl);
