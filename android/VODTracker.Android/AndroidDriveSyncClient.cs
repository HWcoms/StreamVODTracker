using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VODTracker.Core;

namespace VODTracker.Android;

internal sealed class AndroidDriveSyncClient
{
    private const string SyncFileName = "StreamVODTracker.sync.json";
    private const string SyncFolderName = "StreamVODTracker";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private string? _fileId;
    private string? _folderId;
    private string? _lastModifiedTime;

    public async Task<IReadOnlyList<SavedChannelData>> DownloadLibraryAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var fileId = await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (fileId is null)
        {
            _lastModifiedTime = null;
            return [];
        }

        _lastModifiedTime = await GetModifiedTimeAsync(accessToken, fileId, cancellationToken);
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var payload = DriveLibraryCodec.Deserialize(await ReadBodyAsync(response, "Google Drive 동기화 파일을 다운로드하지 못했습니다.", cancellationToken));
        return payload.Channels.Select(channel => new SavedChannelData(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
    }

    public async Task<bool> HasRemoteChangesAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var fileId = _fileId ?? await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (fileId is null) return false;
        var modifiedTime = await GetModifiedTimeAsync(accessToken, fileId, cancellationToken);
        return modifiedTime is not null && !string.Equals(modifiedTime, _lastModifiedTime, StringComparison.Ordinal);
    }

    public async Task UploadLibraryAsync(string accessToken, IEnumerable<SavedChannelData> library, CancellationToken cancellationToken = default)
    {
        var channels = library.Select(channel => new DriveChannelPayload(
            channel.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
            channel.Media.Select(media => media with { ThumbnailUrl = null, ThumbnailPath = null }).ToArray())).ToArray();
        var bytes = DriveLibraryCodec.Serialize(new DriveLibraryPayload(1, DateTimeOffset.UtcNow, channels));
        var fileId = await FindOrMigrateSyncFileAsync(accessToken, cancellationToken);
        if (fileId is null)
            _fileId = await CreateSyncFileAsync(accessToken, _folderId!, bytes, cancellationToken);
        else
        {
            await UpdateSyncFileAsync(accessToken, fileId, bytes, cancellationToken);
            _fileId = fileId;
        }
        _lastModifiedTime = await GetModifiedTimeAsync(accessToken, _fileId!, cancellationToken);
    }

    private async Task<string?> FindOrMigrateSyncFileAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (_fileId is not null) return _fileId;
        _folderId ??= await FindOrCreateSyncFolderAsync(accessToken, cancellationToken);
        var file = await FindSyncFileInFolderAsync(accessToken, _folderId, cancellationToken);
        if (file is null) file = await FindSyncFileInFolderAsync(accessToken, "root", cancellationToken);
        if (file is null) return null;
        if (!file.Parents.Contains(_folderId, StringComparer.Ordinal))
            await MoveFileToFolderAsync(accessToken, file, _folderId, cancellationToken);
        _fileId = file.Id;
        return file.Id;
    }

    private static async Task<string?> GetModifiedTimeAsync(string accessToken, string fileId, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields=modifiedTime,trashed", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await ReadBodyAsync(response, "Google Drive 동기화 상태를 확인하지 못했습니다.", cancellationToken));
        if (document.RootElement.TryGetProperty("trashed", out var trashed) && trashed.GetBoolean()) return null;
        return document.RootElement.TryGetProperty("modifiedTime", out var modifiedTime) ? modifiedTime.GetString() : null;
    }

    private static async Task<DriveFile?> FindSyncFileInFolderAsync(string accessToken, string parentId, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{SyncFileName}' and '{parentId}' in parents and trashed = false");
        using var request = AuthorizedRequest(HttpMethod.Get,
            $"https://www.googleapis.com/drive/v3/files?q={query}&spaces=drive&pageSize=10&fields=files(id,name,parents)", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await ReadBodyAsync(response, "Google Drive에서 동기화 파일을 찾지 못했습니다.", cancellationToken));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return null;
        var item = files[0];
        var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var parents = item.TryGetProperty("parents", out var parentElements)
            ? parentElements.EnumerateArray().Select(parent => parent.GetString()).Where(parent => !string.IsNullOrWhiteSpace(parent)).Cast<string>().ToArray()
            : [];
        return new DriveFile(id, parents);
    }

    private async Task<string> FindOrCreateSyncFolderAsync(string accessToken, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"name = '{SyncFolderName}' and mimeType = '{FolderMimeType}' and 'root' in parents and trashed = false");
        using (var request = AuthorizedRequest(HttpMethod.Get,
                   $"https://www.googleapis.com/drive/v3/files?q={query}&spaces=drive&pageSize=10&fields=files(id)", accessToken))
        using (var response = await Http.SendAsync(request, cancellationToken))
        using (var document = JsonDocument.Parse(await ReadBodyAsync(response, "Google Drive 폴더를 확인하지 못했습니다.", cancellationToken)))
        {
            if (document.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0 &&
                files[0].TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString()!;
        }

        using var createRequest = AuthorizedRequest(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id", accessToken);
        createRequest.Content = new StringContent(JsonSerializer.Serialize(new { name = SyncFolderName, mimeType = FolderMimeType }), Encoding.UTF8, "application/json");
        using var createResponse = await Http.SendAsync(createRequest, cancellationToken);
        using var created = JsonDocument.Parse(await ReadBodyAsync(createResponse, "Google Drive 폴더를 만들지 못했습니다.", cancellationToken));
        return created.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Google Drive에서 폴더 ID를 받지 못했습니다.");
    }

    private static async Task MoveFileToFolderAsync(string accessToken, DriveFile file, string folderId, CancellationToken cancellationToken)
    {
        var parameters = new List<string> { $"addParents={Uri.EscapeDataString(folderId)}" };
        if (file.Parents.FirstOrDefault() is { Length: > 0 } currentParent)
            parameters.Add($"removeParents={Uri.EscapeDataString(currentParent)}");
        using var request = AuthorizedRequest(HttpMethod.Patch,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(file.Id)}?{string.Join('&', parameters)}", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        _ = await ReadBodyAsync(response, "기존 Google Drive 동기화 파일을 이동하지 못했습니다.", cancellationToken);
    }

    private static async Task<string> CreateSyncFileAsync(string accessToken, string folderId, byte[] bytes, CancellationToken cancellationToken)
    {
        using var content = new MultipartContent("related");
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            name = SyncFileName,
            mimeType = "application/json",
            parents = new[] { folderId }
        }), Encoding.UTF8, "application/json"));
        var media = new ByteArrayContent(bytes);
        media.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(media);
        using var request = AuthorizedRequest(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id", accessToken);
        request.Content = content;
        using var response = await Http.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await ReadBodyAsync(response, "Google Drive 동기화 파일을 만들지 못했습니다.", cancellationToken));
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Google Drive에서 동기화 파일 ID를 받지 못했습니다.");
    }

    private static async Task UpdateSyncFileAsync(string accessToken, string fileId, byte[] bytes, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Patch,
            $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media", accessToken);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        _ = await ReadBodyAsync(response, "Google Drive 동기화 파일을 갱신하지 못했습니다.", cancellationToken);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = fallback;
            try
            {
                using var error = JsonDocument.Parse(body);
                var detail = error.RootElement.GetProperty("error").GetProperty("message").GetString();
                if (!string.IsNullOrWhiteSpace(detail)) message += $" ({detail})";
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            throw new InvalidOperationException(message);
        }
        return body;
    }

    private sealed record DriveFile(string Id, string[] Parents);
}
