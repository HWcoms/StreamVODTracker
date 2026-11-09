using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VODTracker.Core;

namespace StreamVODTracker;

internal sealed class GoogleDriveSyncClient
{
    private const string SyncFileName = "StreamVODTracker.sync.json";
    private const string SyncFolderName = "StreamVODTracker";
    private const string DriveFolderMimeType = "application/vnd.google-apps.folder";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly string _clientId;
    private readonly string _clientSecret;
    private GoogleDriveCredentials _credentials;
    private string? _fileId;
    private string? _folderId;
    private string? _lastModifiedTime;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public GoogleDriveSyncClient(string clientId, string clientSecret, GoogleDriveCredentials credentials)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _credentials = credentials;
    }

    public async Task<IReadOnlyList<SavedChannelData>> DownloadLibraryAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        _fileId = await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (_fileId is null) return [];
        _lastModifiedTime = await GetModifiedTimeAsync(accessToken, _fileId, cancellationToken);
        using var request = AuthorizedRequest(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(_fileId)}?alt=media", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not download the VOD Tracker sync file.");
        var payload = DriveLibraryCodec.Deserialize(body);
        return payload.Channels.Select(channel => new SavedChannelData(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
    }

    public async Task<bool> HasRemoteChangesAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        _fileId ??= await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (_fileId is null) return false;
        var modifiedTime = await GetModifiedTimeAsync(accessToken, _fileId, cancellationToken);
        return modifiedTime is not null && !string.Equals(modifiedTime, _lastModifiedTime, StringComparison.Ordinal);
    }

    public async Task UploadLibraryAsync(IEnumerable<SavedChannelData> library, CancellationToken cancellationToken = default)
    {
        var channels = library.Select(channel => new DriveChannelPayload(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
        var bytes = DriveLibraryCodec.Serialize(new DriveLibraryPayload(1, DateTimeOffset.UtcNow, channels));
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        _fileId ??= await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (_fileId is null)
            _fileId = await CreateSyncFileAsync(accessToken, _folderId!, bytes, cancellationToken);
        else
            await UpdateSyncFileAsync(accessToken, _fileId, bytes, cancellationToken);
        if (_fileId is not null) _lastModifiedTime = await GetModifiedTimeAsync(accessToken, _fileId, cancellationToken);
    }

    private static async Task<string?> GetModifiedTimeAsync(string accessToken, string fileId, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields=modifiedTime,trashed", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not check the Google Drive sync file status.");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("trashed", out var trashed) && trashed.GetBoolean()) return null;
        return document.RootElement.TryGetProperty("modifiedTime", out var modifiedTime) ? modifiedTime.GetString() : null;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            return _accessToken;
        var session = await GoogleDriveOAuthClient.RefreshAsync(_clientId, _clientSecret, _credentials, cancellationToken);
        _credentials = _credentials with { RefreshToken = session.RefreshToken };
        _accessToken = session.AccessToken;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, session.ExpiresIn - 120));
        return _accessToken;
    }

    private async Task<string?> FindOrMigrateSyncFileAsync(string accessToken, CancellationToken cancellationToken)
    {
        _folderId ??= await FindOrCreateSyncFolderAsync(accessToken, cancellationToken);
        var file = await FindSyncFileInFolderAsync(accessToken, _folderId, cancellationToken);
        if (file is not null) return file.Id;

        // Migrate the previously used root-level JSON file into the app folder.
        var legacyFile = await FindSyncFileInFolderAsync(accessToken, "root", cancellationToken);
        if (legacyFile is null) return null;
        await MoveFileToFolderAsync(accessToken, legacyFile, _folderId, cancellationToken);
        return legacyFile.Id;
    }

    private static async Task<DriveFileReference?> FindSyncFileInFolderAsync(
        string accessToken, string parentId, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{SyncFileName}' and '{parentId}' in parents and trashed = false");
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files?q={query}&spaces=drive&pageSize=10&fields=files(id,name,parents)", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, "Could not search Google Drive for the sync file.");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return null;
        var file = files[0];
        var id = file.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var parents = file.TryGetProperty("parents", out var parentElements)
            ? parentElements.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray()
            : [];
        return new DriveFileReference(id, parents);
    }

    private async Task<string> FindOrCreateSyncFolderAsync(string accessToken, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{SyncFolderName}' and mimeType = '{DriveFolderMimeType}' and 'root' in parents and trashed = false");
        using (var request = AuthorizedRequest(HttpMethod.Get,
                   $"https://www.googleapis.com/drive/v3/files?q={query}&spaces=drive&pageSize=10&fields=files(id,name)", accessToken))
        using (var response = await Http.SendAsync(request, cancellationToken))
        {
            var body = await ReadBodyAsync(response, "Could not search Google Drive for the app folder.");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0 &&
                files[0].TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString()!;
        }

        using var createRequest = AuthorizedRequest(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id", accessToken);
        createRequest.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { name = SyncFolderName, mimeType = DriveFolderMimeType }),
            Encoding.UTF8, "application/json");
        using var createResponse = await Http.SendAsync(createRequest, cancellationToken);
        var createBody = await ReadBodyAsync(createResponse, "Could not create the app folder in Google Drive.");
        using var createdFolder = JsonDocument.Parse(createBody);
        return createdFolder.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Google Drive did not return a folder ID.");
    }

    private static async Task MoveFileToFolderAsync(string accessToken, DriveFileReference file, string folderId,
        CancellationToken cancellationToken)
    {
        var parameters = new List<string> { $"addParents={Uri.EscapeDataString(folderId)}" };
        if (file.Parents.FirstOrDefault() is { Length: > 0 } currentParent)
            parameters.Add($"removeParents={Uri.EscapeDataString(currentParent)}");
        using var request = AuthorizedRequest(HttpMethod.Patch,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(file.Id)}?{string.Join('&', parameters)}", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        _ = await ReadBodyAsync(response, "Could not move the existing sync file into the app folder.");
    }

    private static async Task<string> CreateSyncFileAsync(string accessToken, string folderId, byte[] bytes, CancellationToken cancellationToken)
    {
        using var content = new MultipartContent("related");
        content.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(new { name = SyncFileName, mimeType = "application/json", parents = new[] { folderId } }), Encoding.UTF8, "application/json"));
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

    private sealed record DriveFileReference(string Id, string[] Parents);

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
