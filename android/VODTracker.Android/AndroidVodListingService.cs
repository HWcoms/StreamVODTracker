using System.Globalization;
using System.Text.Json;
using VODTracker.Core;

namespace VODTracker.Android;

internal sealed record AndroidVodListingItem(
    string VodId, string Title, string Url, DateTimeOffset? PublishedAt, string? ThumbnailUrl, double? DurationSeconds);

internal static class AndroidVodListingService
{
    private const int ChzzkPageSize = 30;
    private const int SoopPageSize = 60;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string UserAgent = "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/131.0.0.0 Mobile Safari/537.36";

    public static Task<IReadOnlyList<AndroidVodListingItem>> FetchLatestAsync(SavedChannel channel, CancellationToken cancellationToken = default) =>
        LibraryTransfer.NormalizePlatform(channel.Platform) == "SOOP"
            ? FetchSoopAsync(channel.ChannelId, cancellationToken)
            : FetchChzzkAsync(channel.ChannelId, cancellationToken);

    private static async Task<IReadOnlyList<AndroidVodListingItem>> FetchSoopAsync(string channelId, CancellationToken cancellationToken)
    {
        var url = $"https://chapi.sooplive.com/api/{Uri.EscapeDataString(channelId)}/vods/all/streamer?page=1&per_page={SoopPageSize}&orderby=reg_date";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SOOP VOD 응답에 목록이 없습니다.");

        var videos = new List<AndroidVodListingItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadStringOrNumber(item, "title_no");
            var title = ReadString(item, "title_name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !seen.Add(id)) continue;
            var duration = ReadDuration(item, "total_file_duration");
            if (duration is > 0) duration /= 1000d;
            duration ??= ReadDuration(item, "file_duration", "duration");
            videos.Add(new AndroidVodListingItem(id, title,
                $"https://vod.sooplive.com/player/{Uri.EscapeDataString(id)}",
                ParseDate(ReadStringOrNumber(item, "reg_date")),
                ReadFirstString(item, "thumb", "thumbnail", "thumb_url", "thumbnail_url", "title_img", "title_thumbnail"),
                duration));
        }
        return videos;
    }

    private static async Task<IReadOnlyList<AndroidVodListingItem>> FetchChzzkAsync(string channelId, CancellationToken cancellationToken)
    {
        var videos = new List<AndroidVodListingItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < 5; page++)
        {
            var url = $"https://api.chzzk.naver.com/service/v1/channels/{Uri.EscapeDataString(channelId)}/videos?sortType=LATEST&pagingType=PAGE&page={page}&size={ChzzkPageSize}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("치지직 VOD 응답에 목록이 없습니다.");
            if (data.GetArrayLength() == 0) break;

            foreach (var item in data.EnumerateArray())
            {
                var id = ReadStringOrNumber(item, "videoNo");
                var title = ReadString(item, "videoTitle");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !seen.Add(id)) continue;
                videos.Add(new AndroidVodListingItem(id, title,
                    $"https://chzzk.naver.com/video/{Uri.EscapeDataString(id)}",
                    ParseDate(ReadString(item, "publishDate")),
                    ReadString(item, "thumbnailImageUrl"),
                    ReadDuration(item, "duration", "videoDuration")));
            }
            if (data.GetArrayLength() < ChzzkPageSize) break;
        }
        return videos;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadStringOrNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static string? ReadFirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(ReadString(item, name))) return ReadString(item, name);
        return null;
    }

    private static double? ReadDuration(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number > 0) return number;
            if (value.ValueKind != JsonValueKind.String) continue;
            if (double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number > 0) return number;
            if (TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var duration) && duration.TotalSeconds > 0)
                return duration.TotalSeconds;
        }
        return null;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;
}
