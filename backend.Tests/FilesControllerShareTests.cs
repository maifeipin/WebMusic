using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Controllers;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class FilesControllerShareTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static FilesController CreateController(
        AppDbContext db,
        ISmbService? smbService = null,
        ClaimsPrincipal? user = null)
    {
        var smbMock = smbService ?? Mock.Of<ISmbService>();
        var scanner = new ScannerService(
            db,
            smbMock,
            NullLogger<ScannerService>.Instance,
            Mock.Of<IServiceScopeFactory>(),
            new ScanStateService());
        var provider = DataProtectionProvider.Create("WebMusic.Tests");
        var controller = new FilesController(
            db,
            smbMock,
            scanner,
            NullLogger<FilesController>.Instance,
            provider);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
            }
        };
        return controller;
    }

    private static ClaimsPrincipal AuthenticatedUser(int userId = 1, string role = "User")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        }, "Bearer");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task Download_WithoutAuthAndWithoutShareToken_ReturnsUnauthorized()
    {
        using var db = CreateInMemoryDb();
        var controller = CreateController(db);

        var result = await controller.Download(18, "book.rar");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task CreateShareLink_ThenAnonymousDownload_Succeeds()
    {
        using var db = CreateInMemoryDb();
        db.ScanSources.Add(new ScanSource
        {
            Id = 18,
            Name = "TestSource",
            Path = "smb://nas/share",
            UserId = null
        });
        await db.SaveChangesAsync();

        var smbMock = new Mock<ISmbService>();
        smbMock.Setup(s => s.OpenFile(It.IsAny<ScanSource>(), "book.rar"))
            .Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("rar-content")));

        var authedController = CreateController(db, smbMock.Object, AuthenticatedUser());
        var shareResult = await authedController.CreateShareLink(new FilesController.CreateFileShareRequest(18, "book.rar"));
        var okResult = Assert.IsType<OkObjectResult>(shareResult);
        var shareToken = okResult.Value!.GetType().GetProperty("shareToken")!.GetValue(okResult.Value)!.ToString()!;

        // Anonymous request with valid shareToken should succeed
        var anonController = CreateController(db, smbMock.Object);
        var downloadResult = await anonController.Download(18, "book.rar", shareToken);
        var fileResult = Assert.IsType<FileStreamResult>(downloadResult);
        using var reader = new StreamReader(fileResult.FileStream);
        Assert.Equal("rar-content", await reader.ReadToEndAsync());

        // Mismatched path or tampered token should be rejected
        var wrongPath = await anonController.Download(18, "other.rar", shareToken);
        Assert.IsType<UnauthorizedObjectResult>(wrongPath);

        var tampered = await anonController.Download(18, "book.rar", shareToken + "x");
        Assert.IsType<UnauthorizedObjectResult>(tampered);
    }
}
