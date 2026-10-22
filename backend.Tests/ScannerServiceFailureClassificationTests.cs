using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class ScannerServiceFailureClassificationTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(OperationCanceledException))]
    public void IsTransientIoException_RecognizesStandardIoExceptions(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(ScannerService.IsTransientIoException(ex));
    }

    [Fact]
    public void IsTransientIoException_RecognizesSmbTimeoutMessage()
    {
        var ex = new Exception("SMB Read failed: STATUS_IO_TIMEOUT");
        Assert.True(ScannerService.IsTransientIoException(ex));
    }

    [Fact]
    public void IsTransientIoException_RecognizesNestedIoException()
    {
        var ex = new InvalidOperationException("Failed outer wrapper", new IOException("Connection reset by peer"));
        Assert.True(ScannerService.IsTransientIoException(ex));
    }

    [Fact]
    public void IsTransientIoException_RejectsFormatAndTagErrors()
    {
        var corruptEx = new TagLib.CorruptFileException("ID3 tag is truncated");
        var unsuppEx = new TagLib.UnsupportedFormatException("Unknown container atom");
        var argEx = new ArgumentException("Invalid tag size");

        Assert.False(ScannerService.IsTransientIoException(corruptEx));
        Assert.False(ScannerService.IsTransientIoException(unsuppEx));
        Assert.False(ScannerService.IsTransientIoException(argEx));
    }

    [Fact]
    public async Task ScanFileAsync_WhenTransientIoExceptionOccurs_ThrowsAndDoesNotSavePlaceholder()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        await using var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var source = new ScanSource { Id = 1, Name = "SMB Share", Path = "smb://nas/share" };
        db.ScanSources.Add(source);
        await db.SaveChangesAsync();

        var mockSmb = new Mock<ISmbService>();
        // Return a stream whose Read method throws IOException (simulating STATUS_IO_TIMEOUT)
        var failingStream = new Mock<Stream>();
        failingStream.Setup(s => s.CanRead).Returns(true);
        failingStream.Setup(s => s.CanSeek).Returns(true);
        failingStream.Setup(s => s.Length).Returns(1024 * 1024);
        failingStream.Setup(s => s.Position).Returns(0);
        failingStream.Setup(s => s.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new IOException("SMB Read failed: STATUS_IO_TIMEOUT"));
        failingStream.Setup(s => s.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("SMB Read failed: STATUS_IO_TIMEOUT"));

        mockSmb.Setup(s => s.OpenFile(It.IsAny<ScanSource>(), It.IsAny<string>()))
            .Returns(failingStream.Object);

        var service = new ScannerService(
            db,
            mockSmb.Object,
            NullLogger<ScannerService>.Instance,
            new Mock<IServiceScopeFactory>().Object,
            new ScanStateService());

        // Attempt scanning a video file that encounters SMB timeout during read
        await Assert.ThrowsAsync<IOException>(() => service.ScanFileAsync(source, "movies/clip.mp4"));

        // Crucial guarantees: neither committed nor tracked in memory as Added!
        Assert.Equal(0, await db.MediaFiles.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries<MediaFile>());
    }

    [Fact]
    public async Task ScanFileAsync_WhenOnlyHashReadFailsWithIoException_ThrowsAndDoesNotIngestPlaceholder()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        await using var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();

        var source = new ScanSource { Id = 1, Name = "SMB Share", Path = "smb://nas/share" };
        db.ScanSources.Add(source);
        await db.SaveChangesAsync();

        var mockSmb = new Mock<ISmbService>();
        var streamWithFailingHash = new Mock<Stream>();
        streamWithFailingHash.Setup(s => s.CanRead).Returns(true);
        streamWithFailingHash.Setup(s => s.CanSeek).Returns(true);
        streamWithFailingHash.Setup(s => s.Length).Returns(1024 * 1024);
        streamWithFailingHash.Setup(s => s.Position).Returns(0);

        // Fail specifically on ReadAsync (used by ComputePartialHash)
        streamWithFailingHash.Setup(s => s.ReadAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("SMB Read failed: STATUS_IO_TIMEOUT"));
        // Synchronous Read would return 0 (simulating network recovered afterwards)
        streamWithFailingHash.Setup(s => s.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(0);

        mockSmb.Setup(s => s.OpenFile(It.IsAny<ScanSource>(), It.IsAny<string>()))
            .Returns(streamWithFailingHash.Object);

        var service = new ScannerService(
            db,
            mockSmb.Object,
            NullLogger<ScannerService>.Instance,
            new Mock<IServiceScopeFactory>().Object,
            new ScanStateService());

        // Even if only the hash read encounters an IO timeout, must throw and NOT save placeholder
        await Assert.ThrowsAsync<IOException>(() => service.ScanFileAsync(source, "movies/clip.mp4"));

        Assert.Equal(0, await db.MediaFiles.CountAsync());
        Assert.Empty(db.ChangeTracker.Entries<MediaFile>());
    }
}
