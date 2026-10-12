namespace VODTracker.Contracts;

public sealed record HealthResponse(string Status, string Version, DateTimeOffset Utc);
public sealed record CreateChannelRequest(string Platform, string DisplayName, string Description, string? ExternalChannelId, string? ChannelUrl);
public sealed record ChannelResponse(Guid Id, string Platform, string? ExternalChannelId, string DisplayName, string Description, string? ChannelUrl, bool IsEnabled);
public sealed record CreateMediaRequest(string Kind, string? Platform, Guid? ChannelId, string Title, string? Url, string? LocalPath, DateTimeOffset? PublishedAtUtc);
public sealed record UpdateWatchRequest(string Status, double PositionSeconds, long RowVersion);
public sealed record UpdateNoteRequest(string Body);
