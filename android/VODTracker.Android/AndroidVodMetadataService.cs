using System.Globalization;
using System.Text.Json;
using VODTracker.Core;

namespace VODTracker.Android;

internal static class AndroidVodMetadataService
{
    private static readonly HttpClient ChzzkClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient SoopClient = CreateSoopClient();
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/131.0.0.0 Mobile Safari/537.36";

    public static Task<double?> GetDurationSecondsAsync(string platform, string? vodUrl, CancellationToken cancellationToken = default) =>
        LibraryTransfer.NormalizePlatform(platform) == "SOOP"
            ? GetSoopDurationSecondsAsync(vodUrl, cancellationToken)
            : GetChzzkDurationSecondsAsync(vodUrl, cancellationToken);

    private static async Task<double?> GetChzzkDurationSecondsAsync(string? vodUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(vodUrl, UriKind.Absolute, out var uri)) return null;
        var videoId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.chzzk.naver.com/service/v2/videos/{Uri.EscapeDataString(videoId)}");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await ChzzkClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("content", out var content)
            ? ReadSeconds(content, "duration", "videoDuration")
            : null;
    }

    private static async Task<double?> GetSoopDurationSecondsAsync(string? vodUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(vodUrl, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var playerIndex = Array.FindIndex(segments, segment => segment.Equals("player", StringComparison.OrdinalIgnoreCase));
        var vodId = playerIndex >= 0 && playerIndex + 1 < segments.Length && long.TryParse(segments[playerIndex + 1], out _)
            ? segments[playerIndex + 1]
            : segments.LastOrDefault(segment => long.TryParse(segment, out _));
        if (vodId is null) return null;

        foreach (var apiLevel in new[] { "10", "11" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.m.sooplive.com/station/video/a/view")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["nTitleNo"] = vodId,
                    ["nApiLevel"] = apiLevel,
                    ["nPlaylistIdx"] = "0"
                })
            };
            request.Headers.Referrer = uri;
            using var response = await SoopClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) continue;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("data", out var data)) continue;
            if (TryReadMilliseconds(data, "total_file_duration", out var totalMs)) return totalMs / 1000d;
            if (data.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var fileTotalMs = files.EnumerateArray()
                    .Sum(file => TryReadMilliseconds(file, "duration", out var durationMs) ? durationMs : 0);
                if (fileTotalMs > 0) return fileTotalMs / 1000d;
            }
        }

        return null;
    }

    private static HttpClient CreateSoopClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://vod.sooplive.com");
        return client;
    }

    private static double? ReadSeconds(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0) return number;
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number > 0)
                return number;
        }
        return null;
    }

    private static bool TryReadMilliseconds(JsonElement element, string name, out double milliseconds)
    {
        milliseconds = 0;
        if (!element.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out milliseconds) && milliseconds > 0;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds) && milliseconds > 0;
    }
}
