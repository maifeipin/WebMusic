using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record LocalMusicBrainzCandidate(
    string RecordingId,
    string? ReleaseId,
    string? ArtistId,
    string MatchedTitle,
    string MatchedArtist,
    TimeSpan MatchedDuration,
    string? Disambiguation,
    double Confidence,
    bool IsDerivativeOrClip
);

public record LocalMusicBrainzScanResult(
    bool Matched,
    LocalMusicBrainzCandidate? BestCandidate,
    int StatusCode,
    string? ErrorDetail = null,
    long ElapsedMs = 0
);

public interface ILocalMusicBrainzService
{
    string BaseUrl { get; }
    Task<LocalMusicBrainzScanResult> SearchRecordingAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default
    );

    Task<LocalMusicBrainzScanResult> ScanMediaIdentityAsync(
        MediaFile media,
        CancellationToken cancellationToken = default
    );
}

public class LocalMusicBrainzService : ILocalMusicBrainzService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalMusicBrainzService> _logger;
    private readonly string _baseUrl;

    public const string ProviderName = "MusicBrainzLocal";
    public const string MatchMethodV1 = "LocalMetadataFuzzy:v1";
    public const double HighConfidenceThreshold = 0.85;
    public const double ProposedConfidenceThreshold = 0.70;

    public string BaseUrl => _baseUrl;

    public LocalMusicBrainzService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<LocalMusicBrainzService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        // DSM private network local endpoint (e.g., http://192.168.2.18:5050)
        _baseUrl = (configuration["MusicBrainz:LocalBaseUrl"] ?? "http://192.168.2.18:5050").TrimEnd('/');
        ValidatePrivateOrLoopbackEndpoint(_baseUrl);
    }

    public static void ValidatePrivateOrLoopbackEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("MusicBrainz:LocalBaseUrl must not be empty.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL format for MusicBrainz:LocalBaseUrl: '{url}'", nameof(url));
        }

        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host.Substring(1, host.Length - 2);
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            throw new InvalidOperationException(
                $"Hostnames ('{uri.Host}') are not permitted for MusicBrainz:LocalBaseUrl to eliminate DNS rebinding risks. " +
                "Only explicit private or loopback IP literals (e.g. 192.168.2.18, 127.0.0.1, 100.91.3.53, ::1) are allowed."
            );
        }

        if (!IsPrivateOrLoopbackIp(ip))
        {
            throw new InvalidOperationException(
                $"MusicBrainz:LocalBaseUrl '{url}' resolves to public or unauthorized IP '{ip}'. Only private (RFC1918), loopback (127.0.0.0/8, ::1), or Tailscale (100.64.0.0/10) addresses are allowed to prevent outbound public API requests."
            );
        }
    }

    public static bool IsPrivateOrLoopbackIp(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // RFC 1918: 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // RFC 1918: 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // RFC 1918: 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // Tailscale / CGNAT: 100.64.0.0/10 (100.64.0.0 - 100.127.255.255)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || System.Net.IPAddress.IsLoopback(ip)) return true;
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7
        }

        return false;
    }

    public async Task<LocalMusicBrainzScanResult> ScanMediaIdentityAsync(
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist) ||
            media.Title.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ||
            media.Artist.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalMusicBrainzScanResult(false, null, 200, "Incomplete or unknown title/artist metadata.");
        }

        return await SearchRecordingAsync(media.Title, media.Artist, media.Duration, cancellationToken);
    }

    private static readonly Regex VersionTagRegex = new Regex(
        @"\s*[\(\[](?:(?:album|single|radio|original|official|deluxe|bonus|explicit|clean|anniversary)\s+(?:version|edit|mix|track|edition|video|audio)|remaster(?:ed)?(?:\s+\d{4})?|\d{4}\s+remaster(?:ed)?|explicit|clean|remaster(?:ed)?|bonus track|deluxe|radio edit|album version)[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static string NormalizePunctuation(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return input.Trim()
            .Replace('（', '(').Replace('）', ')')
            .Replace('【', '[').Replace('】', ']')
            .Replace('｛', '{').Replace('｝', '}')
            .Replace('，', ',').Replace('、', ',')
            .Replace('：', ':').Replace('；', ';')
            .Replace('“', '"').Replace('”', '"')
            .Replace('‘', '\'').Replace('’', '\'')
            .Replace('～', '~').Replace('—', '-')
            .Replace('\u3000', ' ');
    }

    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var normalized = NormalizePunctuation(title);
        var stripped = VersionTagRegex.Replace(normalized, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? normalized : stripped;
    }

    public static string ExtractPrimaryArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        var cleaned = NormalizePunctuation(artist);

        // Split featuring patterns
        var featSplit = Regex.Split(cleaned, @"\s+(?:feat\.?|featuring|ft\.?)\s+", RegexOptions.IgnoreCase);
        if (featSplit.Length > 1 && !string.IsNullOrWhiteSpace(featSplit[0]))
        {
            cleaned = featSplit[0].Trim();
        }

        // Split separators: & , /
        var sepIdx = cleaned.IndexOfAny(new[] { '&', '/', ',' });
        if (sepIdx > 0)
        {
            var first = cleaned.Substring(0, sepIdx).Trim();
            if (!string.IsNullOrWhiteSpace(first))
            {
                cleaned = first;
            }
        }

        return cleaned.Trim();
    }

    private static readonly Dictionary<char, char> SimpToTradMap = new Dictionary<char, char>
    {
        {'万', '萬'}, {'与', '與'}, {'专', '專'}, {'东', '東'}, {'两', '兩'}, {'严', '嚴'}, {'个', '個'},
        {'临', '臨'}, {'为', '為'}, {'丽', '麗'}, {'么', '麼'}, {'义', '義'}, {'乌', '烏'}, {'乐', '樂'},
        {'乔', '喬'}, {'习', '習'}, {'乡', '鄉'}, {'书', '書'}, {'买', '買'}, {'乱', '亂'}, {'争', '爭'},
        {'于', '於'}, {'云', '雲'}, {'亲', '親'}, {'亿', '億'}, {'从', '從'}, {'仪', '儀'}, {'们', '們'},
        {'价', '價'}, {'众', '眾'}, {'优', '優'}, {'会', '會'}, {'伟', '偉'}, {'传', '傳'}, {'伤', '傷'},
        {'伦', '倫'}, {'伪', '偽'}, {'体', '體'}, {'余', '餘'}, {'党', '黨'}, {'兰', '蘭'}, {'关', '關'},
        {'兴', '興'}, {'内', '內'}, {'写', '寫'}, {'农', '農'}, {'冯', '馮'}, {'准', '準'}, {'凤', '鳳'},
        {'凯', '凱'}, {'刘', '劉'}, {'刚', '剛'}, {'创', '創'}, {'别', '別'}, {'制', '製'}, {'劝', '勸'},
        {'动', '動'}, {'势', '勢'}, {'勋', '勳'}, {'医', '醫'}, {'华', '華'}, {'单', '單'}, {'卖', '賣'},
        {'卢', '盧'}, {'厅', '廳'}, {'历', '歷'}, {'压', '壓'}, {'双', '雙'}, {'发', '發'}, {'变', '變'},
        {'只', '隻'}, {'台', '臺'}, {'叶', '葉'}, {'号', '號'}, {'后', '後'}, {'听', '聽'}, {'吴', '吳'},
        {'员', '員'}, {'嘱', '囑'}, {'团', '團'}, {'围', '圍'}, {'国', '國'}, {'图', '圖'}, {'坏', '壞'},
        {'墙', '牆'}, {'壮', '壯'}, {'声', '聲'}, {'复', '複'}, {'够', '夠'}, {'头', '頭'}, {'奖', '獎'},
        {'妇', '婦'}, {'妈', '媽'}, {'孙', '孫'}, {'学', '學'}, {'宁', '寧'}, {'宝', '寶'}, {'实', '實'},
        {'对', '對'}, {'寻', '尋'}, {'导', '導'}, {'寿', '壽'}, {'将', '將'}, {'岁', '歲'}, {'师', '師'},
        {'带', '帶'}, {'干', '乾'}, {'广', '廣'}, {'庄', '莊'}, {'庆', '慶'}, {'开', '開'}, {'异', '異'},
        {'弃', '棄'}, {'张', '張'}, {'强', '強'}, {'归', '歸'}, {'当', '當'}, {'录', '錄'}, {'忆', '憶'},
        {'怀', '懷'}, {'态', '態'}, {'总', '總'}, {'恋', '戀'}, {'愿', '願'}, {'戏', '戲'}, {'战', '戰'},
        {'拥', '擁'}, {'挣', '掙'}, {'损', '損'}, {'换', '換'}, {'摇', '搖'}, {'数', '數'}, {'断', '斷'},
        {'无', '無'}, {'时', '時'}, {'晓', '曉'}, {'暂', '暫'}, {'机', '機'}, {'杀', '殺'}, {'权', '權'},
        {'条', '條'}, {'来', '來'}, {'杨', '楊'}, {'杰', '傑'}, {'极', '極'}, {'构', '構'}, {'标', '標'},
        {'树', '樹'}, {'样', '樣'}, {'桥', '橋'}, {'梦', '夢'}, {'楼', '樓'}, {'欢', '歡'}, {'欧', '歐'},
        {'气', '氣'}, {'汉', '漢'}, {'汤', '湯'}, {'没', '沒'}, {'济', '濟'}, {'涨', '漲'}, {'温', '溫'},
        {'游', '遊'}, {'满', '滿'}, {'灯', '燈'}, {'点', '點'}, {'烟', '煙'}, {'热', '熱'}, {'爱', '愛'},
        {'牵', '牽'}, {'状', '狀'}, {'猫', '貓'}, {'环', '環'}, {'电', '電'}, {'画', '畫'}, {'盖', '蓋'},
        {'盘', '盤'}, {'砖', '磚'}, {'离', '離'}, {'积', '積'}, {'称', '稱'}, {'穷', '窮'}, {'简', '簡'},
        {'类', '類'}, {'红', '紅'}, {'约', '約'}, {'纱', '紗'}, {'线', '線'}, {'组', '組'}, {'细', '細'},
        {'终', '終'}, {'经', '經'}, {'结', '結'}, {'给', '給'}, {'继', '繼'}, {'绪', '緒'}, {'续', '續'},
        {'维', '維'}, {'网', '網'}, {'罗', '羅'}, {'职', '職'}, {'联', '聯'}, {'肃', '肅'}, {'胜', '勝'},
        {'脑', '腦'}, {'脱', '脫'}, {'腾', '騰'}, {'艺', '藝'}, {'荣', '榮'}, {'药', '藥'}, {'萧', '蕭'},
        {'蓝', '藍'}, {'装', '裝'}, {'见', '見'}, {'观', '觀'}, {'规', '規'}, {'视', '視'}, {'觉', '覺'},
        {'认', '認'}, {'让', '讓'}, {'议', '議'}, {'讯', '訊'}, {'记', '記'}, {'讲', '講'}, {'许', '許'},
        {'论', '論'}, {'设', '設'}, {'评', '評'}, {'识', '識'}, {'诉', '訴'}, {'试', '試'}, {'诗', '詩'},
        {'话', '話'}, {'该', '該'}, {'语', '語'}, {'误', '誤'}, {'说', '說'}, {'请', '請'}, {'读', '讀'},
        {'调', '調'}, {'谈', '談'}, {'谢', '謝'}, {'谦', '謙'}, {'谭', '譚'}, {'贝', '貝'}, {'贤', '賢'},
        {'账', '賬'}, {'质', '質'}, {'贫', '貧'}, {'购', '購'}, {'资', '資'}, {'赏', '賞'}, {'赞', '贊'},
        {'赢', '贏'}, {'赵', '趙'}, {'车', '車'}, {'轩', '軒'}, {'转', '轉'}, {'轮', '輪'}, {'轻', '輕'},
        {'辉', '輝'}, {'辞', '辭'}, {'边', '邊'}, {'辽', '遼'}, {'达', '達'}, {'迁', '遷'}, {'过', '過'},
        {'运', '運'}, {'还', '還'}, {'这', '這'}, {'进', '進'}, {'远', '遠'}, {'连', '連'}, {'适', '適'},
        {'选', '選'}, {'遥', '遙'}, {'邓', '鄧'}, {'邮', '郵'}, {'郑', '鄭'}, {'里', '裡'}, {'针', '針'},
        {'钟', '鍾'}, {'钢', '鋼'}, {'钱', '錢'}, {'铁', '鐵'}, {'铅', '鉛'}, {'银', '銀'}, {'销', '銷'},
        {'长', '長'}, {'门', '門'}, {'闪', '閃'}, {'问', '問'}, {'间', '間'}, {'闹', '鬧'}, {'闻', '聞'},
        {'阳', '陽'}, {'阴', '陰'}, {'阵', '陣'}, {'陆', '陸'}, {'陈', '陳'}, {'隐', '隱'}, {'难', '難'},
        {'雾', '霧'}, {'静', '靜'}, {'顺', '順'}, {'须', '須'}, {'顾', '顧'}, {'预', '預'}, {'领', '領'},
        {'频', '頻'}, {'颖', '穎'}, {'题', '題'}, {'颜', '顏'}, {'风', '風'}, {'飞', '飛'}, {'饰', '飾'},
        {'马', '馬'}, {'驶', '駛'}, {'验', '驗'}, {'骑', '騎'}, {'鱼', '魚'}, {'鲁', '魯'}, {'鸟', '鳥'},
        {'鹰', '鷹'}, {'黄', '黃'}, {'齐', '齊'}, {'龙', '龍'}
    };

    public static string ToTraditionalChinese(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (SimpToTradMap.TryGetValue(c, out var trad))
                sb.Append(trad);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<(int StatusCode, string? Error, List<LocalMusicBrainzCandidate> Candidates)> QueryCandidatesAsync(
        string query,
        string targetTitle,
        string targetArtist,
        TimeSpan targetDuration,
        CancellationToken cancellationToken)
    {
        var candidates = new List<LocalMusicBrainzCandidate>();
        var requestUrl = $"{_baseUrl}/ws/2/recording/?fmt=json&limit=25&query={Uri.EscapeDataString(query)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WebMusic-LocalScanner", "2.0"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (statusCode >= 300 && statusCode < 400)
            {
                var redirectLocation = response.Headers.Location?.ToString() ?? "unknown";
                _logger.LogWarning("Local MusicBrainz node returned unexpected redirect HTTP {StatusCode} to {Location}. Redirects are strictly prohibited.", statusCode, redirectLocation);
                return (statusCode, $"Local MusicBrainz node returned redirect HTTP {statusCode} to {redirectLocation}. Redirects are strictly prohibited to prevent public internet access.", candidates);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (statusCode, $"Local MusicBrainz node returned HTTP {statusCode}", candidates);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("recordings", out var recordings) || recordings.GetArrayLength() == 0)
            {
                return (200, "No recordings found.", candidates);
            }

            foreach (var item in recordings.EnumerateArray())
            {
                var candidateId = GetString(item, "id");
                var candidateTitle = GetString(item, "title");
                var disambiguation = GetNullableString(item, "disambiguation");

                var candidateArtist = item.TryGetProperty("artist-credit", out var credit)
                    ? string.Join(", ", credit.EnumerateArray().Select(c => GetString(c, "name")))
                    : string.Empty;

                string? artistId = null;
                if (item.TryGetProperty("artist-credit", out var creditArr) && creditArr.GetArrayLength() > 0)
                {
                    var firstArtist = creditArr[0];
                    if (firstArtist.TryGetProperty("artist", out var artistObj))
                    {
                        artistId = GetNullableString(artistObj, "id");
                    }
                }

                var candidateDuration = item.TryGetProperty("length", out var length) && length.TryGetInt64(out var ms)
                    ? TimeSpan.FromMilliseconds(ms)
                    : TimeSpan.Zero;

                string? releaseId = null;
                if (item.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
                {
                    releaseId = GetNullableString(releases[0], "id");
                }

                var isDerivative = IsDerivativeOrClip(targetTitle, candidateTitle, disambiguation, targetDuration);
                var confidence = CalculateConfidence(targetTitle, targetArtist, targetDuration, candidateTitle, candidateArtist, candidateDuration, isDerivative);

                candidates.Add(new LocalMusicBrainzCandidate(
                    candidateId,
                    releaseId,
                    artistId,
                    candidateTitle,
                    candidateArtist,
                    candidateDuration,
                    disambiguation,
                    confidence,
                    isDerivative
                ));
            }

            return (200, null, candidates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (408, "Local MusicBrainz request timed out", candidates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local MusicBrainz request failed for query {Query}", query);
            return (500, ex.Message, candidates);
        }
    }

    public async Task<LocalMusicBrainzScanResult> SearchRecordingAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var cleanTitle = CleanTitle(title);
        var primaryArtist = ExtractPrimaryArtist(artist);
        var rawTitle = title.Trim();
        var rawArtist = artist.Trim();

        var queryList = new List<string>();

        // Strategy 1: Cleaned Title + Primary Artist
        var escCleanTitle = EscapeLucenePhrase(cleanTitle);
        var escPrimaryArtist = EscapeLucenePhrase(primaryArtist);
        queryList.Add($"recording:\"{escCleanTitle}\" AND artist:\"{escPrimaryArtist}\"");

        // Strategy 2: Raw Title + Raw Artist (if different)
        if (!string.Equals(cleanTitle, rawTitle, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(primaryArtist, rawArtist, StringComparison.OrdinalIgnoreCase))
        {
            var escRawTitle = EscapeLucenePhrase(rawTitle);
            var escRawArtist = EscapeLucenePhrase(rawArtist);
            queryList.Add($"recording:\"{escRawTitle}\" AND artist:\"{escRawArtist}\"");
        }

        // Strategy 3: Traditional Chinese query if applicable
        var tradCleanTitle = ToTraditionalChinese(cleanTitle);
        var tradPrimaryArtist = ToTraditionalChinese(primaryArtist);
        if (tradCleanTitle != cleanTitle || tradPrimaryArtist != primaryArtist)
        {
            var escTradTitle = EscapeLucenePhrase(tradCleanTitle);
            var escTradArtist = EscapeLucenePhrase(tradPrimaryArtist);
            queryList.Add($"recording:\"{escTradTitle}\" AND artist:\"{escTradArtist}\"");
        }

        LocalMusicBrainzCandidate? bestOverall = null;
        int lastStatus = 200;
        string? lastError = null;

        foreach (var query in queryList)
        {
            if (timeoutCts.IsCancellationRequested) break;

            var (status, err, candidates) = await QueryCandidatesAsync(query, title, artist, duration, timeoutCts.Token);
            lastStatus = status;
            if (err != null) lastError = err;

            if (status >= 300 && status < 400)
            {
                sw.Stop();
                return new LocalMusicBrainzScanResult(false, null, status, err, sw.ElapsedMilliseconds);
            }

            foreach (var cand in candidates)
            {
                if (bestOverall == null || cand.Confidence > bestOverall.Confidence)
                {
                    bestOverall = cand;
                }
            }

            // Short-circuit if high confidence found
            if (bestOverall != null && bestOverall.Confidence >= HighConfidenceThreshold)
            {
                break;
            }
        }

        // Strategy 4 fallback: If no match or confidence < Proposed, query cleaned title with limit=25
        if ((bestOverall == null || bestOverall.Confidence < ProposedConfidenceThreshold) && !timeoutCts.IsCancellationRequested)
        {
            var titleOnlyQuery = $"recording:\"{escCleanTitle}\"";
            var (status, err, candidates) = await QueryCandidatesAsync(titleOnlyQuery, title, artist, duration, timeoutCts.Token);
            if (candidates.Count > 0)
            {
                foreach (var cand in candidates)
                {
                    if (bestOverall == null || cand.Confidence > bestOverall.Confidence)
                    {
                        bestOverall = cand;
                    }
                }
            }
        }

        sw.Stop();

        if (timeoutCts.IsCancellationRequested && (bestOverall == null || bestOverall.Confidence < ProposedConfidenceThreshold))
        {
            return new LocalMusicBrainzScanResult(false, null, 408, "Local MusicBrainz request timed out after 5s", sw.ElapsedMilliseconds);
        }

        var matched = bestOverall != null && bestOverall.Confidence >= ProposedConfidenceThreshold;
        return new LocalMusicBrainzScanResult(matched, bestOverall, lastStatus, lastError, sw.ElapsedMilliseconds);
    }

    public static double CalculateConfidence(
        string targetTitle,
        string targetArtist,
        TimeSpan targetDuration,
        string candidateTitle,
        string candidateArtist,
        TimeSpan candidateDuration,
        bool isDerivative)
    {
        var cleanTargetTitle = CleanTitle(targetTitle);
        var cleanCandTitle = CleanTitle(candidateTitle);

        var titleScore = Math.Max(
            Similarity(targetTitle, candidateTitle),
            Similarity(cleanTargetTitle, cleanCandTitle)
        );

        var tradTargetTitle = ToTraditionalChinese(cleanTargetTitle);
        var tradCandTitle = ToTraditionalChinese(cleanCandTitle);
        titleScore = Math.Max(titleScore, Similarity(tradTargetTitle, tradCandTitle));

        var primaryTargetArtist = ExtractPrimaryArtist(targetArtist);
        var primaryCandArtist = ExtractPrimaryArtist(candidateArtist);

        var artistScore = Math.Max(
            Similarity(targetArtist, candidateArtist),
            Similarity(primaryTargetArtist, primaryCandArtist)
        );

        var tradTargetArtist = ToTraditionalChinese(primaryTargetArtist);
        var tradCandArtist = ToTraditionalChinese(primaryCandArtist);
        artistScore = Math.Max(artistScore, Similarity(tradTargetArtist, tradCandArtist));

        if (!string.IsNullOrEmpty(primaryTargetArtist) && !string.IsNullOrEmpty(primaryCandArtist))
        {
            if (candidateArtist.IndexOf(primaryTargetArtist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                targetArtist.IndexOf(primaryCandArtist, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                artistScore = Math.Max(artistScore, 0.90);
            }
        }

        var durationDiff = targetDuration > TimeSpan.Zero && candidateDuration > TimeSpan.Zero
            ? Math.Abs((targetDuration - candidateDuration).TotalSeconds)
            : 0;

        var durationScore = (targetDuration == TimeSpan.Zero || candidateDuration == TimeSpan.Zero)
            ? 0.70
            : durationDiff switch
            {
                <= 2 => 1.0,
                <= 5 => 0.95,
                <= 10 => 0.85,
                <= 20 => 0.65,
                <= 35 => 0.40,
                _ => 0.10
            };

        // Strict thresholds for high quality match
        if (titleScore < 0.80 || artistScore < 0.75) return 0.0;
        if (targetDuration > TimeSpan.Zero && candidateDuration > TimeSpan.Zero && durationDiff > 35) return 0.0;

        var baseScore = titleScore * 0.55 + artistScore * 0.35 + durationScore * 0.10;

        if (isDerivative)
        {
            // Penalize unintended derivative matches (e.g., target is standard song, but MB returned live/remix)
            baseScore *= 0.80;
        }

        return Math.Round(Math.Clamp(baseScore, 0.0, 1.0), 4);
    }

    public static bool IsDerivativeOrClip(
        string targetTitle,
        string candidateTitle,
        string? disambiguation,
        TimeSpan duration)
    {
        var targetNorm = Normalize(targetTitle);
        var candNorm = Normalize(candidateTitle);
        var disNorm = Normalize(disambiguation ?? string.Empty);

        string[] derivativeKeywords = { "live", "remix", "karaoke", "instrumental", "acoustic", "demo", "edit", "clip", "preview", "ringtone", "short" };

        bool targetHas = derivativeKeywords.Any(k => targetNorm.Contains(k, StringComparison.OrdinalIgnoreCase));
        bool candHas = derivativeKeywords.Any(k => candNorm.Contains(k, StringComparison.OrdinalIgnoreCase) || disNorm.Contains(k, StringComparison.OrdinalIgnoreCase));

        // Mismatch: candidate is derivative/live but target was original release
        if (!targetHas && candHas) return true;

        // Clip by duration (under 45 seconds)
        if (duration > TimeSpan.Zero && duration.TotalSeconds < 45) return true;

        return false;
    }

    public static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.92;

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)
                );
            }
            previous = current;
        }

        return Math.Max(0.0, 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    public static string EscapeLucenePhrase(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string GetString(JsonElement element, string property) =>
        GetNullableString(element, property) ?? string.Empty;

    private static string? GetNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
