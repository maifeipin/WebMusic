using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Controllers;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

/// <summary>
/// 应用内更新服务端（版本清单 + APK 分发）的行为约束：
/// - 渠道名归一化必须阻断路径注入；
/// - 清单声明了版本但 APK 缺失时不能诱导客户端更新；
/// - 强制更新阈值（minSupportedVersionCode）要生效；
/// - 清单热更新（文件被替换后无需重启）要生效。
/// </summary>
public class AppReleaseTests : IDisposable
{
    private readonly string _root;

    public AppReleaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wm-app-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 测试清理失败不影响结论 */ }
    }

    private AppReleaseStore CreateStore(string? root = null)
    {
        var effectiveRoot = root ?? _root;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppRelease:RootDir"] = effectiveRoot
            })
            .Build();

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(effectiveRoot);

        return new AppReleaseStore(config, env.Object, NullLogger<AppReleaseStore>.Instance);
    }

    private AppReleaseController CreateController(AppReleaseStore store)
    {
        return new AppReleaseController(store, NullLogger<AppReleaseController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private void PublishApk(string channel, string fileName, int length = 1024)
    {
        var dir = Path.Combine(_root, channel);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), new byte[length]);
    }

    private void WriteManifest(string json, DateTime? stamp = null)
    {
        var path = Path.Combine(_root, AppReleaseStore.ManifestFileName);
        File.WriteAllText(path, json);
        if (stamp.HasValue) File.SetLastWriteTimeUtc(path, stamp.Value);
    }

    private const string DebugManifest = """
    {
      "channels": {
        "debug": {
          "versionCode": 5,
          "versionName": "1.1.0",
          "fileName": "webmusic-debug-5.apk",
          "packageName": "com.maifeipin.webmusic.debug",
          "changelog": "- 新增应用内更新",
          "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "forceUpdate": false,
          "minSupportedVersionCode": 3,
          "publishedAt": "2026-10-23T10:00:00+08:00"
        }
      }
    }
    """;

    [Theory]
    [InlineData(null, "release")]
    [InlineData("", "release")]
    [InlineData("  ", "release")]
    [InlineData("DEBUG", "debug")]
    [InlineData(" release ", "release")]
    [InlineData("../etc", "release")]
    [InlineData("a/b", "release")]
    [InlineData("a.b", "release")]
    [InlineData("debug;rm -rf", "release")]
    [InlineData("debug_2-x", "debug_2-x")]
    public void NormalizeChannel_BlocksInjection(string? input, string expected)
    {
        Assert.Equal(expected, AppReleaseStore.NormalizeChannel(input));
    }

    [Fact]
    public void GetVersion_ReportsUpdate_WhenManifestIsNewer()
    {
        PublishApk("debug", "webmusic-debug-5.apk", 2048);
        WriteManifest(DebugManifest);

        var result = CreateController(CreateStore()).GetVersion(currentCode: 4, channel: "debug", packageName: null);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AppVersionResponse>(ok.Value);
        Assert.True(payload.HasUpdate);
        Assert.Equal(5, payload.VersionCode);
        Assert.Equal("1.1.0", payload.VersionName);
        Assert.Equal("/api/app/download/debug/5", payload.DownloadUrl);
        Assert.Equal(2048, payload.SizeBytes);
        Assert.Equal("com.maifeipin.webmusic.debug", payload.PackageName);
        Assert.False(payload.ForceUpdate);
    }

    [Fact]
    public void GetVersion_NoUpdate_WhenClientIsCurrent()
    {
        PublishApk("debug", "webmusic-debug-5.apk");
        WriteManifest(DebugManifest);

        var result = CreateController(CreateStore()).GetVersion(currentCode: 5, channel: "debug", packageName: null);

        var payload = Assert.IsType<AppVersionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(payload.HasUpdate);
        Assert.Null(payload.DownloadUrl);
    }

    [Fact]
    public void GetVersion_ForcesUpdate_WhenClientBelowMinSupported()
    {
        PublishApk("debug", "webmusic-debug-5.apk");
        WriteManifest(DebugManifest);

        var result = CreateController(CreateStore()).GetVersion(currentCode: 2, channel: "debug", packageName: null);

        var payload = Assert.IsType<AppVersionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(payload.HasUpdate);
        Assert.True(payload.ForceUpdate);
    }

    [Fact]
    public void GetVersion_NoUpdate_WhenApkFileMissing()
    {
        // 清单声明 v5，但目录里没有 APK：不能诱导客户端下载
        WriteManifest(DebugManifest);

        var result = CreateController(CreateStore()).GetVersion(currentCode: 1, channel: "debug", packageName: null);

        var payload = Assert.IsType<AppVersionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(payload.HasUpdate);
        Assert.Null(payload.DownloadUrl);
        Assert.Equal(0, payload.VersionCode);
    }

    [Fact]
    public void GetVersion_NoUpdate_WhenChannelNotPublished()
    {
        var result = CreateController(CreateStore()).GetVersion(currentCode: 1, channel: "beta", packageName: null);

        var payload = Assert.IsType<AppVersionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(payload.HasUpdate);
        Assert.Equal("beta", payload.Channel);
    }

    [Fact]
    public void ResolveApkPath_RejectsTraversalAndWrongExtension()
    {
        var store = CreateStore();
        PublishApk("debug", "webmusic-debug-5.apk");
        File.WriteAllBytes(Path.Combine(_root, "evil.apk"), new byte[8]);

        Assert.NotNull(store.ResolveApkPath("debug", new AppReleaseEntry { FileName = "webmusic-debug-5.apk" }));
        Assert.Null(store.ResolveApkPath("debug", new AppReleaseEntry { FileName = "../evil.apk" }));
        Assert.Null(store.ResolveApkPath("debug", new AppReleaseEntry { FileName = "manifest.json" }));
        Assert.Null(store.ResolveApkPath("debug", new AppReleaseEntry { FileName = "not-there.apk" }));
        Assert.Null(store.ResolveApkPath("debug", new AppReleaseEntry { FileName = "" }));
    }

    [Fact]
    public void Manifest_IsReloaded_AfterFileReplacement()
    {
        var store = CreateStore();
        PublishApk("debug", "webmusic-debug-5.apk");
        PublishApk("debug", "webmusic-debug-6.apk");

        var stamp = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
        WriteManifest(DebugManifest, stamp);
        Assert.Equal(5, store.GetChannel("debug")!.VersionCode);

        WriteManifest(DebugManifest.Replace("\"versionCode\": 5", "\"versionCode\": 6")
            .Replace("webmusic-debug-5.apk", "webmusic-debug-6.apk"), stamp.AddMinutes(1));

        Assert.Equal(6, store.GetChannel("debug")!.VersionCode);
    }

    [Fact]
    public void Manifest_BrokenJson_DoesNotThrow()
    {
        var store = CreateStore();
        WriteManifest("{ this is not json");

        Assert.Empty(store.GetChannels());
        Assert.Null(store.GetChannel("debug"));
    }

    [Fact]
    public void Download_StreamsApkWithRangeSupport()
    {
        PublishApk("debug", "webmusic-debug-5.apk", 4096);
        WriteManifest(DebugManifest);

        var result = CreateController(CreateStore()).Download("debug");

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(AppReleaseStore.ApkContentType, file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        Assert.EndsWith("webmusic-debug-5.apk", file.FileName);
    }

    [Fact]
    public void Download_RejectsStaleVersionInsteadOfServingDifferentBytes()
    {
        PublishApk("debug", "webmusic-debug-5.apk", 4096);
        WriteManifest(DebugManifest);
        Assert.IsType<NotFoundObjectResult>(CreateController(CreateStore()).Download("debug", 4));
    }

    [Fact]
    public void Download_ReturnsNotFound_ForUnknownChannel()
    {
        var result = CreateController(CreateStore()).Download("nope");
        Assert.IsType<NotFoundObjectResult>(result);
    }
}
