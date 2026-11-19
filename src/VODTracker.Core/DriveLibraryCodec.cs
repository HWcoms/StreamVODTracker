using System.Text.Json;

namespace VODTracker.Core;

/// <summary>Shared JSON codec for the Windows and Android Google Drive sync file.</summary>
public static class DriveLibraryCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DriveLibraryPayload Deserialize(string json) =>
        JsonSerializer.Deserialize<DriveLibraryPayload>(json, JsonOptions)
        ?? throw new JsonException("The Drive library payload was empty.");

    public static byte[] Serialize(DriveLibraryPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public static bool HasSameContent(IEnumerable<SavedChannelData> left, IEnumerable<SavedChannelData> right)
    {
        static byte[] Encode(IEnumerable<SavedChannelData> channels)
        {
            var payload = channels
                .Select(channel => new DriveChannelPayload(
                    channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
                    channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null })
                        .OrderBy(media => media.VodId, StringComparer.OrdinalIgnoreCase).ToArray()))
                .OrderBy(channel => DriveLibraryMerger.GetChannelKey(channel.Channel), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        }

        return Encode(left).AsSpan().SequenceEqual(Encode(right));
    }
}
