namespace VODTracker.Core;

/// <summary>Applies the current desktop/Drive merge policy to a platform-neutral library snapshot.</summary>
public static class DriveLibraryMerger
{
    public static IReadOnlyList<SavedChannelData> Merge(
        IEnumerable<SavedChannelData> local,
        IEnumerable<SavedChannelData> remote)
    {
        var localByKey = local.ToDictionary(item => GetChannelKey(item.Channel), StringComparer.OrdinalIgnoreCase);
        var remoteByKey = remote.ToDictionary(item => GetChannelKey(item.Channel), StringComparer.OrdinalIgnoreCase);
        var merged = new List<SavedChannelData>();

        foreach (var key in localByKey.Keys.Union(remoteByKey.Keys, StringComparer.OrdinalIgnoreCase))
        {
            localByKey.TryGetValue(key, out var localChannel);
            remoteByKey.TryGetValue(key, out var remoteChannel);
            if (localChannel is null)
            {
                merged.Add(remoteChannel!);
                continue;
            }
            if (remoteChannel is null)
            {
                merged.Add(localChannel);
                continue;
            }

            var preferredChannel = GetChannelLastUpdate(remoteChannel) > GetChannelLastUpdate(localChannel)
                ? remoteChannel.Channel
                : localChannel.Channel;
            var media = localChannel.Media.Concat(remoteChannel.Media)
                .GroupBy(item => item.VodId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt ?? item.PublishedAt ?? DateTimeOffset.MinValue).First())
                .ToArray();
            merged.Add(new SavedChannelData(preferredChannel, media));
        }

        return merged;
    }

    public static string GetChannelKey(SavedChannel channel) => GetChannelKey(channel.Platform, channel.ChannelId);

    public static string GetChannelKey(string platform, string channelId) => $"{platform}:{channelId}";

    private static DateTimeOffset GetChannelLastUpdate(SavedChannelData channel) =>
        channel.Media.Select(item => item.UpdatedAt ?? item.PublishedAt ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
}
