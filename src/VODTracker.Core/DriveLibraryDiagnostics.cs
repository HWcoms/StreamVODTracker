using System.Security.Cryptography;
using System.Text;

namespace VODTracker.Core;

/// <summary>Produces value-safe diagnostics for cross-device sync comparisons.</summary>
public static class DriveLibraryDiagnostics
{
    public static IReadOnlyList<string> DescribeDifferences(string stage,
        IEnumerable<SavedChannelData> left, IEnumerable<SavedChannelData> right)
    {
        var rightItems = right.SelectMany(channel => channel.Media.Select(media =>
                (Key: $"{DriveLibraryMerger.GetChannelKey(channel.Channel)}:{media.VodId}", Media: media)))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Media, StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        foreach (var (channel, media) in left.SelectMany(channel => channel.Media.Select(media => (channel, media))))
        {
            var key = $"{DriveLibraryMerger.GetChannelKey(channel.Channel)}:{media.VodId}";
            if (!rightItems.TryGetValue(key, out var other) ||
                (media.Favorite == other.Favorite && string.Equals(media.Memo, other.Memo, StringComparison.Ordinal)))
                continue;

            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
            messages.Add($"{stage} card={fingerprint} leftUpdatedAt={Format(media.UpdatedAt)} rightUpdatedAt={Format(other.UpdatedAt)} " +
                         $"favorite={media.Favorite}/{other.Favorite} memoChanged={!string.Equals(media.Memo, other.Memo, StringComparison.Ordinal)}");
        }
        return messages;
    }

    private static string Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O") ?? "null";
}
