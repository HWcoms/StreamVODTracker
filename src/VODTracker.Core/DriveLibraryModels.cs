namespace VODTracker.Core;

public sealed record SavedChannel(
    string Platform,
    string ChannelId,
    string Name,
    string Description,
    string Url,
    string? ProfileImageUrl,
    string? ProfileImagePath);

public sealed record SavedMedia(
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

public sealed record SavedChannelData(SavedChannel Channel, IReadOnlyList<SavedMedia> Media);

public sealed record DriveChannelPayload(SavedChannel Channel, IReadOnlyList<SavedMedia> Media);

public sealed record DriveLibraryPayload(int Version, DateTimeOffset UpdatedAt, IReadOnlyList<DriveChannelPayload> Channels);
