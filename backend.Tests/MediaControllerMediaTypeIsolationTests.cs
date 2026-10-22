using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Controllers;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class MediaControllerMediaTypeIsolationTests
{
    private static async Task<(AppDbContext db, MediaController controller)> CreateSetupAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var source = new ScanSource { Id = 1, Name = "MediaShare", Path = "smb://nas/media" };
        db.ScanSources.Add(source);

        // Add 3 audio songs
        db.MediaFiles.AddRange(
            new MediaFile
            {
                Id = 1,
                ScanSourceId = 1,
                FilePath = "smb://nas/media/songs/track1.mp3",
                Title = "Song One",
                Artist = "Audio Artist",
                Album = "Audio Album",
                Duration = TimeSpan.FromMinutes(3)
            },
            new MediaFile
            {
                Id = 2,
                ScanSourceId = 1,
                FilePath = "smb://nas/media/songs/track2.flac",
                Title = "Song Two",
                Artist = "Audio Artist",
                Album = "Audio Album",
                Duration = TimeSpan.FromMinutes(4)
            },
            new MediaFile
            {
                Id = 3,
                ScanSourceId = 1,
                FilePath = "smb://nas/media/songs/track3.m4a",
                Title = "Song Three",
                Artist = "Audio Artist",
                Album = "Audio Album",
                Duration = TimeSpan.FromMinutes(5)
            },
            // Add 2 video files
            new MediaFile
            {
                Id = 101,
                ScanSourceId = 1,
                FilePath = "smb://nas/media/mv/live_concert.mp4",
                Title = "Live Concert MV",
                Artist = "Video Artist",
                Album = "MV Collection",
                Genre = "MV",
                Duration = TimeSpan.FromMinutes(8)
            },
            new MediaFile
            {
                Id = 102,
                ScanSourceId = 1,
                FilePath = "smb://nas/media/mv/music_video.mkv",
                Title = "Special Music Video",
                Artist = "Video Artist",
                Album = "MV Collection",
                Genre = "MV",
                Duration = TimeSpan.FromMinutes(6)
            }
        );
        await db.SaveChangesAsync();

        var dataService = new DataManagementService(db, NullLogger<DataManagementService>.Instance);
        var pathResolver = new PathResolver();
        var mockSmb = new Mock<ISmbService>();
        var mockShare = new Mock<IShareAccessService>();

        var controller = new MediaController(db, mockSmb.Object, pathResolver, dataService, mockShare.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("sub", "1"),
                        new Claim(ClaimTypes.Role, "User")
                    }, "mock"))
                }
            }
        };

        return (db, controller);
    }

    private static (int total, List<JsonElement> files) ParseResponse(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();

        var total = root.GetProperty("total").GetInt32();
        var filesList = new List<JsonElement>();
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            filesList.Add(item.Clone());
        }
        return (total, filesList);
    }

    [Fact]
    public async Task GetFiles_Default_IsolatesVideos_ReturnsOnlyAudioFiles()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            // Legacy client calls without mediaType param
            var actionResult = await controller.GetFiles(page: 1, pageSize: 50, mediaType: null);
            var (total, files) = ParseResponse(actionResult.Result!);

            // Must contain ONLY the 3 audio songs, zero videos
            Assert.Equal(3, total);
            Assert.Equal(3, files.Count);
            Assert.All(files, f => Assert.Equal("audio", f.GetProperty("mediaType").GetString()));
            Assert.DoesNotContain(files, f => f.GetProperty("Title").GetString()!.Contains("MV"));
        }
    }

    [Fact]
    public async Task GetFiles_WithMediaTypeVideo_ReturnsOnlyVideoFiles()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            var actionResult = await controller.GetFiles(page: 1, pageSize: 50, mediaType: "video");
            var (total, files) = ParseResponse(actionResult.Result!);

            Assert.Equal(2, total);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.Equal("video", f.GetProperty("mediaType").GetString()));
        }
    }

    [Fact]
    public async Task GetFiles_WithMediaTypeAudio_ReturnsOnlyAudioFiles()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            var actionResult = await controller.GetFiles(page: 1, pageSize: 50, mediaType: "audio");
            var (total, files) = ParseResponse(actionResult.Result!);

            Assert.Equal(3, total);
            Assert.Equal(3, files.Count);
            Assert.All(files, f => Assert.Equal("audio", f.GetProperty("mediaType").GetString()));
        }
    }

    [Fact]
    public async Task GetFiles_WithMediaTypeAll_ReturnsBothAudioAndVideo()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            var actionResult = await controller.GetFiles(page: 1, pageSize: 50, mediaType: "all");
            var (total, files) = ParseResponse(actionResult.Result!);

            Assert.Equal(5, total);
            Assert.Equal(5, files.Count);
        }
    }

    [Fact]
    public async Task GetFiles_WithCriteriaTypeVideo_ReturnsOnlyVideoFiles()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            var actionResult = await controller.GetFiles(
                page: 1,
                pageSize: 50,
                criteria: new List<string> { "type:is:video" });
            var (total, files) = ParseResponse(actionResult.Result!);

            Assert.Equal(2, total);
            Assert.All(files, f => Assert.Equal("video", f.GetProperty("mediaType").GetString()));
        }
    }

    [Fact]
    public async Task GetFiles_SearchVideo_FiltersCorrectlyWithinVideos()
    {
        var (db, controller) = await CreateSetupAsync();
        await using (db)
        {
            var actionResult = await controller.GetFiles(
                page: 1,
                pageSize: 50,
                search: "Concert",
                mediaType: "video");
            var (total, files) = ParseResponse(actionResult.Result!);

            Assert.Equal(1, total);
            Assert.Equal("Live Concert MV", files[0].GetProperty("Title").GetString());
            Assert.Equal("video", files[0].GetProperty("mediaType").GetString());
        }
    }
}
