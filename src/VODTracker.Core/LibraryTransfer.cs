using System.Globalization;
using System.Text;
using VODTracker.Core;

namespace VODTracker.Core;

public sealed record TransferMedia(string Platform, string ChannelId, string ChannelName, string ChannelUrl,
    string VodId, string Title, string Url, DateTimeOffset? PublishedAt, string WatchStatus,
    double LastPositionSeconds, string LocalFilePath, string Memo, string UpdatedAt, string ThumbnailUrl = "", bool? Favorite = null);

public static class LibraryTransfer
{
    private static readonly string[] Columns = ["Platform", "ChannelId", "ChannelName", "ChannelUrl", "VodId", "Title", "Url", "PublishedAt", "WatchStatus", "LastPositionSeconds", "LocalFilePath", "Memo", "UpdatedAt", "ThumbnailUrl", "Favorite"];

    public static void ExportCsv(string path, IEnumerable<SavedChannelData> channels)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        ExportCsv(writer, channels);
    }

    public static void ExportCsv(Stream stream, IEnumerable<SavedChannelData> channels)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
        ExportCsv(writer, channels);
    }

    private static void ExportCsv(TextWriter writer, IEnumerable<SavedChannelData> channels)
    {
        WriteRow(writer, Columns);
        foreach (var channel in Flatten(channels)) WriteTransferRow(writer, channel);
        writer.Flush();
    }

    public static IReadOnlyList<TransferMedia> ImportCsv(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ImportCsv(reader);
    }

    public static IReadOnlyList<TransferMedia> ImportCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ImportCsv(reader);
    }

    private static IReadOnlyList<TransferMedia> ImportCsv(TextReader reader)
    {
        var rows = ReadCsv(reader.ReadToEnd());
        if (rows.Count == 0) throw new InvalidDataException("CSV 파일에 데이터가 없습니다.");
        var headers = rows[0];
        int Col(string name) => headers.FindIndex(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var required in new[] { "Platform", "ChannelId", "Title" })
            if (Col(required) < 0) throw new InvalidDataException($"필수 열 {required}이(가) 없습니다.");
        string Value(List<string> row, string name) { var index = Col(name); return index >= 0 && index < row.Count ? row[index] : ""; }
        var result = new List<TransferMedia>();
        foreach (var row in rows.Skip(1))
        {
            var platform = NormalizePlatform(Value(row, "Platform"));
            var channelId = Value(row, "ChannelId").Trim();
            var title = Value(row, "Title").Trim();
            if (platform is not ("CHZZK" or "SOOP") || channelId.Length == 0 || title.Length == 0)
                throw new InvalidDataException("Platform은 CHZZK/치지직 또는 SOOP이어야 하며, ChannelId와 Title은 필수입니다.");
            var url = Value(row, "Url").Trim();
            var channelUrl = Value(row, "ChannelUrl").Trim();
            var thumbnailUrl = Value(row, "ThumbnailUrl").Trim();
            if (channelUrl.Length > 0 && (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var channelUri) || channelUri.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"{channelId}의 ChannelUrl이 올바르지 않습니다.");
            if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var mediaUri) || mediaUri.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"{title}의 Url이 올바르지 않습니다.");
            if (thumbnailUrl.Length > 0 && (!Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var thumbnailUri) || thumbnailUri.Scheme is not ("https" or "http")))
                throw new InvalidDataException($"{title}의 ThumbnailUrl이 올바르지 않습니다.");
            DateTimeOffset? date = DateTimeOffset.TryParse(Value(row, "PublishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            var vodId = Value(row, "VodId").Trim();
            if (vodId.Length == 0) vodId = GetVodId(platform, url, Value(row, "LocalFilePath"), title);
            var position = double.TryParse(Value(row, "LastPositionSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? Math.Max(0, seconds) : 0;
            var favorite = bool.TryParse(Value(row, "Favorite"), out var parsedFavorite) ? parsedFavorite : (bool?)null;
            result.Add(new TransferMedia(platform, channelId, Value(row, "ChannelName").Trim(), channelUrl, vodId,
                title, url, date, Value(row, "WatchStatus").Trim(), position, Value(row, "LocalFilePath").Trim(), Value(row, "Memo"), Value(row, "UpdatedAt"), thumbnailUrl, favorite));
        }
        return result;
    }


    public static string GetVodId(string platform, string url, string file, string title)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) return Uri.UnescapeDataString(segments[^1]);
        }
        var identity = file.Length > 0 ? file : title;
        return "local-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(platform + ":" + identity)))[..16];
    }

    public static string NormalizePlatform(string platform)
    {
        var value = platform.Trim();
        if (value.Equals("SOOP", StringComparison.OrdinalIgnoreCase)) return "SOOP";
        if (value.Equals("CHZZK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("치지직", StringComparison.OrdinalIgnoreCase)) return "CHZZK";
        return value.ToUpperInvariant();
    }

    private static IEnumerable<TransferMedia> Flatten(IEnumerable<SavedChannelData> channels) => channels.SelectMany(data => data.Media.Select(item =>
    {
        var url = item.OnlineUrl ?? (Uri.TryCreate(item.SourceValue, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? item.SourceValue : "");
        var platform = NormalizePlatform(data.Channel.Platform);
        return new TransferMedia(platform, data.Channel.ChannelId, data.Channel.Name, data.Channel.Url,
            GetVodId(platform, url, item.LocalFilePath ?? "", item.Title), item.Title, url, item.PublishedAt,
            item.WatchStatus, item.LastPositionSeconds, item.LocalFilePath ?? "", item.Memo, (item.UpdatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture), item.ThumbnailUrl ?? "", item.Favorite);
    }));

    private static void WriteTransferRow(TextWriter writer, TransferMedia row) =>
        WriteRow(writer, [row.Platform, row.ChannelId, row.ChannelName, row.ChannelUrl, row.VodId, row.Title, row.Url,
            row.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "", row.WatchStatus, row.LastPositionSeconds.ToString(CultureInfo.InvariantCulture), row.LocalFilePath, row.Memo, row.UpdatedAt, row.ThumbnailUrl, row.Favorite == true ? "true" : row.Favorite == false ? "false" : ""]);


    private static void WriteRow(TextWriter writer, IEnumerable<string> fields) => writer.WriteLine(string.Join(',', fields.Select(value => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value)));
    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++) { var c = text[i]; if (quoted) { if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else if (c == '"') quoted = false; else field.Append(c); } else if (c == '"' && field.Length == 0) quoted = true; else if (c == ',') { row.Add(field.ToString()); field.Clear(); } else if (c is '\r' or '\n') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(field.ToString()); field.Clear(); if (row.Any(value => value.Length > 0)) rows.Add(row); row = []; } else field.Append(c); }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); } return rows;
    }
}
