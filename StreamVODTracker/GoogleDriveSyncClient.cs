using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StreamVODTracker;

internal sealed record DriveChannelPayload(SavedChannel Channel, IReadOnlyList<SavedMedia> Media);
internal sealed record DriveLibraryPayload(int Version, DateTimeOffset UpdatedAt, IReadOnlyList<DriveChannelPayload> Channels);

internal sealed class GoogleDriveSyncClient
{
    private const string SyncFileName = "StreamVODTracker.sync.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _clientId;
    private GoogleDriveCredentials _credentials;
    private string? _fileId;

    public GoogleDriveSyncClient(string clientId, GoogleDriveCredentials credentials)
    {
        _clientId = clientId;
        _credentials = credentials;
    }

    public async Task<IReadOnlyList<SavedChannelData>> DownloadLibraryAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        _fileId = await FindSyncFileIdAsync(accessToken, cancellationToken);
        if (_fileId is null) return [];
        using var request = AuthorizedRequest(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(_fileId)}?alt=media", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not download the VOD Tracker sync file.");
        var payload = JsonSerializer.Deserialize<DriveLibraryPayload>(body, JsonOptions);
        if (payload is null) return [];
        return payload.Channels.Select(channel => new SavedChannelData(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
    }

    public async Task UploadLibraryAsync(IEnumerable<SavedChannelData> library, CancellationToken cancellationToken = default)
    {
        var channels = library.Select(channel => new DriveChannelPayload(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DriveLibraryPayload(1, DateTimeOffset.UtcNow, channels), JsonOptions);
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        _fileId ??= await FindSyncFileIdAsync(accessToken, cancellationToken);
        if (_fileId is null)
            _fileId = await CreateSyncFileAsync(accessToken, bytes, cancellationToken);
        else
            await UpdateSyncFileAsync(accessToken, _fileId, bytes, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var session = await GoogleDriveOAuthClient.RefreshAsync(_clientId, _credentials, cancellationToken);
        _credentials = _credentials with { RefreshToken = session.RefreshToken };
        return session.AccessToken;
    }

    private static async Task<string?> FindSyncFileIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{SyncFileName}' and trashed = false");
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files?q={query}&spaces=drive&pageSize=10&fields=files(id,name,modifiedTime)", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not search Google Drive for the sync file.");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return null;
        return files[0].TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static async Task<string> CreateSyncFileAsync(string accessToken, byte[] bytes, CancellationToken cancellationToken)
    {
        using var content = new MultipartContent("related");
        content.Add(new StringContent(JsonSerializer.Serialize(new { name = SyncFileName, mimeType = "application/json" }), Encoding.UTF8, "application/json"));
        var media = new ByteArrayContent(bytes);
        media.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(media);
        using var request = AuthorizedRequest(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id", accessToken);
        request.Content = content;
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not create the sync file in Google Drive.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Google Drive did not return a sync file ID.");
    }

    private static async Task UpdateSyncFileAsync(string accessToken, string fileId, byte[] bytes, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Patch,
            $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media", accessToken);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        _ = await ReadBodyAsync(response, "Could not update the sync file in Google Drive.");
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, string fallback)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var message = fallback;
            try
            {
                using var error = JsonDocument.Parse(body);
                var details = error.RootElement.GetProperty("error").GetProperty("message").GetString();
                if (!string.IsNullOrWhiteSpace(details)) message += $" ({details})";
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            throw new InvalidOperationException(message);
        }
        return body;
    }
}
