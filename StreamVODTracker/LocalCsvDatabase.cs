using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StreamVODTracker;

internal sealed record SavedChannel(string Platform, string ChannelId, string Name, string Description, string Url,
    string? ProfileImageUrl, string? ProfileImagePath);

internal sealed record SavedMedia(
    string Title,
    string Platform,
    string SourceType,
    string SourceLabel,
    string SourceValue,
    DateTimeOffset? PublishedAt,
    string WatchStatus,
    string? ThumbnailUrl,
    string? ThumbnailPath,
    string Availability);

internal sealed record SavedChannelData(SavedChannel Channel, IReadOnlyList<SavedMedia> Media);

internal sealed class LocalCsvDatabase
{
    private static readonly string[] Header =
    [
        "RecordType", "Platform", "ChannelId", "ChannelName", "ChannelDescription", "ChannelUrl",
        "ChannelProfileImageUrl", "ChannelProfileImagePath",
        "Title", "SourceType", "SourceLabel", "SourceValue", "PublishedAtUtc", "WatchStatus",
        "ThumbnailUrl", "ThumbnailPath", "Availability"
    ];
    private static readonly string[] ProfileLegacyHeader =
    [
        "RecordType", "Platform", "ChannelName", "ChannelDescription", "ChannelUrl", "IsLive",
        "ChannelProfileImageUrl", "ChannelProfileImagePath",
        "Title", "SourceType", "SourceLabel", "SourceValue", "PublishedAtUtc", "WatchStatus",
        "ThumbnailUrl", "ThumbnailPath", "Availability"
    ];
    private static readonly string[] LegacyHeader =
    [
        "RecordType", "Platform", "ChannelName", "ChannelDescription", "ChannelUrl", "IsLive",
        "Title", "SourceType", "SourceLabel", "SourceValue", "PublishedAtUtc", "WatchStatus",
        "ThumbnailUrl", "ThumbnailPath", "Availability"
    ];

    private readonly string _databaseDirectory;
    private readonly string _thumbnailDirectory;
    private readonly object _saveLock = new();
    private readonly Dictionary<string, string> _loadedCsvPaths = new(StringComparer.OrdinalIgnoreCase);

    public LocalCsvDatabase(string applicationDirectory)
    {
        _databaseDirectory = Path.Combine(applicationDirectory, "db");
        _thumbnailDirectory = Path.Combine(applicationDirectory, "thumbnail");
        Directory.CreateDirectory(_databaseDirectory);
        Directory.CreateDirectory(_thumbnailDirectory);
    }

    public IReadOnlyList<SavedChannelData> LoadAll()
    {
        var result = new List<SavedChannelData>();
        foreach (var path in Directory.EnumerateFiles(_databaseDirectory, "*.csv"))
        {
            try
            {
                var rows = ReadCsv(path);
                if (rows.Count == 0) continue;
                var hasProfileColumns = rows[0].SequenceEqual(Header, StringComparer.Ordinal);
                var isProfileLegacy = rows[0].SequenceEqual(ProfileLegacyHeader, StringComparer.Ordinal);
                var isLegacy = rows[0].SequenceEqual(LegacyHeader, StringComparer.Ordinal);
                if (!hasProfileColumns && !isProfileLegacy && !isLegacy) continue;
                var mediaStart = hasProfileColumns || isProfileLegacy ? 8 : 6;
                SavedChannel? channel = null;
                var media = new List<SavedMedia>();
                foreach (var row in rows.Skip(1))
                {
                    if (row.Count < (hasProfileColumns ? Header.Length : isProfileLegacy ? ProfileLegacyHeader.Length : LegacyHeader.Length)) continue;
                    if (row[0] == "Channel")
                    {
                        var platform = row[1];
                        var channelId = hasProfileColumns ? NullIfEmpty(row[2]) : null;
                        var nameIndex = hasProfileColumns ? 3 : 2;
                        var descriptionIndex = nameIndex + 1;
                        var urlIndex = nameIndex + 2;
                        channelId ??= ExtractChannelId(platform, row[urlIndex]);
                        var profileIndex = isLegacy ? -1 : hasProfileColumns ? 6 : 6;
                        channel = new SavedChannel(platform, channelId, row[nameIndex], row[descriptionIndex], row[urlIndex],
                            profileIndex < 0 ? null : NullIfEmpty(row[profileIndex]),
                            profileIndex < 0 ? null : NullIfEmpty(row[profileIndex + 1]));
                    }
                    else if (row[0] == "Media")
                    {
                        media.Add(new SavedMedia(row[mediaStart], row[1], row[mediaStart + 1], row[mediaStart + 2], row[mediaStart + 3],
                            DateTimeOffset.TryParse(row[mediaStart + 4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                            row[mediaStart + 5], NullIfEmpty(row[mediaStart + 6]), NullIfEmpty(row[mediaStart + 7]), row[mediaStart + 8]));
                    }
                }
                if (channel is not null)
                {
                    result.Add(new SavedChannelData(channel, media));
                    _loadedCsvPaths[GetChannelKey(channel.Platform, channel.ChannelId)] = path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (DecoderFallbackException) { }
        }
        return result;
    }

    public void Save(ChannelEntry channel, IEnumerable<MediaCard> cards)
    {
        lock (_saveLock)
        {
            var path = GetChannelCsvPath(channel);
            var temporaryPath = path + ".tmp";
            using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(true)))
            {
                WriteRow(writer, Header);
                WriteRow(writer, ["Channel", channel.Platform, channel.ChannelId, channel.DisplayName, channel.Description,
                    channel.ChannelUrl, channel.ProfileImageUrl ?? "", channel.ProfileImagePath ?? "", .. EmptyFields(9)]);
                foreach (var card in cards.Where(card => card.ChannelUrl == channel.ChannelUrl))
                {
                    WriteRow(writer, ["Media", channel.Platform, channel.ChannelId, "", "", "", "", "", card.Title, card.SourceTypeLabel,
                        card.SourceLabel, card.SourceValue, card.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "",
                        card.WatchStatusLabel, card.ThumbnailUrl ?? "", card.ThumbnailPath ?? "", card.AvailabilityLabel]);
                }
            }
            File.Move(temporaryPath, path, true);
            var key = GetChannelKey(channel.Platform, channel.ChannelId);
            if (_loadedCsvPaths.TryGetValue(key, out var oldPath) && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                File.Delete(oldPath);
            _loadedCsvPaths[key] = path;
        }
    }

    public string SaveThumbnail(string platform, string channelId, string title, DateTimeOffset? publishedAt, string sourceUrl,
        byte[] content, string? contentType)
    {
        var channelDirectory = Path.Combine(_thumbnailDirectory, GetStableId(platform, channelId));
        Directory.CreateDirectory(channelDirectory);
        var date = publishedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
        var name = $"{date}_{MakeSafeFileName(title)}_{GetStableId(sourceUrl)[..8]}{GetExtension(sourceUrl, contentType)}";
        var path = Path.Combine(channelDirectory, name);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, path, true);
        return path;
    }

    public string SaveChannelProfileImage(string platform, string channelId, byte[] content, string? contentType)
    {
        var channelDirectory = Path.Combine(_thumbnailDirectory, GetStableId(platform, channelId));
        Directory.CreateDirectory(channelDirectory);
        var path = Path.Combine(channelDirectory, $"channel_profile{GetExtension(channelId, contentType)}");
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, path, true);
        return path;
    }

    private string GetChannelCsvPath(ChannelEntry channel)
    {
        var safeId = MakeSafeFileName(channel.ChannelId);
        return Path.Combine(_databaseDirectory, $"{MakeSafeFileName(channel.Platform)}_{safeId}_{GetStableId(channel.Platform, channel.ChannelId)}.csv");
    }

    private static string GetStableId(string platform, string channelId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{platform}:{channelId}"))).ToLowerInvariant()[..20];

    private static string GetStableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..20];

    private static string GetChannelKey(string platform, string channelId) => $"{platform}:{channelId}";

    private static string ExtractChannelId(string platform, string channelUrl)
    {
        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var uri)) return channelUrl;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return platform == "SOOP" && segments.Length > 1 && segments[0].Equals("station", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[1])
            : segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : channelUrl;
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
        safe = string.Join('_', safe.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return safe.Length > 80 ? safe[..80] : safe;
    }

    private static string GetExtension(string sourceUrl, string? contentType)
    {
        var extension = contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/avif" => ".avif",
            _ => null
        };
        if (extension is not null) return extension;
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            extension = Path.GetExtension(uri.AbsolutePath);
            if (extension.Length is > 0 and <= 6 && extension.All(character => char.IsAsciiLetterOrDigit(character) || character == '.')) return extension;
        }
        return ".img";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static IEnumerable<string> EmptyFields(int count) => Enumerable.Repeat("", count);

    private static void WriteRow(TextWriter writer, IEnumerable<string> fields)
    {
        writer.WriteLine(string.Join(',', fields.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static List<List<string>> ReadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (quoted)
            {
                if (current == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (current == '"') quoted = false;
                else field.Append(current);
            }
            else if (current == '"' && field.Length == 0) quoted = true;
            else if (current == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (current is '\r' or '\n')
            {
                if (current == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(value => value.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(current);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
