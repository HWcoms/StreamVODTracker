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
}
