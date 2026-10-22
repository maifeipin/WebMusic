using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class MediaFileFilterTests
{
    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("track.FLAC", true)]
    [InlineData("audio.m4a", true)]
    [InlineData("sound.wav", true)]
    [InlineData("voice.ogg", true)]
    [InlineData("music.opus", true)]
    [InlineData("video.mp4", false)]
    [InlineData("movie.mkv", false)]
    [InlineData("notes.txt", false)]
    [InlineData(".DS_Store", false)]
    [InlineData("._song.mp3", false)]
    public void IsSupportedAudioFile_FiltersCorrectly(string filename, bool expected)
    {
        Assert.Equal(expected, MediaFileFilter.IsSupportedAudioFile(filename));
    }

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("movie.MKV", true)]
    [InlineData("clip.webm", true)]
    [InlineData("record.avi", true)]
    [InlineData("film.mov", true)]
    [InlineData("stream.flv", true)]
    [InlineData("segment.ts", true)]
    [InlineData("media.m4v", true)]
    [InlineData("song.mp3", false)]
    [InlineData("track.flac", false)]
    [InlineData(".DS_Store", false)]
    [InlineData("._video.mp4", false)]
    public void IsSupportedVideoFile_FiltersCorrectly(string filename, bool expected)
    {
        Assert.Equal(expected, MediaFileFilter.IsSupportedVideoFile(filename));
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("video.mp4", true)]
    [InlineData("movie.mkv", true)]
    [InlineData("text.txt", false)]
    [InlineData("archive.zip", false)]
    [InlineData("._video.mp4", false)]
    public void IsSupportedMediaFile_AcceptsBothAudioAndVideo(string filename, bool expected)
    {
        Assert.Equal(expected, MediaFileFilter.IsSupportedMediaFile(filename));
    }

    [Theory]
    [InlineData("song.mp3", "audio")]
    [InlineData("track.flac", "audio")]
    [InlineData("video.mp4", "video")]
    [InlineData("movie.MKV", "video")]
    [InlineData("clip.webm", "video")]
    [InlineData("film.mov", "video")]
    [InlineData("document.pdf", "unknown")]
    public void GetMediaType_ReturnsCorrectType(string filename, string expected)
    {
        Assert.Equal(expected, MediaFileFilter.GetMediaType(filename));
    }

    [Theory]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("track.flac", "audio/flac")]
    [InlineData("audio.wav", "audio/wav")]
    [InlineData("voice.ogg", "audio/ogg")]
    [InlineData("voice.opus", "audio/opus")]
    [InlineData("voice.m4a", "audio/mp4")]
    [InlineData("video.mp4", "video/mp4")]
    [InlineData("video.m4v", "video/mp4")]
    [InlineData("movie.mkv", "video/x-matroska")]
    [InlineData("web.webm", "video/webm")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("record.avi", "video/x-msvideo")]
    [InlineData("stream.flv", "video/x-flv")]
    [InlineData("stream.ts", "video/mp2t")]
    [InlineData("unknown.xyz", "application/octet-stream")]
    public void GetMimeType_ResolvesExpectedMime(string filename, string expectedMime)
    {
        Assert.Equal(expectedMime, MediaFileFilter.GetMimeType(filename));
    }

    [Fact]
    public void MediaFile_Model_EvaluatesMediaTypeProperty()
    {
        var audioMedia = new MediaFile { FilePath = "smb://nas/music/test.flac" };
        var videoMedia = new MediaFile { FilePath = "smb://nas/video/mv.mp4" };
        var unknownMedia = new MediaFile { FilePath = "smb://nas/misc/doc.dat" };

        Assert.Equal("audio", audioMedia.MediaType);
        Assert.Equal("video", videoMedia.MediaType);
        Assert.Equal("unknown", unknownMedia.MediaType);
    }
}
