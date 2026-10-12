namespace VODTracker.Domain;

public enum Platform { Chzzk, Soop }
public enum MediaKind { OnlineVod, LocalVideo, DirectUrl }
public enum Availability { Available, Missing, Unknown, Unavailable }
public enum WatchStatus { Unwatched, Watching, Watched }

public sealed record Channel(
    Guid Id, Platform Platform, string? ExternalChannelId, string DisplayName,
    string? ChannelUrl, string Description, bool IsEnabled, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record MediaItem(
    Guid Id, MediaKind Kind, Platform? Platform, string? ExternalVodId, Guid? ChannelId,
    string Title, string? SourceUrl, string? LocalPath, DateTimeOffset? PublishedAtUtc,
    long? DurationSeconds, Availability Availability, DateTimeOffset? FirstSeenAtUtc,
    DateTimeOffset? LastSeenAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record WatchRecord(
    Guid MediaItemId, WatchStatus WatchStatus, double PositionSeconds,
    DateTimeOffset? CompletedAtUtc, DateTimeOffset UpdatedAtUtc, long RowVersion);

public sealed record Note(Guid Id, Guid MediaItemId, string Body, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record RefreshRun(
    Guid Id, Guid ChannelId, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    string Status, int NewItemsCount, string? ErrorSummary);
