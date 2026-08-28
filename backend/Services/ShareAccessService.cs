using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace WebMusic.Backend.Services;

public interface IShareAccessService
{
    string CreateTicket(string shareToken);
    bool HasValidTicket(HttpRequest request, string shareToken);
    void Grant(HttpResponse response, string shareToken);
}

public class ShareAccessService : IShareAccessService
{
    private const string CookiePrefix = "webmusic_share_";
    private readonly IDataProtector _protector;

    public ShareAccessService(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("WebMusic.SharedPlaylist.Access.v1");

    public string CreateTicket(string shareToken) => _protector.Protect($"{shareToken}|{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()}");

    public bool HasValidTicket(HttpRequest request, string shareToken)
    {
        var ticket = request.Cookies[CookiePrefix + shareToken];
        if (string.IsNullOrEmpty(ticket)) return false;
        try
        {
            var parts = _protector.Unprotect(ticket).Split('|');
            return parts.Length == 2 && parts[0] == shareToken && long.TryParse(parts[1], out var expiresAt) &&
                   DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= expiresAt;
        }
        catch (CryptographicException) { return false; }
    }

    public void Grant(HttpResponse response, string shareToken) => response.Cookies.Append(CookiePrefix + shareToken, CreateTicket(shareToken), new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromHours(8),
        Path = "/"
    });
}
