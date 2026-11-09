using System.Net.Http.Headers;
using System.Text.Json;
using VODTracker.Core;

namespace VODTracker.Android;

internal sealed record AndroidChannelProfile(string Name, string Description, string? ImageUrl, bool IsLive);

internal static class AndroidChannelProfileService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/131.0.0.0 Mobile Safari/537.36";

    public static async Task<AndroidChannelProfile> LookupAsync(string platform, string channelId, CancellationToken cancellationToken = default)
    {
        platform = LibraryTransfer.NormalizePlatform(platform);
        var urls = platform == "SOOP"
            ? new[] { $"https://chapi.sooplive.com/api/{Uri.EscapeDataString(channelId)}/station", $"https://chapi.sooplive.co.kr/api/{Uri.EscapeDataString(channelId)}/station" }
            : new[] { $"https://api.chzzk.naver.com/service/v1/channels/{Uri.EscapeDataString(channelId)}" };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                using var response = await Client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    continue;
                }

                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = document.RootElement;
                if (platform == "SOOP") return ParseSoop(root, channelId);
                return ParseChzzk(root, channelId);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
            {
                lastError = exception;
            }
        }
        throw new HttpRequestException("채널 프로필 정보를 조회하지 못했습니다.", lastError);
    }

    public static async Task<byte[]> DownloadImageAsync(string imageUrl, string platform, CancellationToken cancellationToken = default)
    {
        var url = imageUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{imageUrl}" : imageUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("채널 이미지 주소가 올바르지 않습니다.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri(LibraryTransfer.NormalizePlatform(platform) == "SOOP" ? "https://www.sooplive.com/" : "https://chzzk.naver.com/");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public static string BuildSoopFallbackImageUrl(string channelId) =>
        $"https://profile.img.sooplive.com/LOGO/{Uri.EscapeDataString(channelId[..Math.Min(2, channelId.Length)])}/{Uri.EscapeDataString(channelId)}/{Uri.EscapeDataString(channelId)}.jpg";

    private static AndroidChannelProfile ParseChzzk(JsonElement root, string channelId)
    {
        if (!root.TryGetProperty("code", out var code) || !IsSuccess(code) ||
            !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("치지직 채널 응답 형식이 올바르지 않습니다.");
        var responseId = ReadString(content, "channelId");
        var name = ReadString(content, "channelName");
        if (string.IsNullOrWhiteSpace(responseId) || !responseId.Equals(channelId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("치지직 채널 ID가 응답과 일치하지 않습니다.");
        return new AndroidChannelProfile(name, ReadString(content, "channelDescription") ?? string.Empty,
            ReadString(content, "channelImageUrl"), content.TryGetProperty("openLive", out var openLive) && openLive.ValueKind == JsonValueKind.True);
    }

    private static AndroidChannelProfile ParseSoop(JsonElement root, string channelId)
    {
        if (!root.TryGetProperty("station", out var station) || station.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("SOOP 채널 응답 형식이 올바르지 않습니다.");
        var responseId = FirstString(station, "user_id", "userId", "station_user_id", "stationUserId", "bj_id", "bjId")
                         ?? FirstString(root, "user_id", "userId", "station_user_id", "stationUserId", "bj_id", "bjId");
        var name = FirstString(station, "user_nick", "station_name");
        if (string.IsNullOrWhiteSpace(responseId) || !responseId.Equals(channelId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("SOOP 채널 ID가 응답과 일치하지 않습니다.");
        var description = station.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object
            ? ReadString(display, "profile_text") ?? string.Empty : string.Empty;
        var imageUrl = ReadString(root, "profile_image") ?? ReadString(station, "profile_image");
        var isLive = root.TryGetProperty("broad", out var broad) && broad.ValueKind == JsonValueKind.Object &&
                     broad.TryGetProperty("broad_no", out var broadNo) && IsPositive(broadNo);
        return new AndroidChannelProfile(name, description, imageUrl, isLive);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(ReadString(element, name))) return ReadString(element, name);
        return null;
    }

    private static bool IsSuccess(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt32(out var number) && number == 200,
        JsonValueKind.String => value.GetString() == "200",
        _ => false
    };

    private static bool IsPositive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var number) && number > 0,
        JsonValueKind.String => long.TryParse(value.GetString(), out var number) && number > 0,
        _ => false
    };
}
