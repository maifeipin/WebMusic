using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class LocalMusicBrainzServiceTests
{
    private readonly Mock<ILogger<LocalMusicBrainzService>> _loggerMock = new();
    private readonly IConfiguration _configuration;

    public LocalMusicBrainzServiceTests()
    {
        var configValues = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["MusicBrainz:LocalBaseUrl"] = "http://192.168.2.18:5050"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
    }

    [Fact]
    public void CalculateConfidence_ReturnsHighScore_ForExactMatch()
    {
        var confidence = LocalMusicBrainzService.CalculateConfidence(
            targetTitle: "Yesterday",
            targetArtist: "The Beatles",
            targetDuration: TimeSpan.FromSeconds(125),
            candidateTitle: "Yesterday",
            candidateArtist: "The Beatles",
            candidateDuration: TimeSpan.FromSeconds(125),
            isDerivative: false
        );

        Assert.True(confidence >= 0.95);
    }

    [Fact]
    public void CalculateConfidence_Penalizes_UnintendedDerivatives()
    {
        var standardConf = LocalMusicBrainzService.CalculateConfidence(
            targetTitle: "Yesterday",
            targetArtist: "The Beatles",
            targetDuration: TimeSpan.FromSeconds(125),
            candidateTitle: "Yesterday",
            candidateArtist: "The Beatles",
            candidateDuration: TimeSpan.FromSeconds(125),
            isDerivative: false
        );

        var derivativeConf = LocalMusicBrainzService.CalculateConfidence(
            targetTitle: "Yesterday",
            targetArtist: "The Beatles",
            targetDuration: TimeSpan.FromSeconds(125),
            candidateTitle: "Yesterday (Live in Tokyo)",
            candidateArtist: "The Beatles",
            candidateDuration: TimeSpan.FromSeconds(125),
            isDerivative: true
        );

        Assert.True(standardConf > derivativeConf);
    }

    [Fact]
    public void IsDerivativeOrClip_DetectsRemixAndShortAudio()
    {
        var isClip = LocalMusicBrainzService.IsDerivativeOrClip(
            "Song Preview",
            "Song",
            null,
            TimeSpan.FromSeconds(30)
        );

        var isRemix = LocalMusicBrainzService.IsDerivativeOrClip(
            "Original Track",
            "Original Track (Club Remix)",
            "remix",
            TimeSpan.FromMinutes(3)
        );

        Assert.True(isClip);
        Assert.True(isRemix);
    }

    [Fact]
    public async Task SearchRecordingAsync_ReturnsBestCandidate_WhenApiReturnsRecordings()
    {
        var jsonResponse = @"
        {
            ""recordings"": [
                {
                    ""id"": ""e0a4714e-6e46-4c49-be7d-304bfaef03f0"",
                    ""title"": ""Yesterday"",
                    ""length"": 125000,
                    ""artist-credit"": [
                        {
                            ""name"": ""The Beatles"",
                            ""artist"": { ""id"": ""b10bbbfc-cf9e-42e0-be56-ac2449102c58"" }
                        }
                    ],
                    ""releases"": [
                        { ""id"": ""4e97669d-2101-4475-b467-3faee8061fb7"" }
                    ]
                }
            ]
        }";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });

        var client = new HttpClient(handlerMock.Object);
        var service = new LocalMusicBrainzService(client, _configuration, _loggerMock.Object);

        var result = await service.SearchRecordingAsync("Yesterday", "The Beatles", TimeSpan.FromSeconds(125));

        Assert.True(result.Matched);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("e0a4714e-6e46-4c49-be7d-304bfaef03f0", result.BestCandidate.RecordingId);
        Assert.Equal("4e97669d-2101-4475-b467-3faee8061fb7", result.BestCandidate.ReleaseId);
        Assert.True(result.BestCandidate.Confidence >= 0.85);
    }

    [Fact]
    public async Task SearchRecordingAsync_ReturnsUnmatched_WhenNoRecordingsFound()
    {
        var jsonResponse = @"{ ""recordings"": [] }";

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });

        var client = new HttpClient(handlerMock.Object);
        var service = new LocalMusicBrainzService(client, _configuration, _loggerMock.Object);

        var result = await service.SearchRecordingAsync("CompletelyUnknownSong12345", "UnknownArtist", TimeSpan.FromMinutes(3));

        Assert.False(result.Matched);
        Assert.Null(result.BestCandidate);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task ScanMediaIdentityAsync_SkipsUnknownMetadata()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var client = new HttpClient(handlerMock.Object);
        var service = new LocalMusicBrainzService(client, _configuration, _loggerMock.Object);

        var media = new MediaFile
        {
            Id = 99,
            Title = "Unknown Track",
            Artist = "Unknown Artist",
            Duration = TimeSpan.FromMinutes(3)
        };

        var result = await service.ScanMediaIdentityAsync(media);

        Assert.False(result.Matched);
        Assert.Null(result.BestCandidate);
        Assert.Contains("Incomplete or unknown", result.ErrorDetail);
    }

    [Theory]
    [InlineData("http://192.168.2.18:5050")]
    [InlineData("http://127.0.0.1:5050")]
    [InlineData("http://[::1]:5050")]
    [InlineData("http://10.0.0.1:5050")]
    [InlineData("http://172.16.0.1:5050")]
    [InlineData("http://172.31.255.255:5050")]
    [InlineData("http://100.64.0.1:5050")]
    [InlineData("http://100.127.255.255:5050")]
    [InlineData("http://100.91.3.53:5050")]
    public void ValidatePrivateOrLoopbackEndpoint_AcceptsValidIpLiterals(string url)
    {
        // Should not throw for explicit private/loopback/Tailscale IP literals
        LocalMusicBrainzService.ValidatePrivateOrLoopbackEndpoint(url);
    }

    [Theory]
    [InlineData("http://localhost:5050")]
    [InlineData("http://dsm.local:5050")]
    [InlineData("http://synology.local:5050")]
    [InlineData("https://musicbrainz.org")]
    [InlineData("http://public-api.example.com")]
    public void ValidatePrivateOrLoopbackEndpoint_RejectsHostnamesToPreventDnsRebinding(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalMusicBrainzService.ValidatePrivateOrLoopbackEndpoint(url));

        Assert.Contains("Hostnames", ex.Message);
        Assert.Contains("eliminate DNS rebinding risks", ex.Message);
    }

    [Theory]
    [InlineData("http://8.8.8.8:5050")]
    [InlineData("http://1.1.1.1:5050")]
    [InlineData("http://172.32.0.1:5050")]
    [InlineData("http://100.128.0.1:5050")]
    public void ValidatePrivateOrLoopbackEndpoint_RejectsPublicIpLiterals(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalMusicBrainzService.ValidatePrivateOrLoopbackEndpoint(url));

        Assert.Contains("public or unauthorized IP", ex.Message);
    }

    [Fact]
    public async Task ConnectCallback_BlocksHostnamesAndPublicIps()
    {
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> callback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                host = host[1..^1];
            }

            if (!IPAddress.TryParse(host, out var ip) || !LocalMusicBrainzService.IsPrivateOrLoopbackIp(ip))
            {
                throw new InvalidOperationException($"Outbound connection to '{context.DnsEndPoint.Host}' is strictly prohibited.");
            }

            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = callback
        };

        using var client = new HttpClient(handler);

        // Hostname attempt should be blocked at connect time (wrapped in HttpRequestException)
        var hostnameEx = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync("http://synology.local:5050/ws/2/recording"));
        Assert.NotNull(hostnameEx.InnerException);
        Assert.IsType<InvalidOperationException>(hostnameEx.InnerException);
        Assert.Contains("Outbound connection to 'synology.local' is strictly prohibited", hostnameEx.InnerException.Message);

        // Public IP attempt should be blocked at connect time (wrapped in HttpRequestException)
        var publicIpEx = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync("http://8.8.8.8:5050/ws/2/recording"));
        Assert.NotNull(publicIpEx.InnerException);
        Assert.IsType<InvalidOperationException>(publicIpEx.InnerException);
        Assert.Contains("Outbound connection to '8.8.8.8' is strictly prohibited", publicIpEx.InnerException.Message);
    }

    [Fact]
    public async Task SearchRecordingAsync_WhenNodeReturns302Redirect_DoesNotFollowAndFails()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); // 302
                response.Headers.Location = new Uri("https://musicbrainz.org/ws/2/recording");
                return response;
            });

        var client = new HttpClient(handlerMock.Object);
        var service = new LocalMusicBrainzService(client, _configuration, _loggerMock.Object);

        var result = await service.SearchRecordingAsync("Yesterday", "The Beatles", TimeSpan.FromSeconds(125));

        Assert.False(result.Matched);
        Assert.Null(result.BestCandidate);
        Assert.Equal(302, result.StatusCode);
        Assert.Contains("Redirects are strictly prohibited", result.ErrorDetail);
        Assert.Contains("https://musicbrainz.org/ws/2/recording", result.ErrorDetail);
    }

    [Fact]
    public async Task SearchRecordingAsync_Returns408_WhenLocalNodeTimesOut()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns(async (HttpRequestMessage req, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var client = new HttpClient(handlerMock.Object);
        var service = new LocalMusicBrainzService(client, _configuration, _loggerMock.Object);

        var result = await service.SearchRecordingAsync("Yesterday", "The Beatles", TimeSpan.FromSeconds(125));

        Assert.False(result.Matched);
        Assert.Equal(408, result.StatusCode);
        Assert.Contains("timed out", result.ErrorDetail);
    }
}
