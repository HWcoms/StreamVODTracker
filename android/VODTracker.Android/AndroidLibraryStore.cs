using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VODTracker.Core;

namespace VODTracker.Android;

internal sealed class AndroidLibraryStore
{
    private readonly string _path;
    private readonly object _saveQueueLock = new();
    private Task _pendingSave = Task.CompletedTask;
    private IReadOnlyList<SavedChannelData>? _cachedChannels;

    public AndroidLibraryStore()
    {
        var filesDirectory = global::Android.App.Application.Context.FilesDir!.AbsolutePath!;
        _path = Path.Combine(filesDirectory, "vod-library.json");
    }

    public IReadOnlyList<SavedChannelData> LoadAll()
    {
        if (_cachedChannels is not null) return _cachedChannels;
        if (!File.Exists(_path)) return [];
        var payload = DriveLibraryCodec.Deserialize(File.ReadAllText(_path, Encoding.UTF8));
        return _cachedChannels = payload.Channels.Select(channel => new SavedChannelData(channel.Channel, channel.Media)).ToArray();
    }

    public int Import(IEnumerable<TransferMedia> rows)
    {
        var channels = LoadAll().ToDictionary(
            item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var row in rows)
        {
            var key = Key(row.Platform, row.ChannelId);
            if (!channels.TryGetValue(key, out var channelData))
            {
                var channelUrl = row.ChannelUrl.Length > 0 ? row.ChannelUrl : row.Platform == "SOOP"
                    ? $"https://www.sooplive.com/station/{Uri.EscapeDataString(row.ChannelId)}"
                    : $"https://chzzk.naver.com/{Uri.EscapeDataString(row.ChannelId)}";
                channelData = new SavedChannelData(
                    new SavedChannel(row.Platform, row.ChannelId,
                        string.IsNullOrWhiteSpace(row.ChannelName) ? row.ChannelId : row.ChannelName,
                        string.Empty, channelUrl, null, null), []);
            }
            else if (!string.IsNullOrWhiteSpace(row.ChannelUrl) &&
                     !channelData.Channel.Url.Equals(row.ChannelUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{row.Platform}/{row.ChannelId}의 채널 주소가 기존 데이터와 일치하지 않습니다.");
            }

            var media = channelData.Media.ToList();
            var existingIndex = media.FindIndex(item => item.VodId.Equals(row.VodId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                var existing = media[existingIndex];
                media[existingIndex] = existing with
                {
                    Title = row.Title,
                    OnlineUrl = NullIfEmpty(row.Url),
                    LocalFilePath = NullIfEmpty(row.LocalFilePath),
                    SourceValue = row.Url.Length > 0 ? row.Url : row.LocalFilePath,
                    PublishedAt = row.PublishedAt,
                    ThumbnailUrl = NullIfEmpty(row.ThumbnailUrl) ?? existing.ThumbnailUrl,
                    Favorite = row.Favorite ?? existing.Favorite
                };
            }
            else
            {
                var sourceType = row.Url.Length > 0 && row.LocalFilePath.Length > 0 ? "링크+파일" : row.Url.Length > 0 ? "링크" : "PC 파일";
                var sourceValue = row.Url.Length > 0 ? row.Url : row.LocalFilePath;
                media.Add(new SavedMedia(row.Title, row.Platform, sourceType,
                    row.Url.Length > 0 && Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) ? uri.Host : Path.GetFileName(row.LocalFilePath),
                    sourceValue, row.PublishedAt, row.WatchStatus, NullIfEmpty(row.ThumbnailUrl), null,
                    row.LocalFilePath.Length > 0 ? "파일 경로 확인 필요" : "링크 사용 가능",
                    NullIfEmpty(row.Url), NullIfEmpty(row.LocalFilePath), row.VodId,
                    row.LastPositionSeconds, row.Memo,
                    DateTimeOffset.TryParse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated)
                        ? updated : DateTimeOffset.UtcNow,
                    row.Favorite ?? false));
                added++;
            }

            channels[key] = channelData with { Media = media };
        }

        _cachedChannels = channels.Values.ToArray();
        QueueSave(_cachedChannels);
        return added;
    }

    public void Export(Stream stream) => LibraryTransfer.ExportCsv(stream, LoadAll());

    public Task DeleteAllAsync()
    {
        lock (_saveQueueLock)
        {
            _cachedChannels = Array.Empty<SavedChannelData>();
            _pendingSave = _pendingSave.ContinueWith(_ => Task.Run(() =>
            {
                if (File.Exists(_path)) File.Delete(_path);
                var temporaryPath = _path + ".tmp";
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                var filesDirectory = global::Android.App.Application.Context.FilesDir!.AbsolutePath!;
                foreach (var directoryName in new[] { "channel-images", "media-thumbnails" })
                {
                    var directory = Path.Combine(filesDirectory, directoryName);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _pendingSave;
        }
    }

    public string GetChannelImagePath(string platform, string channelId)
    {
        var directory = Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath!, "channel-images");
        Directory.CreateDirectory(directory);
        var key = Encoding.UTF8.GetBytes($"{LibraryTransfer.NormalizePlatform(platform)}:{channelId}");
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(key)) + ".img");
    }

    public string GetMediaThumbnailPath(string imageUrl)
    {
        var directory = Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath!, "media-thumbnails");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl))) + ".img");
    }

    public void InvalidateMediaThumbnailCache(string imageUrl)
    {
        var path = GetMediaThumbnailPath(imageUrl);
        if (File.Exists(path)) File.Delete(path);
    }

    public void UpdateChannelProfile(string platform, string channelId, string name, string description, string? imageUrl, string? imagePath)
    {
        var channels = LoadAll().ToDictionary(item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var key = Key(platform, channelId);
        if (!channels.TryGetValue(key, out var existing)) return;
        var updatedChannel = existing.Channel with
        {
            Name = string.IsNullOrWhiteSpace(name) ? existing.Channel.Name : name,
            Description = description,
            ProfileImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? existing.Channel.ProfileImageUrl : imageUrl,
            ProfileImagePath = string.IsNullOrWhiteSpace(imagePath) ? existing.Channel.ProfileImagePath : imagePath
        };
        channels[key] = existing with { Channel = updatedChannel };
        Commit(channels.Values);
    }

    public void UpdateWatchStatus(string platform, string channelId, string vodId, string watchStatus)
    {
        var channels = LoadAll().ToDictionary(item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var key = Key(platform, channelId);
        if (!channels.TryGetValue(key, out var existing)) return;

        UpdateMedia(channels, key, existing, vodId, item => item with { WatchStatus = watchStatus, UpdatedAt = DateTimeOffset.UtcNow });
    }

    public void UpdateFavorite(string platform, string channelId, string vodId, bool favorite)
    {
        var channels = LoadAll().ToDictionary(item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var key = Key(platform, channelId);
        if (!channels.TryGetValue(key, out var existing)) return;

        UpdateMedia(channels, key, existing, vodId, item => item with { Favorite = favorite, UpdatedAt = DateTimeOffset.UtcNow });
    }

    public void UpdateMemo(string platform, string channelId, string vodId, string memo)
    {
        var channels = LoadAll().ToDictionary(item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var key = Key(platform, channelId);
        if (!channels.TryGetValue(key, out var existing)) return;

        UpdateMedia(channels, key, existing, vodId, item => item with { Memo = memo, UpdatedAt = DateTimeOffset.UtcNow });
    }

    public void UpdateLastPosition(string platform, string channelId, string vodId, double positionSeconds)
    {
        var channels = LoadAll().ToDictionary(item => Key(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
        var key = Key(platform, channelId);
        if (!channels.TryGetValue(key, out var existing)) return;

        UpdateMedia(channels, key, existing, vodId, item => item with { LastPositionSeconds = Math.Max(0, positionSeconds), UpdatedAt = DateTimeOffset.UtcNow });
    }

    private void UpdateMedia(Dictionary<string, SavedChannelData> channels, string key, SavedChannelData existing,
        string vodId, Func<SavedMedia, SavedMedia> update)
    {
        var media = existing.Media.Select(item => item.VodId.Equals(vodId, StringComparison.OrdinalIgnoreCase) ? update(item) : item).ToArray();
        channels[key] = existing with { Media = media };
        Commit(channels.Values);
    }

    private void Commit(IEnumerable<SavedChannelData> channels)
    {
        _cachedChannels = channels.ToArray();
        QueueSave(_cachedChannels);
    }

    private void QueueSave(IReadOnlyList<SavedChannelData> snapshot)
    {
        lock (_saveQueueLock)
        {
            _pendingSave = _pendingSave.ContinueWith(_ => Task.Run(() =>
            {
                try { Save(snapshot); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private void Save(IEnumerable<SavedChannelData> channels)
    {
        var payload = new DriveLibraryPayload(1, DateTimeOffset.UtcNow,
            channels.Select(item => new DriveChannelPayload(item.Channel, item.Media)).ToArray());
        var bytes = DriveLibraryCodec.Serialize(payload);
        var temporaryPath = _path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static string Key(string platform, string channelId) => $"{LibraryTransfer.NormalizePlatform(platform)}:{channelId}";
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
