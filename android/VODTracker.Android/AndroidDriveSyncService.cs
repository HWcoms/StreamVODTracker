using System.Net.Http.Headers;
using System.Text.Json;
using VODTracker.Core;

namespace VODTracker.Android;

internal sealed class AndroidDriveSyncService(AndroidLibraryStore libraryStore, AndroidSettingsStore settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly AndroidDriveSyncClient _drive = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public async Task<int> ConnectAndSyncAsync(MainActivity activity, CancellationToken cancellationToken = default)
    {
        var authorization = await activity.AuthorizeGoogleDriveAsync();
        var accessToken = authorization.AccessToken
            ?? throw new InvalidOperationException("Google에서 Drive 접근 토큰을 반환하지 않았습니다.");
        var email = await GetAccountEmailAsync(accessToken, cancellationToken);
        settings.SaveGoogleDriveAccountEmail(email);
        return await SyncWithTokenAsync(accessToken, cancellationToken);
    }

    public async Task<int> SyncAsync(MainActivity activity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.LoadGoogleDriveAccountEmail()))
            throw new InvalidOperationException("먼저 Google 계정을 연결하세요.");
        var authorization = await activity.AuthorizeGoogleDriveAsync();
        return await SyncWithTokenAsync(authorization.AccessToken
            ?? throw new InvalidOperationException("Google에서 Drive 접근 토큰을 반환하지 않았습니다."), cancellationToken);
    }

    public async Task<bool> CheckForRemoteChangesAsync(MainActivity activity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.LoadGoogleDriveAccountEmail())) return false;
        var authorization = await activity.AuthorizeGoogleDriveAsync();
        var accessToken = authorization.AccessToken
            ?? throw new InvalidOperationException("Google에서 Drive 접근 토큰을 반환하지 않았습니다.");
        if (!await _drive.HasRemoteChangesAsync(accessToken, cancellationToken)) return false;
        await SyncWithTokenAsync(accessToken, cancellationToken);
        return true;
    }

    private async Task<int> SyncWithTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var local = libraryStore.LoadAll();
            var remote = await _drive.DownloadLibraryAsync(accessToken, cancellationToken);
            var merged = PreserveDeviceOnlyData(DriveLibraryMerger.Merge(local, remote), local);
            await libraryStore.ReplaceFromDriveAsync(merged);
            await _drive.UploadLibraryAsync(accessToken, merged, cancellationToken);
            return merged.Count;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static IReadOnlyList<SavedChannelData> PreserveDeviceOnlyData(
        IReadOnlyList<SavedChannelData> merged, IReadOnlyList<SavedChannelData> local)
    {
        var localByChannel = local.ToDictionary(channel => DriveLibraryMerger.GetChannelKey(channel.Channel), StringComparer.OrdinalIgnoreCase);
        return merged.Select(channel =>
        {
            if (!localByChannel.TryGetValue(DriveLibraryMerger.GetChannelKey(channel.Channel), out var localChannel))
                return channel;
            var localMedia = localChannel.Media.ToDictionary(media => media.VodId, StringComparer.OrdinalIgnoreCase);
            return channel with
            {
                Channel = channel.Channel with
                {
                    ProfileImageUrl = localChannel.Channel.ProfileImageUrl,
                    ProfileImagePath = localChannel.Channel.ProfileImagePath
                },
                Media = channel.Media.Select(media =>
                {
                    if (!localMedia.TryGetValue(media.VodId, out var localItem)) return media;
                    return media with
                    {
                        ThumbnailUrl = localItem.ThumbnailUrl ?? media.ThumbnailUrl,
                        ThumbnailPath = localItem.ThumbnailPath,
                        LocalFilePath = localItem.LocalFilePath ?? media.LocalFilePath
                    };
                }).ToArray()
            };
        }).ToArray();
    }

    private static async Task<string> GetAccountEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google 계정 이메일을 확인하지 못했습니다. " + body);
        using var document = JsonDocument.Parse(body);
        var email = document.RootElement.TryGetProperty("email", out var emailProperty) ? emailProperty.GetString() : null;
        return string.IsNullOrWhiteSpace(email)
            ? throw new InvalidOperationException("Google에서 계정 이메일을 반환하지 않았습니다.")
            : email;
    }
}
