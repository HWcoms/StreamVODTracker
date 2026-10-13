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
    string Availability,
    string? OnlineUrl,
    string? LocalFilePath,
    string VodId = "",
    double LastPositionSeconds = 0,
    string Memo = "",
    DateTimeOffset? UpdatedAt = null);

internal sealed record SavedChannelData(SavedChannel Channel, IReadOnlyList<SavedMedia> Media);

internal interface IVodRepository
{
    IReadOnlyList<SavedChannelData> LoadAll();
    void Save(ChannelEntry channel, IEnumerable<MediaCard> cards);
}

internal sealed class LocalCsvDatabase : IVodRepository
{
    private static readonly string[] Header =
    [
        "RecordType", "Platform", "ChannelId", "ChannelName", "ChannelDescription", "ChannelUrl",
        "ChannelProfileImageUrl", "ChannelProfileImagePath",
        "VodId", "Title", "SourceType", "Url", "LocalFilePath", "PublishedAtUtc", "WatchStatus",
        "LastPositionSeconds", "Memo", "UpdatedAt", "ThumbnailUrl", "ThumbnailPath"
    ];
    private static readonly string[] PreviousCurrentHeader =
    [
        "RecordType", "Platform", "ChannelId", "ChannelName", "ChannelDescription", "ChannelUrl",
        "ChannelProfileImageUrl", "ChannelProfileImagePath", "Title", "SourceType", "SourceLabel", "SourceValue",
        "PublishedAtUtc", "WatchStatus", "ThumbnailUrl", "ThumbnailPath", "Availability", "Url", "LocalFilePath",
        "VodId", "LastPositionSeconds", "Memo", "UpdatedAt"
    ];
    private static readonly string[] PreviousExtendedHeader =
    [
        "RecordType", "Platform", "ChannelId", "ChannelName", "ChannelDescription", "ChannelUrl",
        "ChannelProfileImageUrl", "ChannelProfileImagePath", "Title", "SourceType", "SourceLabel", "SourceValue",
        "PublishedAtUtc", "WatchStatus", "ThumbnailUrl", "ThumbnailPath", "Availability", "Url", "LocalFilePath"
    ];
    private static readonly string[] PreviousHeader =
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
        RemoveObsoleteChannelIndexes();
    }

    public IReadOnlyList<SavedChannelData> LoadAll()
    {
        var result = new List<SavedChannelData>();
        var paths = Directory.EnumerateFiles(_databaseDirectory, "*.csv")
            .Concat(new[] { "chzzk", "soop" }.SelectMany(platform =>
            {
                var directory = Path.Combine(_databaseDirectory, platform);
                return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.csv") : [];
            }));
        foreach (var path in paths)
        {
            try
            {
                var rows = ReadCsv(path);
                if (rows.Count == 0) continue;
                var hasProfileColumns = rows[0].SequenceEqual(Header, StringComparer.Ordinal);
                var hasPreviousCurrentColumns = rows[0].SequenceEqual(PreviousCurrentHeader, StringComparer.Ordinal);
                var hasExtendedColumns = rows[0].SequenceEqual(PreviousExtendedHeader, StringComparer.Ordinal);
                var hasPreviousColumns = rows[0].SequenceEqual(PreviousHeader, StringComparer.Ordinal);
                var isProfileLegacy = rows[0].SequenceEqual(ProfileLegacyHeader, StringComparer.Ordinal);
                var isLegacy = rows[0].SequenceEqual(LegacyHeader, StringComparer.Ordinal);
                if (!hasProfileColumns && !hasPreviousCurrentColumns && !hasExtendedColumns && !hasPreviousColumns && !isProfileLegacy && !isLegacy) continue;
                var hasChannelIdColumn = hasProfileColumns || hasPreviousCurrentColumns || hasExtendedColumns || hasPreviousColumns;
                var hasProfile = hasChannelIdColumn || isProfileLegacy;
                var mediaStart = hasProfile ? 8 : 6;
                SavedChannel? channel = null;
                var media = new List<SavedMedia>();
                foreach (var row in rows.Skip(1))
                {
                    if (row.Count < (hasProfileColumns ? Header.Length : hasPreviousCurrentColumns ? PreviousCurrentHeader.Length : hasExtendedColumns ? PreviousExtendedHeader.Length : hasPreviousColumns ? PreviousHeader.Length : isProfileLegacy ? ProfileLegacyHeader.Length : LegacyHeader.Length)) continue;
                    if (row[0] == "Channel")
                    {
                        var platform = row[1];
                        var channelId = hasChannelIdColumn ? NullIfEmpty(row[2]) : null;
                        var nameIndex = hasChannelIdColumn ? 3 : 2;
                        var descriptionIndex = nameIndex + 1;
                        var urlIndex = nameIndex + 2;
                        channelId ??= ExtractChannelId(platform, row[urlIndex]);
                        var profileIndex = isLegacy ? -1 : 6;
                        channel = new SavedChannel(platform, channelId, row[nameIndex], row[descriptionIndex], row[urlIndex],
                            profileIndex < 0 ? null : NullIfEmpty(row[profileIndex]),
                            profileIndex < 0 ? null : NullIfEmpty(row[profileIndex + 1]));
                    }
                    else if (row[0] == "Media")
                    {
                        var mediaPlatform = string.IsNullOrWhiteSpace(row[1]) ? channel?.Platform ?? "" : row[1];
                        if (hasProfileColumns)
                        {
                            var vodId = row[8];
                            var title = row[9];
                            var sourceType = row[10];
                            var onlineUrl = NullIfEmpty(row[11]);
                            var localFilePath = NullIfEmpty(row[12]);
                            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(row[13], CultureInfo.InvariantCulture, DateTimeStyles.None, out var newDate) ? newDate : null;
                            var position = double.TryParse(row[15], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
                            DateTimeOffset? updatedAt = DateTimeOffset.TryParse(row[17], CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ? updated : null;
                            media.Add(new SavedMedia(title, mediaPlatform, NormalizeSourceType(sourceType, onlineUrl, localFilePath),
                                GetSourceLabel(onlineUrl, localFilePath), onlineUrl ?? localFilePath ?? "", publishedAt,
                                row[14], NullIfEmpty(row[18]), NullIfEmpty(row[19]), GetAvailability(localFilePath),
                                onlineUrl, localFilePath, string.IsNullOrWhiteSpace(vodId) ? LibraryTransfer.GetVodId(mediaPlatform, onlineUrl ?? "", localFilePath ?? "", title) : vodId,
                                position, row[16], updatedAt));
                        }
                        else
                        {
                            var oldSourceType = row[mediaStart + 1];
                            var oldSourceValue = row[mediaStart + 3];
                            var onlineUrl = hasPreviousCurrentColumns || hasExtendedColumns ? NullIfEmpty(row[17]) :
                                oldSourceType == "PC 파일" ? null : IsHttpUrl(oldSourceValue) ? oldSourceValue : null;
                            var localFilePath = hasPreviousCurrentColumns || hasExtendedColumns ? NullIfEmpty(row[18]) :
                                oldSourceType == "PC 파일" ? NullIfEmpty(oldSourceValue) : null;
                            var vodId = hasPreviousCurrentColumns ? NullIfEmpty(row[19]) ?? LibraryTransfer.GetVodId(mediaPlatform, onlineUrl ?? "", localFilePath ?? "", row[mediaStart]) : "";
                            var position = hasPreviousCurrentColumns && double.TryParse(row[20], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
                            var memo = hasPreviousCurrentColumns ? row[21] : "";
                            DateTimeOffset? updatedAt = hasPreviousCurrentColumns && DateTimeOffset.TryParse(row[22], CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ? updated : null;
                            media.Add(new SavedMedia(row[mediaStart], mediaPlatform, NormalizeSourceType(oldSourceType, onlineUrl, localFilePath), row[mediaStart + 2], oldSourceValue,
                                DateTimeOffset.TryParse(row[mediaStart + 4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                                row[mediaStart + 5], NullIfEmpty(row[mediaStart + 6]), NullIfEmpty(row[mediaStart + 7]), row[mediaStart + 8],
                                onlineUrl, localFilePath, vodId, position, memo, updatedAt));
                        }
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
            var directory = GetPlatformDirectory(channel.Platform);
            Directory.CreateDirectory(directory);
            var path = GetChannelCsvPath(channel);
            var temporaryPath = path + ".tmp";
            using (var writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(true)))
            {
                WriteRow(writer, Header);
                WriteRow(writer, ["Channel", channel.Platform, channel.ChannelId, channel.DisplayName, channel.Description,
                    channel.ChannelUrl, channel.ProfileImageUrl ?? "", channel.ProfileImagePath ?? "", .. EmptyFields(12)]);
                foreach (var card in cards.Where(card => card.ChannelUrl == channel.ChannelUrl))
                {
                    WriteRow(writer, ["Media", "", "", "", "", "", "", "", card.VodId, card.Title, GetPersistedSourceType(card),
                        card.OnlineUrl ?? "", card.LocalFilePath ?? "", card.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "",
                        card.WatchStatusLabel, card.LastPositionSeconds.ToString(CultureInfo.InvariantCulture), card.Memo,
                        card.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), card.ThumbnailUrl ?? "", card.ThumbnailPath ?? ""]);
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
        var fileId = safeId.Equals(channel.ChannelId, StringComparison.Ordinal) ? safeId : $"{safeId}_{GetStableId(channel.Platform, channel.ChannelId)[..8]}";
        return Path.Combine(GetPlatformDirectory(channel.Platform), $"{fileId}.csv");
    }

    private string GetPlatformDirectory(string platform) =>
        Path.Combine(_databaseDirectory, platform.Equals("SOOP", StringComparison.OrdinalIgnoreCase) ? "soop" : "chzzk");

    private void RemoveObsoleteChannelIndexes()
    {
        foreach (var platform in new[] { "chzzk", "soop" })
        {
            var directory = Path.Combine(_databaseDirectory, platform);
            var indexPath = Path.Combine(directory, "channels.csv");
            try { if (File.Exists(indexPath)) File.Delete(indexPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetPersistedSourceType(MediaCard card) =>
        !string.IsNullOrWhiteSpace(card.OnlineUrl) ? "URL" : "파일";

    private static string NormalizeSourceType(string sourceType, string? url, string? filePath) =>
        !string.IsNullOrWhiteSpace(url) || sourceType is "VOD" or "URL" or "링크" or "링크+파일" ? "URL" :
        !string.IsNullOrWhiteSpace(filePath) || sourceType is "파일" or "PC 파일" ? "파일" : sourceType;

    private static string GetSourceLabel(string? url, string? filePath) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host :
        !string.IsNullOrWhiteSpace(filePath) ? Path.GetFileName(filePath) : "링크 없음";

    private static string GetAvailability(string? filePath) => string.IsNullOrWhiteSpace(filePath)
        ? "링크 사용 가능"
        : File.Exists(filePath) ? "파일 확인됨" : "파일 확인 필요";

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
    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
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
