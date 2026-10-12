using VODTracker.Domain;

namespace VODTracker.Application;

public interface IVodProvider
{
    Platform Platform { get; }
    bool IsConfigured { get; }
    Task<IReadOnlyList<DiscoveredVod>> FetchAsync(Channel channel, CancellationToken cancellationToken);
}

public sealed record DiscoveredVod(string ExternalVodId, string Title, string Url, DateTimeOffset? PublishedAtUtc, long? DurationSeconds);

public interface IChannelRepository
{
    Task<IReadOnlyList<Channel>> GetChannelsAsync(Platform? platform, CancellationToken cancellationToken = default);
    Task<Channel> AddChannelAsync(Channel channel, CancellationToken cancellationToken = default);
    Task<bool> DeleteChannelAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaItem>> GetMediaAsync(Platform? platform = null, CancellationToken cancellationToken = default);
    Task<MediaItem> AddMediaAsync(MediaItem item, CancellationToken cancellationToken = default);
    Task UpsertDiscoveredAsync(Channel channel, IReadOnlyList<DiscoveredVod> items, CancellationToken cancellationToken = default);
    Task<WatchRecord?> GetWatchRecordAsync(Guid mediaItemId, CancellationToken cancellationToken = default);
    Task<WatchRecord> UpdateWatchRecordAsync(Guid mediaItemId, WatchStatus status, double positionSeconds, long expectedRowVersion, CancellationToken cancellationToken = default);
    Task SaveNoteAsync(Guid mediaItemId, string body, CancellationToken cancellationToken = default);
}
