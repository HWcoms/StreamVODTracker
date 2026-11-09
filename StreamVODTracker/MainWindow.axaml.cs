using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using VODTracker.Core;

namespace StreamVODTracker;

public partial class MainWindow : Window
{
    public static string UnselectedCardBorderColor { get; private set; } = "#C3C7D0";
    private readonly ObservableCollection<MediaCard> _allCards = [];
    private readonly ObservableCollection<MediaCard> _visibleCards = [];
    private readonly List<MediaCard> _filteredCards = [];
    private readonly Dictionary<string, SavedChannelData> _lastSavedDriveSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _soopNextPageByChannel = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _soopHasMoreByChannel = new(StringComparer.OrdinalIgnoreCase);
    private int _cardDisplayLimit = 120;
    private readonly ObservableCollection<ChannelEntry> _channels = [];
    private readonly ObservableCollection<ChannelEntry> _visibleChannels = [];
    private readonly HashSet<string> _selectedChannelUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Border> _channelCardVisuals = [];
    private readonly HashSet<string> _channelDragSelectionBase = new(StringComparer.OrdinalIgnoreCase);
    private bool _isChannelPointerTracking;
    private bool _isChannelLassoSelecting;
    private Point _channelLassoStart;
    private ChannelEntry? _channelPressedTarget;
    private ChannelEntry? _channelSelectionAnchor;
    private KeyModifiers _channelPressModifiers;
    private readonly AppSettingsStore _settingsStore = new(AppContext.BaseDirectory);
    private readonly List<Border> _cardVisuals = [];
    private readonly HashSet<MediaCard> _dragSelectionBase = [];
    private readonly HashSet<string> _durationRefreshInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<MediaCard> _durationRefreshAttempted = [];
    private readonly Dictionary<string, CancellationTokenSource> _durationRefreshCancellation = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim SoopDurationLoadLimiter = new(4);
    private static readonly HttpClient SoopDurationHttpClient = CreateSoopDurationHttpClient();
    private readonly LocalCsvDatabase _localDatabase;
    private readonly IVodRepository _vodRepository;
    private GoogleDriveSyncClient? _driveSyncClient;
    private bool _isSyncingDrive;
    private bool _isCheckingDrive;
    private bool _driveUploadPending;
    private readonly DispatcherTimer _driveUploadTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _driveCheckTimer = new();
    private bool _isWindowActive;
    private static readonly SemaphoreSlim ThumbnailLoadLimiter = new(4);
    private ChannelEntry? _selectedChannel;
    private bool _isListView;
    private bool _showThumbnails = true;
    private bool _showFavoritesOnly;
    private readonly DispatcherTimer _channelStatusTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly DispatcherTimer _searchDurationRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _refreshNotificationTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _driveSyncNotificationTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _syncSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private double _syncSpinnerAngle;
    private bool _isDarkTheme;
    private bool _isLoadingSettings = true;
    private bool _sortDescending = true;
    private bool _isAllLibraryView;
    private bool _isShowAllChannels;
    private bool _isLoadingMoreAtScrollEnd;

    public MainWindow()
    {
        InitializeComponent();
        UpdateViewButtonStates();
        _sortDescending = _settingsStore.LoadSortDescending();
        _cardDisplayLimit = _settingsStore.LoadCardBatchSize();
        UpdateSortDirectionButton();
        SortOrder.SelectedIndex = _settingsStore.LoadSortOrder() switch
        {
            "RecentAdded" => 1,
            "Title" => 2,
            _ => 0
        };
        _isLoadingSettings = false;
        _localDatabase = new LocalCsvDatabase(AppContext.BaseDirectory);
        _vodRepository = _localDatabase;
        AddHandler(InputElement.PointerPressedEvent, Window_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(InputElement.PointerMovedEvent, Window_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, Window_PointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        CardsArea.AddHandler(InputElement.PointerPressedEvent, CardsArea_PointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        CardsList.ItemsSource = _visibleCards;
        CardsListRows.ItemsSource = _visibleCards;
        ChannelCards.ItemsSource = _visibleChannels;
        LicenseEntries.ItemsSource = BuildLicenseEntries();
        LoadLocalDatabase();
        _isAllLibraryView = true;
        PlatformTabs.SelectedIndex = -1;
        UpdateNavigationSelection("all");
        ApplyTheme(_settingsStore.LoadIsDarkTheme());
        AppLogger.Info("MainWindow", $"Loaded local library. Channels={_channels.Count}; Cards={_allCards.Count}");
        _searchDurationRefreshTimer.Tick += SearchDurationRefreshTimer_Tick;
        _channelStatusTimer.Tick += ChannelStatusTimer_Tick;
        _refreshNotificationTimer.Tick += (_, _) =>
        {
            _refreshNotificationTimer.Stop();
            RefreshNotification.IsVisible = false;
        };
        _driveSyncNotificationTimer.Tick += (_, _) =>
        {
            _driveSyncNotificationTimer.Stop();
            DriveSyncNotification.IsVisible = false;
        };
        DriveSyncNotificationSpinner.RenderTransform = new RotateTransform();
        _syncSpinnerTimer.Tick += (_, _) =>
        {
            _syncSpinnerAngle = (_syncSpinnerAngle + 30) % 360;
            ((RotateTransform)DriveSyncNotificationSpinner.RenderTransform!).Angle = _syncSpinnerAngle;
        };
        _driveUploadTimer.Tick += DriveUploadTimer_Tick;
        _driveCheckTimer.Interval = TimeSpan.FromSeconds(_settingsStore.LoadDriveCheckIntervalSeconds());
        _driveCheckTimer.Tick += DriveCheckTimer_Tick;
        _channelStatusTimer.Start();
        _ = RestoreGoogleDriveAndSyncAsync();
        Activated += async (_, _) =>
        {
            _isWindowActive = true;
            _driveCheckTimer.Start();
            RefreshUnknownVisibleCardDurations();
            await CheckDriveForChangesAsync();
        };
        Deactivated += (_, _) =>
        {
            _isWindowActive = false;
            _driveCheckTimer.Stop();
        };
        Closed += (_, _) =>
        {
            AppLogger.Info("MainWindow", "Window closed.");
            _channelStatusTimer.Stop();
            _searchDurationRefreshTimer.Stop();
            _refreshNotificationTimer.Stop();
            _driveSyncNotificationTimer.Stop();
            _syncSpinnerTimer.Stop();
            _driveUploadTimer.Stop();
            _driveCheckTimer.Stop();
            foreach (var channel in _channels) channel.Dispose();
            foreach (var card in _allCards) card.Dispose();
        };
        UpdateCards();
        RefreshUnknownVisibleCardDurations();
    }

    private async void ChannelStatusTimer_Tick(object? sender, EventArgs e) => await RefreshChannelStatusesAsync();

    private async Task RefreshChannelStatusesAsync()
    {
        var refreshTasks = _channels.Select(async channel =>
        {
            try
            {
                var id = channel.ChannelId;
                var platformKey = channel.Platform == "SOOP" ? "SOOP" : "CHZZK";
                var profile = await LookupChannelProfileAsync(platformKey, Uri.UnescapeDataString(id));
                channel.IsLive = profile.IsLive;
                if (string.IsNullOrWhiteSpace(channel.ProfileImagePath) || !File.Exists(channel.ProfileImagePath))
                {
                    var imageUrl = profile.ImageUrl;
                    if (string.IsNullOrWhiteSpace(imageUrl) && platformKey == "SOOP") imageUrl = BuildSoopProfileImageUrl(id);
                    channel.ProfileImageUrl = imageUrl;
                    SaveChannel(channel);
                    if (!string.IsNullOrWhiteSpace(imageUrl) && !await LoadChannelImageAsync(channel, imageUrl) && platformKey == "SOOP")
                    {
                        var fallbackUrl = BuildSoopProfileImageUrl(id);
                        if (!string.Equals(imageUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            channel.ProfileImageUrl = fallbackUrl;
                            SaveChannel(channel);
                            await LoadChannelImageAsync(channel, fallbackUrl);
                        }
                    }
                }
                SaveChannel(channel);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Keep the last known live state if the service is temporarily unavailable.
            }
        });
        await Task.WhenAll(refreshTasks);
    }

    private async Task<bool> LoadChannelImageAsync(ChannelEntry channel, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return false;
        var normalizedImageUrl = imageUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{imageUrl}" : imageUrl;
        if (!Uri.TryCreate(normalizedImageUrl, UriKind.Absolute, out var imageUri) ||
            (imageUri.Scheme != Uri.UriSchemeHttps && imageUri.Scheme != Uri.UriSchemeHttp))
            return false;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Referrer = new Uri(channel.Platform == "SOOP" ? "https://www.sooplive.com/" : "https://chzzk.naver.com/");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var imageBytes = await response.Content.ReadAsByteArrayAsync();
            var imagePath = _localDatabase.SaveChannelProfileImage(channel.Platform, channel.ChannelId, imageBytes,
                response.Content.Headers.ContentType?.MediaType);
            channel.ProfileImageUrl = normalizedImageUrl;
            channel.SetProfileImage(new Bitmap(imagePath), imagePath);
            SaveChannel(channel);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Keep the initials placeholder when the profile image cannot be loaded.
            return false;
        }
    }

    private void LoadLocalDatabase()
    {
        _lastSavedDriveSnapshots.Clear();
        foreach (var saved in _vodRepository.LoadAll())
        {
            _lastSavedDriveSnapshots[GetSyncChannelKey(saved.Channel.Platform, saved.Channel.ChannelId)] =
                NormalizeDriveSnapshot(saved);
            var channel = new ChannelEntry(saved.Channel.Platform, saved.Channel.ChannelId, saved.Channel.Name,
                saved.Channel.Description, saved.Channel.Url);
            channel.ProfileImageUrl = saved.Channel.ProfileImageUrl;
            channel.ProfileImagePath = saved.Channel.ProfileImagePath;
            _channels.Add(channel);
            if (!string.IsNullOrWhiteSpace(channel.ProfileImagePath) && File.Exists(channel.ProfileImagePath))
            {
                try
                {
                    channel.SetProfileImage(new Bitmap(channel.ProfileImagePath), channel.ProfileImagePath);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
            }
            foreach (var item in saved.Media)
            {
                var card = new MediaCard(item.Title, item.Platform, item.SourceType, item.SourceLabel,
                    item.Availability, item.WatchStatus, item.SourceValue, saved.Channel.Url,
                    item.PublishedAt, item.ThumbnailUrl, item.ThumbnailPath, item.OnlineUrl, item.LocalFilePath,
                    item.VodId, item.LastPositionSeconds, item.Memo, item.UpdatedAt, isFavorite: item.Favorite);
                _allCards.Add(card);
                if (card.HasLocalFilePath) _ = DetectLocalDurationAsync(card);
                if (_showThumbnails && !string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    try { card.SetThumbnail(new Bitmap(item.ThumbnailPath)); }
                    catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
                }
                if (_showThumbnails && IsHttpUrl(card.OnlineUrl))
                    _ = RefreshCardThumbnailAsync(card);
                else if (_showThumbnails && !card.HasThumbnail && !string.IsNullOrWhiteSpace(item.ThumbnailUrl))
                {
                    _ = LoadThumbnailAsync(card, item.ThumbnailUrl);
                }
            }
            SaveChannel(channel);
            _ = RefreshChannelMetadataAsync(channel);
        }
        ChannelEmptyState.IsVisible = _channels.Count == 0;
        ChannelScroller.IsVisible = _channels.Count > 0;
    }

    private void SaveChannel(ChannelEntry channel)
    {
        if (_channels.Contains(channel))
        {
            var cards = _allCards.Where(card => card.ChannelUrl == channel.ChannelUrl).ToArray();
            var snapshot = CreateDriveSnapshot(channel, cards);
            var key = GetSyncChannelKey(channel.Platform, channel.ChannelId);
            var hasDriveDataChanged = !_lastSavedDriveSnapshots.TryGetValue(key, out var previousSnapshot) ||
                                      !DriveSnapshotsEqual(previousSnapshot, snapshot);
            _vodRepository.Save(channel, cards);
            _lastSavedDriveSnapshots[key] = snapshot;
            if (hasDriveDataChanged && !_isSyncingDrive && _driveSyncClient is not null)
            {
                _driveUploadPending = true;
                ShowGoogleDriveSyncProgress();
                _driveUploadTimer.Stop();
                _driveUploadTimer.Start();
            }
        }
    }

    private static SavedChannelData CreateDriveSnapshot(ChannelEntry channel, IEnumerable<MediaCard> cards)
    {
        var savedChannel = new SavedChannel(channel.Platform, channel.ChannelId, channel.DisplayName,
            channel.Description, channel.ChannelUrl, null, null);
        var savedMedia = cards.Select(card => new SavedMedia(
            card.Title,
            card.PlatformLabel,
            string.IsNullOrWhiteSpace(card.OnlineUrl) ? "파일" : "URL",
            "",
            "",
            card.PublishedAt,
            card.WatchStatusLabel,
            null,
            null,
            "",
            card.OnlineUrl,
            card.LocalFilePath,
            card.VodId,
            card.LastPositionSeconds,
            card.Memo,
            card.UpdatedAt,
            card.IsFavorite)).ToArray();
        return NormalizeDriveSnapshot(new SavedChannelData(savedChannel, savedMedia));
    }

    private static bool DriveSnapshotsEqual(SavedChannelData left, SavedChannelData right) =>
        left.Channel == right.Channel && left.Media.SequenceEqual(right.Media);

    private static SavedChannelData NormalizeDriveSnapshot(SavedChannelData saved) => new(
        saved.Channel with { ProfileImageUrl = null, ProfileImagePath = null },
        saved.Media.Select(media => media with
        {
            SourceLabel = "",
            SourceValue = "",
            ThumbnailUrl = null,
            ThumbnailPath = null,
            Availability = "",
            UpdatedAt = null
        }).OrderBy(media => media.VodId, StringComparer.OrdinalIgnoreCase).ToArray());

    private async void DriveUploadTimer_Tick(object? sender, EventArgs e)
    {
        _driveUploadTimer.Stop();
        if (_driveSyncClient is null || !_driveUploadPending) return;
        _driveUploadPending = false;
        ShowGoogleDriveSyncProgress();
        try
        {
            await _driveSyncClient.UploadLibraryAsync(_localDatabase.LoadAll());
            ShowDriveSyncNotification("Google Drive와 동기화 완료");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Google Drive background sync failed: {exception.Message}");
            ShowDriveSyncNotification($"Google Drive sync failed: {exception.Message}", isError: true);
        }
    }

    private async Task RestoreGoogleDriveAndSyncAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var credentials = WindowsCredentialVault.ReadGoogleDrive();
            if (credentials is null || string.IsNullOrWhiteSpace(_settingsStore.LoadGoogleOAuthClientId())) return;
            await SyncGoogleDriveLibraryAsync(credentials);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Google Drive startup sync failed: {exception.Message}");
            ShowDriveSyncNotification($"Google Drive sync failed: {exception.Message}", isError: true);
        }
    }

    private async Task<int> SyncGoogleDriveLibraryAsync(GoogleDriveCredentials credentials)
    {
        var clientId = _settingsStore.LoadGoogleOAuthClientId();
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Enter the Google OAuth desktop client ID in Settings.");
        _isSyncingDrive = true;
        _driveUploadPending = false;
        _driveUploadTimer.Stop();
        var client = new GoogleDriveSyncClient(clientId, _settingsStore.LoadGoogleOAuthClientSecret(), credentials);
        _driveSyncClient = client;
        ShowGoogleDriveSyncProgress();
        try
        {
            var local = _localDatabase.LoadAll();
            var remote = await client.DownloadLibraryAsync();
            var localByKey = local.ToDictionary(item => GetSyncChannelKey(item.Channel.Platform, item.Channel.ChannelId), StringComparer.OrdinalIgnoreCase);
            var merged = DriveLibraryMerger.Merge(local, remote);

            foreach (var saved in merged)
            {
                localByKey.TryGetValue(GetSyncChannelKey(saved.Channel.Platform, saved.Channel.ChannelId), out var localVersion);
                var localMedia = localVersion?.Media.ToDictionary(item => item.VodId, StringComparer.OrdinalIgnoreCase)
                                 ?? new Dictionary<string, SavedMedia>(StringComparer.OrdinalIgnoreCase);
                var channel = new ChannelEntry(saved.Channel.Platform, saved.Channel.ChannelId, saved.Channel.Name,
                    saved.Channel.Description, saved.Channel.Url)
                {
                    ProfileImageUrl = localVersion?.Channel.ProfileImageUrl,
                    ProfileImagePath = localVersion?.Channel.ProfileImagePath
                };
                var cards = saved.Media.Select(item =>
                {
                    localMedia.TryGetValue(item.VodId, out var localItem);
                    return new MediaCard(item.Title, item.Platform, item.SourceType,
                    item.SourceLabel, item.Availability, item.WatchStatus, item.SourceValue, saved.Channel.Url,
                    item.PublishedAt, localItem?.ThumbnailUrl, localItem?.ThumbnailPath, item.OnlineUrl, item.LocalFilePath,
                    item.VodId, item.LastPositionSeconds, item.Memo, item.UpdatedAt, isFavorite: item.Favorite);
                }).ToArray();
                _localDatabase.Save(channel, cards);
                foreach (var card in cards) card.Dispose();
            }

            await client.UploadLibraryAsync(merged);
            foreach (var channel in _channels) channel.Dispose();
            foreach (var card in _allCards) card.Dispose();
            _channels.Clear();
            _allCards.Clear();
            _visibleChannels.Clear();
            _visibleCards.Clear();
            _selectedChannel = null;
            LoadLocalDatabase();
            UpdateCards();
            UpdateVisibleChannels();
            ShowDriveSyncNotification("Google Drive와 동기화 완료");
            return merged.Count;
        }
        finally
        {
            StopGoogleDriveSyncAnimation();
            _isSyncingDrive = false;
        }
    }

    private static string GetSyncChannelKey(string platform, string channelId) => DriveLibraryMerger.GetChannelKey(platform, channelId);

    private async Task LoadChannelAppearanceAsync(ChannelEntry channel, string platformKey, string channelId,
        string? imageUrl, bool profileLookupSucceeded)
    {
        if (!profileLookupSucceeded)
        {
            try
            {
                var profile = await LookupChannelProfileAsync(platformKey, channelId);
                imageUrl = profile.ImageUrl;
                if (!string.IsNullOrWhiteSpace(profile.Name)) channel.DisplayName = profile.Name;
                channel.Description = profile.Description;
                channel.IsLive = profile.IsLive;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if (platformKey != "SOOP") return;
                imageUrl = BuildSoopProfileImageUrl(channelId);
            }
        }

        if (string.IsNullOrWhiteSpace(imageUrl) && platformKey == "SOOP")
            imageUrl = BuildSoopProfileImageUrl(channelId);
        channel.ProfileImageUrl = imageUrl;
        SaveChannel(channel);
        if (!string.IsNullOrWhiteSpace(imageUrl) && !await LoadChannelImageAsync(channel, imageUrl) && platformKey == "SOOP")
        {
            var fallbackUrl = BuildSoopProfileImageUrl(channelId);
            if (!string.Equals(imageUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
            {
                channel.ProfileImageUrl = fallbackUrl;
                SaveChannel(channel);
                await LoadChannelImageAsync(channel, fallbackUrl);
            }
        }
    }

    private async Task RefreshChannelMetadataAsync(ChannelEntry channel)
    {
        var platformKey = channel.Platform == "SOOP" ? "SOOP" : "CHZZK";
        try
        {
            var profile = await LookupChannelProfileAsync(platformKey, channel.ChannelId);
            if (!string.IsNullOrWhiteSpace(profile.Name)) channel.DisplayName = profile.Name;
            channel.Description = profile.Description;
            channel.IsLive = profile.IsLive;
            var imageUrl = profile.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl) && platformKey == "SOOP") imageUrl = BuildSoopProfileImageUrl(channel.ChannelId);
            channel.ProfileImageUrl = imageUrl;
            SaveChannel(channel);
            if (!string.IsNullOrWhiteSpace(imageUrl) && !await LoadChannelImageAsync(channel, imageUrl) && platformKey == "SOOP")
            {
                var fallbackUrl = BuildSoopProfileImageUrl(channel.ChannelId);
                if (!string.Equals(imageUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
                {
                    channel.ProfileImageUrl = fallbackUrl;
                    SaveChannel(channel);
                    await LoadChannelImageAsync(channel, fallbackUrl);
                }
            }
            SaveChannel(channel);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (string.IsNullOrWhiteSpace(channel.ProfileImagePath) || !File.Exists(channel.ProfileImagePath))
                await LoadChannelImageAsync(channel, channel.ProfileImageUrl);
            // Keep the last saved channel details and cached image when the service cannot be reached.
        }
    }

    private static string BuildSoopProfileImageUrl(string channelId) =>
        $"https://profile.img.sooplive.com/LOGO/{Uri.EscapeDataString(channelId[..2])}/{Uri.EscapeDataString(channelId)}/{Uri.EscapeDataString(channelId)}.jpg";

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e) => ApplyTheme(!_isDarkTheme);

    private void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        UnselectedCardBorderColor = isDark ? "#454955" : "#C3C7D0";
        _settingsStore.SaveIsDarkTheme(isDark);
        if (Application.Current is { } app)
            app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        SetBrushColor("AppBackgroundBrush", isDark ? "#12141B" : "#F5F6FA");
        SetBrushColor("SidebarBackgroundBrush", isDark ? "#0D0F15" : "#171923");
        SetBrushColor("MainSurfaceBrush", isDark ? "#1B1E27" : "#FFFFFF");
        SetBrushColor("InsetSurfaceBrush", isDark ? "#22252F" : "#F7F7FA");
        SetBrushColor("CardSurfaceBrush", isDark ? "#242731" : "#FFFFFF");
        SetBrushColor("MainBorderBrush", isDark ? "#353946" : "#E8E9EF");
        SetBrushColor("SoftBorderBrush", isDark ? "#3A3D49" : "#E5E6ED");
        SetBrushColor("PrimaryTextBrush", isDark ? "#F1F2F7" : "#202230");
        SetBrushColor("SecondaryTextBrush", isDark ? "#A8ABBA" : "#777B8B");
        SetBrushColor("CardTitleBrush", isDark ? "#F1F2F7" : "#292B3A");
        SetBrushColor("LicenseSurfaceBrush", isDark ? "#2C2840" : "#F4F1FF");
        SetBrushColor("LicenseBorderBrush", isDark ? "#494263" : "#DDD6FF");
        SetBrushColor("LicenseTitleBrush", isDark ? "#C8BFFF" : "#4934A6");
        SetBrushColor("LicenseBodyBrush", isDark ? "#D1CEDD" : "#55596A");
        SetBrushColor("ThemeButtonBackgroundBrush", isDark ? "#292C36" : "#FFFFFF");
        SetBrushColor("ThemeButtonForegroundBrush", isDark ? "#F3F4F8" : "#252836");
        foreach (var card in _allCards) card.RefreshTheme();
        foreach (var channel in _channels) channel.RefreshTheme();

        ThemeToggleButton.Content = isDark ? "☾" : "☀";
        ToolTip.SetTip(ThemeToggleButton, isDark ? "라이트 모드로 전환" : "다크 모드로 전환");
    }

    private void SetBrushColor(string key, string color)
    {
        if (Resources.TryGetResource(key, null, out var resource) && resource is SolidColorBrush brush)
            brush.Color = Color.Parse(color);
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;

        if (section == "all")
        {
            UpdateNavigationSelection(section);
            _isAllLibraryView = true;
            ClearChannelSelection();
            _cardDisplayLimit = _settingsStore.LoadCardBatchSize();
            foreach (var channel in _channels) channel.IsSelected = false;
            PlatformTabs.SelectedIndex = -1;
            PlatformTabs.IsVisible = true;
            LibraryPanel.IsVisible = true;
            LicensePanel.IsVisible = false;
            SearchFiltersPanel.IsVisible = true;
            PageTitle.Text = "전체 보관함";
            PageDescription.Text = "치지직과 SOOP에 등록한 영상을 모아봅니다.";
            ListTitle.Text = "다시보기 리스트";
            UpdateCards();
            RefreshUnknownVisibleCardDurations();
            return;
        }

        if (section == "licenses")
        {
            UpdateNavigationSelection(section);
            _isAllLibraryView = false;
            PlatformTabs.SelectedIndex = -1;
            PlatformTabs.IsVisible = false;
            LibraryPanel.IsVisible = false;
            LicensePanel.IsVisible = true;
            SearchFiltersPanel.IsVisible = false;
            PageTitle.Text = "라이선스 및 오픈소스 고지";
            PageDescription.Text = "VODTracker의 MIT 라이선스와 사용한 프로젝트 및 패키지의 라이선스를 표시합니다.";
            ListTitle.Text = "라이선스 및 코드 참조";
            return;
        }

        UpdateNavigationSelection(section);
        _isAllLibraryView = false;
        ClearChannelSelection();
        PlatformTabs.SelectedIndex = section == "soop" ? 1 : 0;
        UpdateCards();
        RefreshUnknownVisibleCardDurations();
    }

    private void UpdateNavigationSelection(string section)
    {
        AllLibraryButton.Classes.Set("selected", section == "all");
        ChzzkNavigationButton.Classes.Set("selected", section == "chzzk");
        SoopNavigationButton.Classes.Set("selected", section == "soop");
        LicensesNavigationButton.Classes.Set("selected", section == "licenses");
    }

    private void PlatformTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageTitle is null) return;
        if (_isAllLibraryView)
        {
            UpdateCards();
            RefreshUnknownVisibleCardDurations();
            return;
        }
        if (PlatformTabs.SelectedItem is not TabItem tab) return;

        var section = tab.Tag?.ToString();
        if (section is "chzzk" or "soop") UpdateNavigationSelection(section);
        ClearChannelSelection();
        if (PlatformTabs is not null) PlatformTabs.IsVisible = true;
        if (LibraryPanel is not null) LibraryPanel.IsVisible = true;
        if (LicensePanel is not null) LicensePanel.IsVisible = false;
        if (SearchFiltersPanel is not null) SearchFiltersPanel.IsVisible = true;

        (PageTitle.Text, PageDescription.Text, ListTitle.Text) = section switch
        {
            "chzzk" => ("치지직", "치지직 채널과 영상을 관리하세요.", "다시보기 리스트"),
            "soop" => ("SOOP", "SOOP 채널과 영상을 관리하세요.", "다시보기 리스트"),
            _ => ("전체 보관함", "치지직과 SOOP에 등록한 영상을 모아봅니다.", "다시보기 리스트")
        };
        UpdateCards();
        RefreshUnknownVisibleCardDurations();
    }
    private void AddMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;

        var menu = new MenuFlyout();
        if (GetSelectedPlatform() is { } platform)
        {
            foreach (var item in CreateMediaMenuItems(platform)) menu.Items.Add(item);
        }
        else
        {
            var chzzk = new MenuItem { Header = "치지직" };
            var soop = new MenuItem { Header = "SOOP" };
            foreach (var item in CreateMediaMenuItems("치지직")) chzzk.Items.Add(item);
            foreach (var item in CreateMediaMenuItems("SOOP")) soop.Items.Add(item);
            menu.Items.Add(chzzk);
            menu.Items.Add(soop);
        }

        menu.ShowAt(control);
    }

    private MenuItem[] CreateMediaMenuItems(string platform)
    {
        var addLink = new MenuItem { Header = "링크로 추가" };
        var addFile = new MenuItem { Header = "PC 파일로 추가" };
        addLink.Click += async (_, _) => await AddLinkAsync(platform);
        addFile.Click += async (_, _) => await AddLocalFileAsync(platform);
        return [addLink, addFile];
    }

    private async Task AddLinkAsync(string platform)
    {
        var titleInput = new TextBox { PlaceholderText = "영상 제목", MinHeight = 38 };
        var memoInput = new TextBox { PlaceholderText = "메모", Height = 90, MinHeight = 90, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top };
        ScrollViewer.SetVerticalScrollBarVisibility(memoInput, ScrollBarVisibility.Visible);
        var urlInput = new TextBox { PlaceholderText = "https://...", MinHeight = 38 };
        var validation = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cancelButton = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var dialog = new Window
        {
            Title = $"{platform} 링크 추가",
            Width = 460,
            Height = 390,
            MinWidth = 460,
            MinHeight = 390,
            MaxWidth = 460,
            MaxHeight = 390,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"{platform} 영상 링크", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = "HTTP 또는 HTTPS 주소를 입력하세요.", Foreground = Avalonia.Media.Brushes.Gray, FontSize = 12 },
                    titleInput,
                    urlInput,
                    validation,
                    memoInput,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            cancelButton,
                            new Button { Content = "추가", MinWidth = 80, IsDefault = true, Tag = "save" }
                        }
                    }
                }
            }
        };

        var actionRow = (StackPanel)((StackPanel)dialog.Content!).Children[^1]!;
        var saveButton = (Button)actionRow.Children[1]!;
        cancelButton.Click += (_, _) => dialog.Close();
        saveButton.Click += (_, _) =>
        {
            var title = titleInput.Text?.Trim();
            var address = urlInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                validation.Text = "제목을 입력해 주세요.";
                return;
            }
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                validation.Text = "올바른 HTTP 또는 HTTPS URL을 입력해 주세요.";
                return;
            }

            _allCards.Add(new MediaCard(title, platform, "링크", uri.Host, "링크 사용 가능", "안 봄", uri.AbsoluteUri, memo: memoInput.Text));
            UpdateCards();
            dialog.Close();
        };

        await dialog.ShowDialog(this);
    }

    private async Task AddLocalFileAsync(string platform)
    {
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{platform}에 등록할 영상 파일 선택",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("영상 파일")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (selected.Count == 0) return;
        var file = selected[0];
        var filePath = file.Path.LocalPath;
        _allCards.Add(new MediaCard(Path.GetFileNameWithoutExtension(file.Name), platform, "PC 파일",
            file.Name, "파일 확인됨", "안 봄", filePath));
        _ = DetectLocalDurationAsync(_allCards[^1]);
        UpdateCards();
    }

    private void ChannelCard_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ChannelEntry channel } ||
            !e.GetCurrentPoint(ChannelPanel).Properties.IsLeftButtonPressed) return;
        _isChannelPointerTracking = true;
        _isChannelLassoSelecting = false;
        _channelPressedTarget = channel;
        _channelPressModifiers = e.KeyModifiers;
        _channelDragSelectionBase.Clear();
        _channelDragSelectionBase.UnionWith(_selectedChannelUrls);
        _channelLassoStart = e.GetPosition(ChannelPanel);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private async void ChannelName_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ChannelEntry channel } ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            !Uri.TryCreate(channel.ChannelUrl, UriKind.Absolute, out var uri)) return;
        e.Handled = true;
        await Launcher.LaunchUriAsync(uri);
    }

    private async void ChannelAvatar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Image { DataContext: ChannelEntry channel } ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        var liveUrl = channel.Platform == "SOOP"
            ? $"https://play.sooplive.com/{Uri.EscapeDataString(channel.ChannelId)}"
            : $"https://chzzk.naver.com/live/{Uri.EscapeDataString(channel.ChannelId)}";
        await Launcher.LaunchUriAsync(new Uri(liveUrl));
    }

    private void ApplyChannelClickSelection(ChannelEntry channel, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var anchorIndex = _visibleChannels.IndexOf(_channelSelectionAnchor ?? channel);
            var targetIndex = _visibleChannels.IndexOf(channel);
            if (anchorIndex < 0) anchorIndex = targetIndex;
            if (!modifiers.HasFlag(KeyModifiers.Control)) _selectedChannelUrls.Clear();
            for (var i = Math.Min(anchorIndex, targetIndex); i <= Math.Max(anchorIndex, targetIndex); i++)
                _selectedChannelUrls.Add(_visibleChannels[i].ChannelUrl);
        }
        else if (modifiers.HasFlag(KeyModifiers.Control))
        {
            if (!_selectedChannelUrls.Add(channel.ChannelUrl)) _selectedChannelUrls.Remove(channel.ChannelUrl);
            _channelSelectionAnchor = channel;
        }
        else
        {
            _selectedChannelUrls.Clear();
            _selectedChannelUrls.Add(channel.ChannelUrl);
            _channelSelectionAnchor = channel;
        }

        _isShowAllChannels = false;
        foreach (var item in _channels) item.IsSelected = _selectedChannelUrls.Contains(item.ChannelUrl);
        _selectedChannel = _channels.FirstOrDefault(item => _selectedChannelUrls.Contains(item.ChannelUrl));
        ShowAllChannelsCard.Classes.Set("selected", false);
    }

    private void ShowAllChannels_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isShowAllChannels = true;
        _selectedChannelUrls.Clear();
        _selectedChannel = null;
        foreach (var channel in _channels) channel.IsSelected = false;
        ShowAllChannelsCard.Classes.Set("selected", true);
        _cardDisplayLimit = _settingsStore.LoadCardBatchSize();
        UpdateCards();
        e.Handled = true;
    }

    private void ChannelPanel_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ChannelPanel).Properties.IsLeftButtonPressed ||
            IsInsideChannelCard(e.Source) || IsInsideButton(e.Source) || IsInsideScrollBar(e.Source)) return;

        _isChannelPointerTracking = true;
        _isChannelLassoSelecting = false;
        _channelPressedTarget = null;
        _channelPressModifiers = e.KeyModifiers;
        _channelDragSelectionBase.Clear();
        _channelLassoStart = e.GetPosition(ChannelPanel);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private async void DeleteSelectedChannels_Click(object? sender, RoutedEventArgs e)
    {
        var selectedChannels = _channels.Where(channel => _selectedChannelUrls.Contains(channel.ChannelUrl)).ToArray();
        if (_isShowAllChannels || selectedChannels.Length == 0) return;

        var confirm = new Window
        {
            Title = "채널 삭제",
            Width = 400,
            Height = 200,
            MinWidth = 400,
            MinHeight = 200,
            MaxWidth = 400,
            MaxHeight = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancelButton = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var deleteButton = new Button { Content = "삭제", MinWidth = 82, Classes = { "primary" }, IsDefault = true };
        cancelButton.Click += (_, _) => confirm.Close(false);
        deleteButton.Click += (_, _) => confirm.Close(true);
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = $"선택한 채널 {selectedChannels.Length}개와 해당 채널의 영상 및 저장 파일을 삭제할까요?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, deleteButton }
                }
            }
        };
        if (await confirm.ShowDialog<bool?>(this) != true) return;

        var failures = new List<string>();
        foreach (var channel in selectedChannels)
        {
            try
            {
                _vodRepository.Delete(channel);
                _lastSavedDriveSnapshots.Remove(GetSyncChannelKey(channel.Platform, channel.ChannelId));
            }
            catch (Exception exception)
            {
                failures.Add($"{channel.DisplayName}: {exception.Message}");
                continue;
            }

            foreach (var card in _allCards.Where(card => card.ChannelUrl == channel.ChannelUrl).ToArray())
            {
                _allCards.Remove(card);
                card.Dispose();
            }
            _channels.Remove(channel);
            _soopNextPageByChannel.Remove(channel.ChannelUrl);
            _soopHasMoreByChannel.Remove(channel.ChannelUrl);
            channel.Dispose();
        }

        if (_driveSyncClient is not null)
        {
            _driveUploadPending = true;
            ShowGoogleDriveSyncProgress();
            _driveUploadTimer.Stop();
            _driveUploadTimer.Start();
        }

        ClearChannelSelection();
        UpdateCards();
        if (failures.Count > 0)
            await ShowTransferMessageAsync("채널 삭제 일부 실패", string.Join(Environment.NewLine, failures));
    }

    private static bool IsInsideChannelCard(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Border { DataContext: ChannelEntry }) return true;
        return false;
    }

    private void ClearChannelSelection()
    {
        _selectedChannel = null;
        _isShowAllChannels = false;
        _selectedChannelUrls.Clear();
        foreach (var channel in _channels) channel.IsSelected = false;
        ShowAllChannelsCard?.Classes.Set("selected", false);
        _cardDisplayLimit = _settingsStore.LoadCardBatchSize();
    }

    private MediaCard? _selectionAnchor;
    private bool _isLassoSelecting;
    private Point _lassoStart;

    private void MediaCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: MediaCard selectedCard }) return;
        // Let interactive controls inside the card receive their normal click events.
        // Marking the pointer press handled here prevents Button.Click from firing.
        if (IsInsideButton(e.Source) || IsInsideInput(e.Source)) return;

        var modifiers = e.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var start = _visibleCards.IndexOf(_selectionAnchor ?? selectedCard);
            var end = _visibleCards.IndexOf(selectedCard);
            if (start < 0) start = end;
            if (!modifiers.HasFlag(KeyModifiers.Control))
                foreach (var card in _allCards) card.IsSelected = false;
            for (var index = Math.Min(start, end); index <= Math.Max(start, end); index++)
                _visibleCards[index].IsSelected = true;
        }
        else if (modifiers.HasFlag(KeyModifiers.Control))
        {
            selectedCard.IsSelected = !selectedCard.IsSelected;
            _selectionAnchor = selectedCard;
        }
        else
        {
            foreach (var card in _allCards) card.IsSelected = ReferenceEquals(card, selectedCard);
            _selectionAnchor = selectedCard;
        }

        CardsArea.Focus();
        UpdateSelectionButtons();
        e.Handled = true;
    }

    private void CardsArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CardsArea).Properties.IsLeftButtonPressed ||
            e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
            IsInsideScrollBar(e.Source) || IsInsideButton(e.Source) || IsInsideInput(e.Source) || IsInsidePlaybackProgress(e.Source) ||
            e.Source is TextBlock { Cursor: not null }) return;

        var pressedCard = (e.Source as Control)?.DataContext as MediaCard;
        if (pressedCard is null)
            ClearCardSelection();
        CardsArea.Focus();
        _dragSelectionBase.Clear();
        foreach (var card in _allCards.Where(card => card.IsSelected && _visibleCards.Contains(card)))
            _dragSelectionBase.Add(card);
        _lassoStart = e.GetPosition(CardsArea);
        _isLassoSelecting = true;
        UpdateLasso(e.GetPosition(CardsArea));
        e.Pointer.Capture(this);
    }

    private static bool IsInsideScrollBar(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is ScrollBar) return true;
        return false;
    }

    private static bool IsInsideButton(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Button) return true;
        return false;
    }

    private static bool IsInsideInput(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox or Slider) return true;
        return false;
    }

    private static bool IsInsidePlaybackProgress(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control control && control.Classes.Contains("playback-progress")) return true;
        return false;
    }

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isLassoSelecting || _isChannelPointerTracking || CardsArea.IsPointerOver) return;
        if (DeleteSelectedButton.IsPointerOver || EditSelectedButton.IsPointerOver) return;
        ClearCardSelection();
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isChannelPointerTracking && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var current = e.GetPosition(ChannelPanel);
            if (!_isChannelLassoSelecting && Math.Abs(current.X - _channelLassoStart.X) + Math.Abs(current.Y - _channelLassoStart.Y) > 5)
                _isChannelLassoSelecting = true;
            if (_isChannelLassoSelecting) UpdateChannelLasso(current);
            return;
        }
        if (_isLassoSelecting && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            UpdateLasso(e.GetPosition(CardsArea));
    }

    private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isChannelPointerTracking)
        {
            var current = e.GetPosition(ChannelPanel);
            var wasLasso = _isChannelLassoSelecting;
            var pressedChannel = _channelPressedTarget;
            if (wasLasso) UpdateChannelLasso(current);
            else if (pressedChannel is { } channel)
            {
                ApplyChannelClickSelection(channel, _channelPressModifiers);
            }
            else
            {
                ClearChannelSelection();
            }
            _selectedChannel = _channels.FirstOrDefault(channel => _selectedChannelUrls.Contains(channel.ChannelUrl));
            _isChannelPointerTracking = false;
            _isChannelLassoSelecting = false;
            _channelPressedTarget = null;
            e.Pointer.Capture(null);
            ChannelSelectionRectangle.IsVisible = false;
            _cardDisplayLimit = _settingsStore.LoadCardBatchSize();
            ShowAllChannelsCard.Classes.Set("selected", false);
            UpdateCards();
            if (!wasLasso && pressedChannel is not null && _selectedChannelUrls.Contains(pressedChannel.ChannelUrl))
                _ = RefreshChannelDurationsAsync(pressedChannel);
            return;
        }
        if (!_isLassoSelecting) return;
        UpdateLasso(e.GetPosition(CardsArea));
        _isLassoSelecting = false;
        SelectionRectangle.IsVisible = false;
        e.Pointer.Capture(null);
        UpdateSelectionButtons();
    }

    private void UpdateChannelLasso(Point current)
    {
        var left = Math.Min(_channelLassoStart.X, current.X);
        var top = Math.Min(_channelLassoStart.Y, current.Y);
        var rect = new Rect(left, top, Math.Abs(current.X - _channelLassoStart.X), Math.Abs(current.Y - _channelLassoStart.Y));
        Canvas.SetLeft(ChannelSelectionRectangle, left);
        Canvas.SetTop(ChannelSelectionRectangle, top);
        ChannelSelectionRectangle.Width = rect.Width;
        ChannelSelectionRectangle.Height = rect.Height;
        ChannelSelectionRectangle.IsVisible = rect.Width > 2 || rect.Height > 2;
        var selection = new HashSet<string>(_channelDragSelectionBase, StringComparer.OrdinalIgnoreCase);
        foreach (var border in _channelCardVisuals)
        {
            if (border.DataContext is not ChannelEntry channel) continue;
            var position = border.TranslatePoint(new Point(0, 0), ChannelPanel);
            if (position is not { } topLeft) continue;
            var bounds = new Rect(topLeft, border.Bounds.Size);
            if (bounds.Right >= rect.Left && bounds.Left <= rect.Right && bounds.Bottom >= rect.Top && bounds.Top <= rect.Bottom)
                selection.Add(channel.ChannelUrl);
        }
        _selectedChannelUrls.Clear();
        _selectedChannelUrls.UnionWith(selection);
        _isShowAllChannels = false;
        foreach (var channel in _channels) channel.IsSelected = _selectedChannelUrls.Contains(channel.ChannelUrl);
        ShowAllChannelsCard.Classes.Set("selected", false);
    }

    private void ChannelCard_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border && !_channelCardVisuals.Contains(border)) _channelCardVisuals.Add(border);
    }

    private void ChannelCard_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border) _channelCardVisuals.Remove(border);
    }

    private void UpdateLasso(Point current)
    {
        var left = Math.Min(_lassoStart.X, current.X);
        var top = Math.Min(_lassoStart.Y, current.Y);
        var width = Math.Abs(current.X - _lassoStart.X);
        var height = Math.Abs(current.Y - _lassoStart.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
        SelectionRectangle.IsVisible = width > 2 || height > 2;

        var selection = new HashSet<MediaCard>(_dragSelectionBase);
        var dragRect = new Rect(left, top, width, height);
        foreach (var border in _cardVisuals)
        {
            if (border.Tag?.ToString() != (_isListView ? "list" : "grid") ||
                border.DataContext is not MediaCard card || !_visibleCards.Contains(card)) continue;
            var position = border.TranslatePoint(new Point(0, 0), CardsArea);
            if (position is not { } topLeft) continue;
            var bounds = new Rect(topLeft, border.Bounds.Size);
            if (bounds.Right >= dragRect.Left && bounds.Left <= dragRect.Right &&
                bounds.Bottom >= dragRect.Top && bounds.Top <= dragRect.Bottom)
                selection.Add(card);
        }

        foreach (var card in _visibleCards)
            card.IsSelected = selection.Contains(card);
        UpdateSelectionButtons();
    }

    private void MediaCard_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border && !_cardVisuals.Contains(border))
            _cardVisuals.Add(border);
        Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
    }

    private void MediaCard_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Border border)
            _cardVisuals.Remove(border);
    }

    private void CardsArea_KeyDown(object? sender, KeyEventArgs e)
    {
        var scrollViewer = _isListView ? CardsListScroller : CardsScroller;
        if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            foreach (var card in _visibleCards) card.IsSelected = true;
            _selectionAnchor = _visibleCards.FirstOrDefault();
            UpdateSelectionButtons();
            e.Handled = true;
            return;
        }

        var offset = scrollViewer.Offset;
        switch (e.Key)
        {
            case Key.Home:
                scrollViewer.Offset = new Vector(offset.X, 0);
                break;
            case Key.End:
                scrollViewer.Offset = new Vector(offset.X, scrollViewer.Extent.Height);
                break;
            case Key.PageUp:
                scrollViewer.Offset = new Vector(offset.X, Math.Max(0, offset.Y - scrollViewer.Viewport.Height));
                break;
            case Key.PageDown:
                scrollViewer.Offset = new Vector(offset.X, Math.Min(scrollViewer.Extent.Height, offset.Y + scrollViewer.Viewport.Height));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ClearCardSelection()
    {
        foreach (var card in _allCards) card.IsSelected = false;
        _selectionAnchor = null;
        UpdateSelectionButtons();
    }

    private void UpdateSelectionButtons()
    {
        if (DeleteSelectedButton is null || EditSelectedButton is null) return;
        var selectedCards = _allCards.Where(card => card.IsSelected && _visibleCards.Contains(card)).ToArray();
        var selectedCount = selectedCards.Length;
        DeleteSelectedButton.IsVisible = selectedCount > 0;
        EditSelectedButton.IsVisible = selectedCount > 0;
        EditSelectedButton.IsEnabled = selectedCount == 1;

        var platform = selectedCards.FirstOrDefault()?.PlatformLabel ?? _selectedChannel?.Platform;
        var accent = GetPlatformAccentBrush(platform);
        DeleteSelectedButton.Background = accent;
        DeleteSelectedButton.Foreground = Brushes.White;
        EditSelectedButton.Background = selectedCount > 1
            ? new SolidColorBrush(Color.Parse("#A0A3AF"))
            : accent;
        EditSelectedButton.Foreground = Brushes.White;
    }

    private static IBrush GetPlatformAccentBrush(string? platform) =>
        new SolidColorBrush(Color.Parse(platform == "SOOP" ? "#398FE0" : "#35B879"));

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = _allCards.Where(card => card.IsSelected && _visibleCards.Contains(card)).ToArray();
        if (selected.Length == 0) return;

        var confirm = new Window
        {
            Title = "영상 삭제",
            Width = 360,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "취소", MinWidth = 78, IsCancel = true };
        var delete = new Button { Content = "삭제", MinWidth = 78, Classes = { "primary" }, IsDefault = true };
        cancel.Click += (_, _) => confirm.Close(false);
        delete.Click += (_, _) => confirm.Close(true);
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = $"선택한 영상 {selected.Length}개를 삭제할까요?", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, delete }
                }
            }
        };
        if (await confirm.ShowDialog<bool?>(this) != true) return;

        var affectedChannels = selected.Select(card => card.ChannelUrl)
            .Where(url => url is not null)
            .Distinct()
            .Select(url => _channels.FirstOrDefault(channel => channel.ChannelUrl == url))
            .Where(channel => channel is not null)
            .ToArray();
        foreach (var card in selected)
        {
            _allCards.Remove(card);
            card.Dispose();
        }
        ClearCardSelection();
        foreach (var channel in affectedChannels)
            SaveChannel(channel!);
        UpdateCards();
    }

    private (Grid DateRow, Popup CalendarPopup) CreateDatePicker(TextBox dateInput)
    {
        var popupSurface = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#252832" : "#FFFFFF"));
        var popupBorder = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#454955" : "#D9DBE3"));
        var calendarForeground = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#F1F2F7" : "#202230"));
        var selectedDate = DateTime.TryParseExact(dateInput.Text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : DateTime.Today;
        var calendar = new Calendar { SelectedDate = selectedDate, DisplayDate = selectedDate, Background = popupSurface, Foreground = calendarForeground };
        var calendarButton = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE787", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 20,
                Foreground = new SolidColorBrush(Color.Parse("#7C5CFC")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            },
            Width = 38, Height = 38, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(calendarButton, "날짜 선택");
        var todayButton = new Button
        {
            Content = "오늘", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 64, Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#39324F" : "#F0EDFF")),
            BorderBrush = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#5A4F7A" : "#D8D0FA")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Foreground = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#D2C8FF" : "#654CD3"))
        };
        var calendarPopup = new Popup
        {
            PlacementTarget = calendarButton,
            Placement = PlacementMode.Bottom,
            Child = new Border
            {
                Background = popupSurface, BorderBrush = popupBorder, BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = new StackPanel { Spacing = 6, Children = { calendar, todayButton } }
            }
        };
        void SetDate(DateTime date)
        {
            calendar.DisplayDate = date;
            calendar.SelectedDate = date;
            dateInput.Text = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            calendarPopup.IsOpen = false;
        }
        todayButton.Click += (_, _) => SetDate(DateTime.Today);
        calendarButton.Click += (_, _) =>
        {
            if (DateTime.TryParseExact(dateInput.Text?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var currentDate))
            {
                calendar.DisplayDate = currentDate;
                calendar.SelectedDate = currentDate;
            }
            calendarPopup.IsOpen = !calendarPopup.IsOpen;
        };
        calendar.PropertyChanged += (_, args) =>
        {
            if (args.Property == Calendar.SelectedDateProperty && calendar.SelectedDate is { } date)
                SetDate(date);
        };
        var dateRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,38"), ColumnSpacing = 8 };
        dateRow.Children.Add(dateInput);
        dateRow.Children.Add(calendarButton);
        Grid.SetColumn(calendarButton, 1);
        return (dateRow, calendarPopup);
    }

    private async void EditSelected_Click(object? sender, RoutedEventArgs e)
    {
        var card = _allCards.SingleOrDefault(item => item.IsSelected && _visibleCards.Contains(item));
        if (card is null) return;

        var titleInput = new TextBox { Text = card.Title, PlaceholderText = "영상 제목", MinHeight = 38 };
        var memoInput = new TextBox { Text = card.Memo, PlaceholderText = "메모", Height = 90, MinHeight = 90, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top };
        ScrollViewer.SetVerticalScrollBarVisibility(memoInput, ScrollBarVisibility.Visible);
        var urlInput = new TextBox { Text = card.OnlineUrl ?? "", PlaceholderText = "온라인 영상 URL (선택)", MinHeight = 38 };
        var filePathInput = new TextBox { Text = card.LocalFilePath ?? "", PlaceholderText = "파일 링크/경로 (선택)", MinHeight = 38 };
        var dateInput = new TextBox
        {
            Text = card.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            PlaceholderText = "방송 날짜 (선택, YYYY-MM-DD)",
            MinHeight = 38
        };
        var (dateRow, calendarPopup) = CreateDatePicker(dateInput);
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "영상 수정",
            Width = 500,
            Height = 540,
            MinWidth = 500,
            MinHeight = 540,
            MaxWidth = 500,
            MaxHeight = 540,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var chooseFile = new Button { Content = "찾기", MinWidth = 66 };
        chooseFile.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "영상 파일 선택",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("영상 파일") { Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.m4v"] }, FilePickerFileTypes.All]
            });
            if (files.Count == 0) return;
            filePathInput.Text = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(titleInput.Text)) titleInput.Text = Path.GetFileNameWithoutExtension(files[0].Name);
        };
        var cancel = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var save = new Button { Content = "저장", MinWidth = 82, Classes = { "primary" }, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            var title = titleInput.Text?.Trim();
            var address = urlInput.Text?.Trim();
            var filePath = filePathInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(title)) { error.Text = "영상 제목을 입력해 주세요."; return; }
            if (!string.IsNullOrWhiteSpace(address) && !IsHttpUrl(address))
            { error.Text = "URL은 HTTP 또는 HTTPS 주소로 입력해 주세요."; return; }
            if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(filePath))
            { error.Text = "영상 URL 또는 파일 경로를 하나 이상 입력해 주세요."; return; }

            DateTimeOffset? publishedAt = null;
            if (!string.IsNullOrWhiteSpace(dateInput.Text))
            {
                if (!DateTime.TryParseExact(dateInput.Text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsedDate))
                { error.Text = "날짜를 YYYY-MM-DD 형식으로 입력해 주세요."; return; }
                publishedAt = new DateTimeOffset(parsedDate, TimeZoneInfo.Local.GetUtcOffset(parsedDate));
            }

            card.UpdateDetails(title, publishedAt, address, filePath);
            card.SetMemo(memoInput.Text ?? "");
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(item => item.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
            UpdateCards();
            Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
            dialog.Close();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "영상 정보 수정", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                titleInput,
                urlInput,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children = { filePathInput, chooseFile }
                },
                dateRow,
                memoInput,
                calendarPopup,
                error,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save }
                }
            }
        };
        Grid.SetColumn(chooseFile, 1);
        await dialog.ShowDialog(this);
    }

    private void GridView_Click(object? sender, RoutedEventArgs e)
    {
        _isListView = false;
        UpdateViewButtonStates();
        UpdateCards();
    }

    private void ListView_Click(object? sender, RoutedEventArgs e)
    {
        _isListView = true;
        UpdateViewButtonStates();
        UpdateCards();
    }

    private void ThumbnailView_Click(object? sender, RoutedEventArgs e)
    {
        _showThumbnails = !_showThumbnails;
        foreach (var card in _allCards)
        {
            card.ShowThumbnails = _showThumbnails;
            if (!_showThumbnails)
            {
                card.CancelThumbnailRequest();
                continue;
            }
            if (!card.HasThumbnail && IsHttpUrl(card.OnlineUrl)) _ = RefreshCardThumbnailAsync(card);
            else if (!card.HasThumbnail && !string.IsNullOrWhiteSpace(card.ThumbnailUrl)) _ = LoadThumbnailAsync(card, card.ThumbnailUrl);
        }
        UpdateViewButtonStates();
    }

    private void UpdateViewButtonStates()
    {
        ThumbnailViewButton.Classes.Set("selected", _showThumbnails);
        GridViewButton.Classes.Set("selected", !_isListView);
        ListViewButton.Classes.Set("selected", _isListView);
        ToolTip.SetTip(ThumbnailViewButton, _showThumbnails ? "썸네일 숨기기" : "썸네일 표시");
        ToolTip.SetTip(GridViewButton, _isListView ? "그리드 보기" : "현재 그리드 보기");
        ToolTip.SetTip(ListViewButton, _isListView ? "현재 리스트 보기" : "리스트 보기");
    }

    private void FavoriteFilter_Click(object? sender, RoutedEventArgs e)
    {
        _showFavoritesOnly = !_showFavoritesOnly;
        FavoriteFilterButton.Content = _showFavoritesOnly ? "★" : "☆";
        FavoriteFilterButton.Foreground = new SolidColorBrush(Color.Parse(_showFavoritesOnly ? "#F4C542" : "#9699A6"));
        UpdateCards();
    }

    private void FavoriteCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCard card }) return;
        card.ToggleFavorite();
        if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
            SaveChannel(channel);
        UpdateCards();
    }

    private void WatchStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaCard card })
        {
            card.CycleWatchStatus();
            if (card.IsLastPositionVisible && !card.HasKnownDuration)
                _ = RefreshDurationForWatchingCardAsync(card);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
            UpdateCards();
        }
    }

    private async void Memo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCard card }) return;
        var editor = new TextBox
        {
            Text = card.Memo,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 130,
            MinHeight = 130,
            MaxLength = 10000,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Visible);
        var save = new Button { Content = "저장", Classes = { "primary" }, MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80 };
        var dialog = new Window
        {
            Title = "메모",
            Width = 420,
            Height = 280,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = card.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    editor,
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
                }
            }
        };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (await dialog.ShowDialog<bool>(this))
        {
            card.SetMemo(editor.Text ?? "");
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        }
    }

    private void LastPositionInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox input || input.DataContext is not MediaCard card) return;
        if (card.IsPositionSliderInteracting) return;
        var text = input.Text ?? "";
        var digits = new string(text.Where(char.IsAsciiDigit).Take(2).ToArray());
        var sanitized = text.Contains('-') || (text.Length > 0 && digits.Length == 0) ? "00" : digits;
        if (!string.Equals(text, sanitized, StringComparison.Ordinal))
        {
            var caretIndex = input.CaretIndex;
            input.Text = sanitized;
            input.CaretIndex = Math.Min(caretIndex, sanitized.Length);
            return;
        }
        if (input.Tag is string part) card.SetLastPositionPart(part, sanitized);
    }

    private void LastPositionInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox input || input.DataContext is not MediaCard card) return;
        if (input.Text is { Length: 1 } digit && char.IsAsciiDigit(digit[0]))
            input.Text = $"0{digit}";
        Dispatcher.UIThread.Post(() =>
        {
            var focused = TopLevel.GetTopLevel(input)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox { DataContext: MediaCard focusedCard, Tag: string tag } &&
                ReferenceEquals(focusedCard, card) && tag is "hours" or "minutes" or "seconds") return;
            if (!card.CommitPositionInput()) return;
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        });
    }

    private void PlaybackPositionSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MediaCard card } control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            card.BeginPositionSliderInteraction();
            card.SetPlaybackProgressDragging(true);
            control.Focus();
            e.Pointer.Capture(control);
            UpdatePlaybackPositionFromPointer(control, card, e.GetPosition(control).X);
            e.Handled = true;
        }
    }

    private void PlaybackPositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control { DataContext: MediaCard card }) return;
        e.Pointer.Capture(null);
        card.SetPlaybackProgressDragging(false);
        if (card.EndPositionSliderInteraction() && card.ChannelUrl is not null &&
            _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
            SaveChannel(channel);
        e.Handled = true;
    }

    private void PlaybackPositionSlider_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { DataContext: MediaCard card } control || !card.IsPositionSliderInteracting) return;
        UpdatePlaybackPositionFromPointer(control, card, e.GetPosition(control).X);
        e.Handled = true;
    }

    private static void UpdatePlaybackPositionFromPointer(Control control, MediaCard card, double pointerX)
    {
        if (control.Bounds.Width <= 0 || card.DurationSeconds is not > 0) return;
        var progress = Math.Clamp(pointerX / control.Bounds.Width, 0, 1);
        card.PlaybackPositionSliderValue = progress * card.DurationSeconds.Value;
    }

    private void PlaybackPositionSlider_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Control { DataContext: MediaCard card } && !card.IsPositionSliderInteracting && IsSliderAdjustmentKey(e.Key))
        {
            card.BeginPositionSliderInteraction();
            card.SetPlaybackProgressDragging(true);
        }
    }

    private void PlaybackPositionSlider_KeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: MediaCard card } || !IsSliderAdjustmentKey(e.Key)) return;
        card.SetPlaybackProgressDragging(false);
        if (card.EndPositionSliderInteraction() && card.ChannelUrl is not null &&
            _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
            SaveChannel(channel);
    }

    private void PlaybackProgress_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: MediaCard card }) card.SetPlaybackProgressHovered(true);
    }

    private void PlaybackProgress_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: MediaCard card }) card.SetPlaybackProgressPointerOver(false);
    }

    private static bool IsSliderAdjustmentKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void LastPositionInput_TextInput(object? sender, TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && e.Text.Any(character => !char.IsAsciiDigit(character))) e.Handled = true;
    }

    private void LastPositionInput_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox input) input.SelectAll();
    }

    private async void MediaSource_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: MediaCard card }) return;
        if (e.Source is Button || !card.HasOnlineUrl) return;
        e.Handled = true;
        foreach (var item in _allCards) item.IsSelected = ReferenceEquals(item, card);
        if (IsHttpUrl(card.OnlineUrl) && Uri.TryCreate(card.OnlineUrl, UriKind.Absolute, out var uri))
        {
            var targetUri = card.WatchStatusLabel == "시청중"
                ? AddVodStartPosition(uri, card.PlatformLabel, card.LastPositionSeconds)
                : uri;
            await Launcher.LaunchUriAsync(targetUri);
            return;
        }

    }

    private void MediaFile_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: MediaCard card }) return;
        if (e.Source is Button || !card.HasLocalFilePath) return;
        e.Handled = true;
        OpenLocalFile(card);
    }

    private async void AddCardLocalFile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCard card }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "영상 파일 선택",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("영상 파일") { Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.m4v"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count == 0) return;

        card.UpdateDetails(card.Title, card.PublishedAt, card.OnlineUrl, files[0].Path.LocalPath);
        _ = DetectLocalDurationAsync(card);
        if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
            SaveChannel(channel);
    }

    private async void AddCardOnlineUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MediaCard card }) return;
        var urlInput = new TextBox { PlaceholderText = "https://...", MinHeight = 38 };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "온라인 영상 주소 추가",
            Width = 440,
            Height = 210,
            MinWidth = 440,
            MinHeight = 210,
            MaxWidth = 440,
            MaxHeight = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var save = new Button { Content = "저장", MinWidth = 82, Classes = { "primary" }, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += (_, _) =>
        {
            var address = urlInput.Text?.Trim();
            if (!IsHttpUrl(address ?? ""))
            {
                error.Text = "HTTP 또는 HTTPS 주소를 입력해 주세요.";
                return;
            }
            dialog.Close(true);
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "온라인 영상 주소", FontSize = 18, FontWeight = FontWeight.SemiBold },
                urlInput,
                error,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
            }
        };
        if (await dialog.ShowDialog<bool>(this))
        {
            card.UpdateDetails(card.Title, card.PublishedAt, urlInput.Text?.Trim(), card.LocalFilePath);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        }
    }

    private static Uri AddVodStartPosition(Uri uri, string platform, double positionSeconds)
    {
        var parameter = platform.Equals("SOOP", StringComparison.OrdinalIgnoreCase)
            ? "change_second"
            : platform.Equals("CHZZK", StringComparison.OrdinalIgnoreCase) || platform.Equals("치지직", StringComparison.OrdinalIgnoreCase)
                ? "currentTime"
                : null;
        if (parameter is null) return uri;

        var builder = new UriBuilder(uri);
        var seconds = Math.Max(0, (long)Math.Floor(positionSeconds));
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals(parameter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        queryParts.Add($"{parameter}={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        builder.Query = string.Join('&', queryParts);
        return builder.Uri;
    }

    private void OpenLocalFile(MediaCard card)
    {
        var path = card.LocalFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowRefreshNotification("영상 파일을 찾을 수 없습니다.", isError: true);
            return;
        }

        var playerPath = _settingsStore.LoadVideoPlayerPath();
        try
        {
            if (string.IsNullOrWhiteSpace(playerPath))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }
            if (!File.Exists(playerPath))
            {
                ShowRefreshNotification("설정된 플레이어 실행 파일을 찾을 수 없습니다.", isError: true);
                return;
            }

            var arguments = _settingsStore.LoadVideoPlayerArguments(playerPath);
            var positionSeconds = card.WatchStatusLabel == "시청중" ? Math.Max(0, (long)Math.Floor(card.LastPositionSeconds)) : 0;
            var positionTime = TimeSpan.FromSeconds(positionSeconds);
            var positionHms = $"{(long)positionTime.TotalHours:00}:{positionTime.Minutes:00}:{positionTime.Seconds:00}";
            var fileArgument = QuoteWindowsArgument(path);
            var hasFilePlaceholder = arguments.Contains("{file}", StringComparison.OrdinalIgnoreCase);
            arguments = arguments.Replace("\"{file}\"", fileArgument, StringComparison.OrdinalIgnoreCase)
                .Replace("{file}", fileArgument, StringComparison.OrdinalIgnoreCase)
                .Replace("{position_hms}", positionHms, StringComparison.OrdinalIgnoreCase)
                .Replace("{position}", positionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            if (!hasFilePlaceholder) arguments = string.IsNullOrWhiteSpace(arguments) ? fileArgument : $"{arguments} {fileArgument}";

            Process.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = arguments,
                UseShellExecute = false
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            ShowRefreshNotification($"플레이어를 실행하지 못했습니다: {exception.Message}", isError: true);
        }
    }

    private static string QuoteWindowsArgument(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private async Task DetectLocalDurationAsync(MediaCard card)
    {
        var path = card.LocalFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "ffprobe",
                ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process is null) return;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) return;
            if (!string.Equals(card.LocalFilePath, path, StringComparison.OrdinalIgnoreCase)) return;
            card.SetDuration(seconds);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // ffprobe is optional; keep the duration unknown when it is unavailable.
        }
    }

    private async Task RefreshCardDurationAsync(MediaCard card)
    {
        var onlineUrl = card.OnlineUrl;
        var localPath = card.LocalFilePath;
        if (card.PlatformLabel == "SOOP" && !string.IsNullOrWhiteSpace(onlineUrl))
        {
            var duration = await FetchSoopVodDurationAsync(onlineUrl, card.SetDurationDiagnostic);
            if (duration is > 0 && string.Equals(card.OnlineUrl, onlineUrl, StringComparison.OrdinalIgnoreCase))
            {
                card.SetDuration(duration);
                if (card.ChannelUrl is not null && _channels.FirstOrDefault(item => item.ChannelUrl == card.ChannelUrl) is { } owner)
                    SaveChannel(owner);
                return;
            }
            if (string.Equals(card.LocalFilePath, localPath, StringComparison.OrdinalIgnoreCase) && card.HasLocalFilePath)
                await DetectLocalDurationAsync(card);
            return;
        }
        if (!string.IsNullOrWhiteSpace(onlineUrl) && IsHttpUrl(onlineUrl))
        {
            try
            {
                var duration = await FetchChzzkVodDurationAsync(onlineUrl, card.SetDurationDiagnostic);
                if (duration is > 0 && string.Equals(card.OnlineUrl, onlineUrl, StringComparison.OrdinalIgnoreCase))
                {
                    card.SetDuration(duration);
                    if (card.ChannelUrl is not null && _channels.FirstOrDefault(item => item.ChannelUrl == card.ChannelUrl) is { } owner)
                        SaveChannel(owner);
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Fall back to local media when the channel metadata cannot be refreshed.
            }
        }
        if (string.Equals(card.LocalFilePath, localPath, StringComparison.OrdinalIgnoreCase) && card.HasLocalFilePath)
            await DetectLocalDurationAsync(card);
    }

    private async Task RefreshDurationForWatchingCardAsync(MediaCard card)
    {
        var onlineUrl = card.OnlineUrl;
        double? duration = card.PlatformLabel == "SOOP"
            ? await FetchSoopVodDurationAsync(onlineUrl, card.SetDurationDiagnostic)
            : null;
        if (duration is null && card.PlatformLabel != "SOOP")
        {
            await RefreshCardDurationAsync(card);
            return;
        }
        if (duration is > 0 && string.Equals(card.OnlineUrl, onlineUrl, StringComparison.OrdinalIgnoreCase))
        {
            card.SetDuration(duration);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        }
    }

    private Task RefreshChannelDurationsAsync(ChannelEntry channel)
    {
        var cards = GetViewportVisibleCards()
            .Where(card => card.ChannelUrl == channel.ChannelUrl && !card.HasKnownDuration && _durationRefreshAttempted.Add(card))
            .ToArray();
        return RefreshChannelDurationsAsync(channel, cards);
    }

    private async Task RefreshChannelDurationsAsync(ChannelEntry channel, IReadOnlyCollection<MediaCard>? cardsToRefresh)
    {
        if (!_durationRefreshInProgress.Add(channel.ChannelUrl)) return;
        var cancellation = new CancellationTokenSource();
        _durationRefreshCancellation[channel.ChannelUrl] = cancellation;
        var cancellationToken = cancellation.Token;
        try
        {
            var channelCards = (cardsToRefresh?.ToArray() ?? GetViewportVisibleCards().Where(card => card.ChannelUrl == channel.ChannelUrl).ToArray())
                .Where(card => !card.HasKnownDuration)
                .ToArray();
            AppLogger.Info("Duration", $"Starting channel duration refresh. Platform={channel.Platform}; ChannelId={channel.ChannelId}; Cards={channelCards.Length}; PlatformLabels={string.Join(',', channelCards.Select(card => card.PlatformLabel).Distinct())}");
            if (channel.Platform == "SOOP")
            {
                await Task.WhenAll(channelCards.Select(async card =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var duration = await FetchSoopVodDurationAsync(card.OnlineUrl, card.SetDurationDiagnostic, cancellationToken);
                    if (duration is > 0) card.SetDuration(duration);
                    else if (card.HasLocalFilePath) await DetectLocalDurationAsync(card);
                }));
            }
            else
            {
                await Task.WhenAll(channelCards.Select(async card =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var duration = await FetchChzzkVodDurationAsync(card.OnlineUrl, card.SetDurationDiagnostic, cancellationToken);
                    if (duration is > 0) card.SetDuration(duration);
                    else if (card.HasLocalFilePath) await DetectLocalDurationAsync(card);
                }));
            }
            SaveChannel(channel);
            AppLogger.Info("Duration", $"Finished channel duration refresh. Platform={channel.Platform}; ChannelId={channel.ChannelId}; Known={channelCards.Count(card => card.HasKnownDuration)}/{channelCards.Length}");
        }
        catch (OperationCanceledException)
        {
            foreach (var card in cardsToRefresh ?? [])
            {
                if (!card.HasKnownDuration)
                    _durationRefreshAttempted.Remove(card);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Duration", $"Channel duration refresh failed. Platform={channel.Platform}; ChannelId={channel.ChannelId}", exception);
            foreach (var card in _allCards.Where(card => card.ChannelUrl == channel.ChannelUrl && card.HasLocalFilePath))
                _ = DetectLocalDurationAsync(card);
        }
        finally
        {
            if (_durationRefreshCancellation.TryGetValue(channel.ChannelUrl, out var active) && ReferenceEquals(active, cancellation))
                _durationRefreshCancellation.Remove(channel.ChannelUrl);
            cancellation.Dispose();
            _durationRefreshInProgress.Remove(channel.ChannelUrl);
            var refreshedCards = cardsToRefresh?.ToHashSet() ?? [];
            foreach (var card in GetViewportVisibleCards().Where(card =>
                         card.ChannelUrl == channel.ChannelUrl && !card.HasKnownDuration && !refreshedCards.Contains(card)))
                _durationRefreshAttempted.Remove(card);
            Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
        }
    }

    private static HttpClient CreateSoopDurationHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://vod.sooplive.com");
        return client;
    }

    private static async Task<double?> FetchChzzkVodDurationAsync(string? vodUrl, Action<string>? reportDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(vodUrl, UriKind.Absolute, out var uri)) return null;
        var videoId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        reportDiagnostic?.Invoke("\uC870\uD68C \uC911");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var response = await client.GetAsync($"https://api.chzzk.naver.com/service/v2/videos/{Uri.EscapeDataString(videoId)}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("content", out var content))
            {
                var seconds = GetDurationSeconds(content, "duration", "videoDuration");
                if (seconds is > 0) return seconds;
            }
            reportDiagnostic?.Invoke("\uAE38\uC774 \uC5C6\uC74C");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            reportDiagnostic?.Invoke("\uC870\uD68C \uC624\uB958");
            AppLogger.Warn("Duration", $"CHZZK VOD={videoId}; direct duration lookup failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static async Task<double?> FetchSoopVodDurationAsync(string? vodUrl, Action<string>? reportDiagnostic = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(vodUrl, UriKind.Absolute, out var uri))
        {
            reportDiagnostic?.Invoke("\uC8FC\uC18C \uC624\uB958");
            AppLogger.Warn("SoopDuration", $"Skipped invalid VOD URL: {vodUrl ?? "<null>"}");
            return null;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var playerIndex = Array.FindIndex(segments, segment => segment.Equals("player", StringComparison.OrdinalIgnoreCase));
        var vodId = playerIndex >= 0 && playerIndex + 1 < segments.Length &&
                    long.TryParse(segments[playerIndex + 1], out _)
            ? segments[playerIndex + 1]
            : segments.LastOrDefault(segment => long.TryParse(segment, out _));
        if (vodId is null)
        {
            reportDiagnostic?.Invoke("\uC8FC\uC18C \uC624\uB958");
            AppLogger.Warn("SoopDuration", $"Could not extract numeric VOD ID. Path={uri.AbsolutePath}");
            return null;
        }

        reportDiagnostic?.Invoke("\uB300\uAE30 \uC911");
        AppLogger.Info("SoopDuration", $"VOD={vodId}; queued for duration request.");
        await SoopDurationLoadLimiter.WaitAsync(cancellationToken);
        try
        {
            foreach (var apiLevel in new[] { "10", "11" })
            {
                reportDiagnostic?.Invoke("\uC870\uD68C \uC911");
                AppLogger.Info("SoopDuration", $"VOD={vodId}; sending request; ApiLevel={apiLevel}.");
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.m.sooplive.com/station/video/a/view")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["nTitleNo"] = vodId,
                        ["nApiLevel"] = apiLevel,
                        ["nPlaylistIdx"] = "0"
                    })
                };
                request.Headers.Referrer = uri;
                HttpResponseMessage response;
                try
                {
                    response = await SoopDurationHttpClient.SendAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    AppLogger.Warn("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; request failed: {exception.GetType().Name}: {exception.Message}");
                    if (apiLevel == "11") throw;
                    continue;
                }
                using (response)
                {
                if (!response.IsSuccessStatusCode)
                {
                    reportDiagnostic?.Invoke($"HTTP {(int)response.StatusCode}");
                    AppLogger.Warn("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; HTTP={(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!json.RootElement.TryGetProperty("data", out var data))
                {
                    reportDiagnostic?.Invoke("\uC751\uB2F5 \uC624\uB958");
                    AppLogger.Warn("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; response has no data field. RootFields={GetJsonPropertyNames(json.RootElement)}");
                    continue;
                }
                if (TryGetMilliseconds(data, "total_file_duration", out var milliseconds))
                {
                    var seconds = milliseconds / 1000d;
                    reportDiagnostic?.Invoke("\uC870\uD68C \uC911");
                    AppLogger.Info("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; Source=total_file_duration; Milliseconds={milliseconds}; Seconds={seconds}");
                    return seconds;
                }
                if (data.TryGetProperty("files", out var files) && files.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var totalMilliseconds = files.EnumerateArray().Sum(file => TryGetMilliseconds(file, "duration", out var durationMs) ? durationMs : 0);
                    if (totalMilliseconds > 0)
                    {
                        var seconds = totalMilliseconds / 1000d;
                        reportDiagnostic?.Invoke("\uC870\uD68C \uC911");
                        AppLogger.Info("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; Source=files[].duration; Files={files.GetArrayLength()}; Milliseconds={totalMilliseconds}; Seconds={seconds}");
                        return seconds;
                    }
                }
                reportDiagnostic?.Invoke("\uAE38\uC774 \uC5C6\uC74C");
                AppLogger.Warn("SoopDuration", $"VOD={vodId}; ApiLevel={apiLevel}; Result={GetJsonValue(json.RootElement, "result")}; Code={GetJsonValue(data, "code")}; TotalFileDuration={GetJsonValue(data, "total_file_duration")}; DataFields={GetJsonPropertyNames(data)}; Files={GetJsonArrayLength(data, "files")}");
                }
            }
            reportDiagnostic?.Invoke("\uAE38\uC774 \uC5C6\uC74C");
            AppLogger.Warn("SoopDuration", $"VOD={vodId}; no duration found after trying API levels 10 and 11.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reportDiagnostic?.Invoke(exception is TaskCanceledException
                ? "\uC870\uD68C \uC2E4\uD328 (\uC751\uB2F5 \uC2DC\uAC04 \uCD08\uACFC)"
                : "\uC870\uD68C \uC624\uB958");
            AppLogger.Error("SoopDuration", $"VOD={vodId}; API request failed.", exception);
            return null;
        }
        finally
        {
            SoopDurationLoadLimiter.Release();
        }
    }

    private static string GetJsonPropertyNames(System.Text.Json.JsonElement element) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Object
            ? string.Join(',', element.EnumerateObject().Select(property => property.Name))
            : element.ValueKind.ToString();

    private static string GetJsonValue(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.ToString() : "missing";

    private static string FormatDurationForDiagnostic(double seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string GetJsonArrayLength(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
            ? value.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "missing";

    private static bool TryGetMilliseconds(System.Text.Json.JsonElement element, string propertyName, out double milliseconds)
    {
        milliseconds = 0;
        if (!element.TryGetProperty(propertyName, out var value)) return false;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
            return value.TryGetDouble(out milliseconds) && milliseconds > 0;
        return value.ValueKind == System.Text.Json.JsonValueKind.String &&
               double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out milliseconds) && milliseconds > 0;
    }

    private async Task<bool> LoadThumbnailAsync(MediaCard card, string? thumbnailUrl)
    {
        if (!card.ShowThumbnails || string.IsNullOrWhiteSpace(thumbnailUrl)) return false;
        var normalizedUrl = thumbnailUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{thumbnailUrl}" : thumbnailUrl;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return false;

        using var requestCancellation = new CancellationTokenSource();
        card.SetThumbnailRequest(requestCancellation);
        var enteredLimiter = false;
        try
        {
            await ThumbnailLoadLimiter.WaitAsync(requestCancellation.Token);
            enteredLimiter = true;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            if (card.PlatformLabel == "SOOP")
                request.Headers.Referrer = Uri.TryCreate(card.OnlineUrl, UriKind.Absolute, out var vodUri)
                    ? vodUri : new Uri("https://www.sooplive.com/");
            using var response = await client.SendAsync(request, requestCancellation.Token);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(requestCancellation.Token);
            if (!card.ShowThumbnails || requestCancellation.IsCancellationRequested) return false;
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } ownerChannel)
            {
                var path = _localDatabase.SaveThumbnail(ownerChannel.Platform, ownerChannel.ChannelId,
                    card.Title, card.PublishedAt, normalizedUrl, bytes, response.Content.Headers.ContentType?.MediaType);
                card.SetThumbnailPath(path);
            }
            card.SetThumbnail(bitmap);
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } owner)
                SaveChannel(owner);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            // Keep the already loaded cached thumbnail if the online image cannot be loaded.
            return false;
        }
        finally
        {
            if (enteredLimiter) ThumbnailLoadLimiter.Release();
            card.ClearThumbnailRequest(requestCancellation);
        }
    }

    private async Task RefreshCardThumbnailAsync(MediaCard card, string? refreshedThumbnailUrl = null)
    {
        if (!card.ShowThumbnails || !IsHttpUrl(card.OnlineUrl)) return;

        if (!string.IsNullOrWhiteSpace(refreshedThumbnailUrl))
            card.UpdateThumbnailUrl(refreshedThumbnailUrl);

        var currentThumbnailUrl = card.ThumbnailUrl;
        if (!string.IsNullOrWhiteSpace(currentThumbnailUrl) && await LoadThumbnailAsync(card, currentThumbnailUrl)) return;
        if (!card.ShowThumbnails) return;

        var fallbackUrl = card.PlatformLabel == "SOOP"
            ? await FetchSoopVodThumbnailUrlAsync(card.OnlineUrl!)
            : await FetchPageThumbnailUrlAsync(card.OnlineUrl!);
        if (card.ShowThumbnails && !string.IsNullOrWhiteSpace(fallbackUrl) && !string.Equals(fallbackUrl, currentThumbnailUrl, StringComparison.OrdinalIgnoreCase))
        {
            card.UpdateThumbnailUrl(fallbackUrl);
            await LoadThumbnailAsync(card, fallbackUrl);
        }
    }

    private static async Task<string?> FetchSoopVodThumbnailUrlAsync(string vodUrl)
    {
        if (!Uri.TryCreate(vodUrl, UriKind.Absolute, out var uri)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"(?:player|PLAYER/STATION)/(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return await FetchPageThumbnailUrlAsync(vodUrl);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.m.sooplive.com/station/video/a/view")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["nTitleNo"] = match.Groups[1].Value,
                    ["nApiLevel"] = "10"
                })
            };
            request.Headers.Referrer = new Uri(vodUrl);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("data", out var data))
                return GetFirstString(data, "thumb", "thumbnail", "thumbnail_url", "thumb_url");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Try the VOD page metadata as a fallback.
        }
        return await FetchPageThumbnailUrlAsync(vodUrl);
    }

    private static async Task<string?> FetchPageThumbnailUrlAsync(string pageUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            var html = await client.GetStringAsync(pageUrl);
            var match = System.Text.RegularExpressions.Regex.Match(html,
                "<meta[^>]+(?:property|name)=[\\\"'](?:og:image|twitter:image)[\\\"'][^>]+content=[\\\"']([^\\\"']+)[\\\"']|<meta[^>]+content=[\\\"']([^\\\"']+)[\\\"'][^>]+(?:property|name)=[\\\"'](?:og:image|twitter:image)[\\\"']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var value = System.Net.WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            return Uri.TryCreate(new Uri(pageUrl), value, out var imageUri) ? imageUri.ToString() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    private async void RefreshChannelVideos_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedChannel is null) return;
        var button = sender as Button;
        var addedCards = new List<MediaCard>();
        if (button is not null) button.IsEnabled = false;
        try
        {
            var channel = _selectedChannel;
            var foundVideos = await FetchChannelVideosAsync(channel);
            if (!_channels.Contains(channel)) return;
            if (channel.Platform == "SOOP")
            {
                _soopNextPageByChannel[channel.ChannelUrl] = 2;
                if (foundVideos.Count >= 60) _soopHasMoreByChannel.Add(channel.ChannelUrl);
                else _soopHasMoreByChannel.Remove(channel.ChannelUrl);
            }
            var addedCount = 0;
            foreach (var video in foundVideos)
            {
                var existingCard = _allCards.FirstOrDefault(card =>
                    string.Equals(card.OnlineUrl?.TrimEnd('/'), video.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if (existingCard is not null)
                {
                    if (video.DurationSeconds is > 0) existingCard.SetDuration(video.DurationSeconds);
                    _ = RefreshCardThumbnailAsync(existingCard, video.ThumbnailUrl);
                    if (!existingCard.HasKnownDuration && existingCard.HasLocalFilePath)
                        _ = DetectLocalDurationAsync(existingCard);
                    continue;
                }
                var card = new MediaCard(video.Title, channel.Platform, "VOD", video.Url,
                    "", "미시청", video.Url, channel.ChannelUrl, video.PublishedAt, video.ThumbnailUrl,
                    durationSeconds: video.DurationSeconds);
                _allCards.Add(card);
                addedCards.Add(card);
                _ = RefreshCardThumbnailAsync(card, video.ThumbnailUrl);
                addedCount++;
            }
            foreach (var card in _allCards.Where(card => card.ChannelUrl == channel.ChannelUrl && card.HasLocalFilePath &&
                                                          !foundVideos.Any(video => string.Equals(video.Url.TrimEnd('/'), card.OnlineUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && video.DurationSeconds is > 0)))
                _ = DetectLocalDurationAsync(card);
            SaveChannel(channel);
            UpdateCards();
            Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
            ShowRefreshNotification(addedCount == 0 ? "새로고침 할 내용 없음" : $"새로고침 완료 · 새 영상 {addedCount}개 추가");
        }
        catch (Exception)
        {
            foreach (var card in addedCards)
            {
                _allCards.Remove(card);
                card.Dispose();
            }
            UpdateCards();
            ShowRefreshNotification("새로고침 실패", isError: true);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void ShowRefreshNotification(string message, bool isError = false)
    {
        RefreshNotificationText.Text = message;
        RefreshNotification.BorderBrush = new SolidColorBrush(Color.Parse(isError ? "#E05D68" : "#35B879"));
        RefreshNotification.IsVisible = true;
        _refreshNotificationTimer.Stop();
        _refreshNotificationTimer.Start();
    }

    private void ShowGoogleDriveSyncProgress()
    {
        _driveSyncNotificationTimer.Stop();
        DriveSyncNotificationText.Text = "Google Drive와 동기화 중...";
        DriveSyncNotification.BorderBrush = new SolidColorBrush(Color.Parse("#35B879"));
        DriveSyncNotificationSpinner.IsVisible = true;
        DriveSyncNotification.IsVisible = true;
        _syncSpinnerTimer.Start();
    }

    private void ShowDriveSyncNotification(string message, bool isError = false)
    {
        StopGoogleDriveSyncAnimation();
        DriveSyncNotificationText.Text = message;
        DriveSyncNotification.BorderBrush = new SolidColorBrush(Color.Parse(isError ? "#E05D68" : "#35B879"));
        DriveSyncNotification.IsVisible = true;
        _driveSyncNotificationTimer.Stop();
        _driveSyncNotificationTimer.Start();
    }

    private void StopGoogleDriveSyncAnimation()
    {
        _syncSpinnerTimer.Stop();
        DriveSyncNotificationSpinner.IsVisible = false;
    }

    private async void CardsScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        RefreshUnknownVisibleCardDurations();
        if (_isLoadingMoreAtScrollEnd) return;
        var remaining = scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height;
        if (remaining > 160) return;
        if (_visibleCards.Count < _filteredCards.Count)
        {
            _cardDisplayLimit += _settingsStore.LoadCardBatchSize();
            UpdateCards();
            Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
            return;
        }
        var channel = _selectedChannel;
        if (channel is null || channel.Platform != "SOOP" || !_soopHasMoreByChannel.Contains(channel.ChannelUrl)) return;
        _isLoadingMoreAtScrollEnd = true;
        try
        {
            var page = _soopNextPageByChannel.GetValueOrDefault(channel.ChannelUrl, 2);
            var videos = await FetchChannelVideosAsync(channel, page);
            if (!_channels.Contains(channel)) return;
            foreach (var video in videos)
            {
                if (_allCards.Any(card => string.Equals(card.OnlineUrl?.TrimEnd('/'), video.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) continue;
                var card = new MediaCard(video.Title, channel.Platform, "VOD", video.Url,
                    "", "미시청", video.Url, channel.ChannelUrl, video.PublishedAt, video.ThumbnailUrl,
                    durationSeconds: video.DurationSeconds);
                _allCards.Add(card);
                // Duration lookup is queued only after the new card enters the viewport.
                _ = RefreshCardThumbnailAsync(card, video.ThumbnailUrl);
            }
            _soopNextPageByChannel[channel.ChannelUrl] = page + 1;
            if (videos.Count < 60) _soopHasMoreByChannel.Remove(channel.ChannelUrl);
            SaveChannel(channel);
            UpdateCards();
            Dispatcher.UIThread.Post(RefreshUnknownVisibleCardDurations, DispatcherPriority.Background);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Scrolling to the end again retries a failed page request.
        }
        finally
        {
            _isLoadingMoreAtScrollEnd = false;
        }
    }

    private static async Task<List<(string Title, string Url, DateTimeOffset? PublishedAt, string? ThumbnailUrl, double? DurationSeconds)>> FetchChannelVideosAsync(ChannelEntry channel, int soopPage = 1, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(channel.ChannelUrl, UriKind.Absolute, out var channelUri) ||
            !TryGetPlatformChannelId(channel.Platform == "SOOP" ? "SOOP" : "CHZZK", channelUri, out var channelId))
            throw new InvalidOperationException("Channel ID could not be extracted.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        var videos = new List<(string Title, string Url, DateTimeOffset? PublishedAt, string? ThumbnailUrl, double? DurationSeconds)>();
        if (channel.Platform == "SOOP")
        {
            const int pageSize = 60;
            var seenVodIds = new HashSet<string>(StringComparer.Ordinal);
            var page = Math.Max(1, soopPage);
            var url = $"https://chapi.sooplive.com/api/{Uri.EscapeDataString(channelId)}/vods/all/streamer?page={page}&per_page={pageSize}&orderby=reg_date";
            using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidOperationException("The SOOP VOD response did not contain a valid data list.");
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("title_no", out var titleNo) ? titleNo.ToString() : null;
                var title = item.TryGetProperty("title_name", out var titleName) ? titleName.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !seenVodIds.Add(id)) continue;
                var dateValue = item.TryGetProperty("reg_date", out var regDate) ? regDate.ToString() : null;
                var thumbnailUrl = GetFirstString(item, "thumb", "thumbnail", "thumb_url", "thumbnail_url", "title_img", "title_thumbnail");
                var durationSeconds = GetDurationSeconds(item, "total_file_duration");
                if (durationSeconds is > 0) durationSeconds /= 1000d;
                durationSeconds ??= GetDurationSeconds(item, "file_duration", "duration");
                videos.Add((title, $"https://vod.sooplive.com/player/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue), thumbnailUrl,
                    durationSeconds));
            }
            return videos;
        }

        for (var page = 0; page < 5; page++)
        {
            var url = $"https://api.chzzk.naver.com/service/v1/channels/{Uri.EscapeDataString(channelId)}/videos?sortType=LATEST&pagingType=PAGE&page={page}&size=30";
            using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(url, cancellationToken));
            if (!json.RootElement.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidOperationException("The CHZZK VOD response did not contain a valid content list.");
            if (data.GetArrayLength() == 0) break;
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("videoNo", out var videoNo) ? videoNo.ToString() : null;
                var title = item.TryGetProperty("videoTitle", out var videoTitle) ? videoTitle.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var dateValue = item.TryGetProperty("publishDate", out var publishDate) ? publishDate.GetString() : null;
                var thumbnailUrl = item.TryGetProperty("thumbnailImageUrl", out var thumbnail) ? thumbnail.GetString() : null;
                videos.Add((title, $"https://chzzk.naver.com/video/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue), thumbnailUrl,
                    GetDurationSeconds(item, "duration", "videoDuration")));
            }
            if (data.GetArrayLength() < 30) break;
        }
        return videos;
    }

    private static string? GetFirstString(System.Text.Json.JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
        return null;
    }

    private static double? GetDurationSeconds(System.Text.Json.JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds > 0) return seconds;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0)
                return seconds;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                TimeSpan.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var time) && time.TotalSeconds > 0)
                return time.TotalSeconds;
        }
        return null;
    }

    private static DateTimeOffset? ParsePublishedDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var date) ? date : null;

    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async void DirectAddChannelVideo_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedChannel is { } channel) await AddChannelVideoAsync(channel);
    }

    private async Task AddChannelVideoAsync(ChannelEntry channel)
    {
        var titleInput = new TextBox { PlaceholderText = "영상 제목", MinHeight = 38 };
        var memoInput = new TextBox { PlaceholderText = "메모", Height = 90, MinHeight = 90, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top };
        ScrollViewer.SetVerticalScrollBarVisibility(memoInput, ScrollBarVisibility.Visible);
        var urlInput = new TextBox { PlaceholderText = "영상 링크 (선택)", MinHeight = 38 };
        var filePathInput = new TextBox { PlaceholderText = "파일 링크/경로 (선택)", MinHeight = 38 };
        var dateInput = new TextBox { PlaceholderText = "방송 날짜 (선택, 예: 2026-10-13)", MinHeight = 38 };
        var calendarButton = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE787",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
                Foreground = new SolidColorBrush(Color.Parse("#7C5CFC")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            },
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(calendarButton, "날짜 선택");
        var popupSurface = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#252832" : "#FFFFFF"));
        var popupBorder = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#454955" : "#D9DBE3"));
        var calendarForeground = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#F1F2F7" : "#202230"));
        var calendar = new Calendar { SelectedDate = DateTime.Today, Background = popupSurface, Foreground = calendarForeground };
        var todayButton = new Button
        {
            Content = "오늘",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 64,
            Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#39324F" : "#F0EDFF")),
            BorderBrush = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#5A4F7A" : "#D8D0FA")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Foreground = new SolidColorBrush(Color.Parse(_isDarkTheme ? "#D2C8FF" : "#654CD3"))
        };
        var calendarPopup = new Popup
        {
            PlacementTarget = calendarButton,
            Placement = PlacementMode.Bottom,
            Child = new Border
            {
                Background = popupSurface,
                BorderBrush = popupBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children = { calendar, todayButton }
                }
            }
        };
        todayButton.Click += (_, _) =>
        {
            var today = DateTime.Today;
            calendar.DisplayDate = today;
            calendar.SelectedDate = today;
            dateInput.Text = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            calendarPopup.IsOpen = false;
        };
        var dateRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,38"), ColumnSpacing = 8 };
        dateRow.Children.Add(dateInput);
        dateRow.Children.Add(calendarButton);
        Grid.SetColumn(calendarButton, 1);
        calendarButton.Click += (_, _) =>
        {
            if (DateTime.TryParseExact(dateInput.Text?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var currentDate))
            {
                calendar.DisplayDate = currentDate;
                calendar.SelectedDate = currentDate;
            }
            calendarPopup.IsOpen = !calendarPopup.IsOpen;
        };
        calendar.PropertyChanged += (_, args) =>
        {
            if (args.Property == Calendar.SelectedDateProperty && calendar.SelectedDate is { } date)
            {
                dateInput.Text = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                calendarPopup.IsOpen = false;
            }
        };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "채널 영상 직접 추가", Width = 500, Height = 540, MinWidth = 500, MinHeight = 540,
            MaxWidth = 500, MaxHeight = 540, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var save = new Button { Content = "추가", MinWidth = 82, Classes = { "primary" }, IsDefault = true };
        var chooseFile = new Button { Content = "찾기", MinWidth = 66 };
        chooseFile.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "영상 파일 선택", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("영상 파일") { Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.m4v"] }, FilePickerFileTypes.All]
            });
            if (files.Count == 0) return;
            filePathInput.Text = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(titleInput.Text)) titleInput.Text = Path.GetFileNameWithoutExtension(files[0].Name);
        };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            var title = titleInput.Text?.Trim();
            var address = urlInput.Text?.Trim();
            var filePath = filePathInput.Text?.Trim();
            DateTimeOffset? publishedAt = null;
            if (!string.IsNullOrWhiteSpace(dateInput.Text))
            {
                if (!DateTime.TryParseExact(dateInput.Text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsedDate))
                {
                    error.Text = "날짜를 YYYY-MM-DD 형식으로 입력해 주세요.";
                    return;
                }
                publishedAt = new DateTimeOffset(parsedDate, TimeZoneInfo.Local.GetUtcOffset(parsedDate));
            }
            if (string.IsNullOrWhiteSpace(title)) { error.Text = "영상 제목을 입력해 주세요."; return; }
            if (!string.IsNullOrWhiteSpace(address) && !IsHttpUrl(address))
            { error.Text = "URL은 HTTP 또는 HTTPS 주소로 입력해 주세요."; return; }
            if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(filePath))
            { error.Text = "영상 URL 또는 파일 경로를 하나 이상 입력해 주세요."; return; }

            var primarySource = !string.IsNullOrWhiteSpace(address) ? address : filePath!;
            var sourceLabel = !string.IsNullOrWhiteSpace(address) ? new Uri(address).Host : Path.GetFileName(filePath) ?? filePath!;
            var hasUrl = !string.IsNullOrWhiteSpace(address);
            var hasFile = !string.IsNullOrWhiteSpace(filePath);
            var sourceType = hasUrl && hasFile ? "링크+파일" : hasUrl ? "링크" : "PC 파일";
            _allCards.Add(new MediaCard(title!, channel.Platform, sourceType, sourceLabel,
                hasFile ? File.Exists(filePath) ? "파일 확인됨" : "파일 확인 필요" : "링크 사용 가능",
                "안 봄", primarySource, channel.ChannelUrl, publishedAt, onlineUrl: address, localFilePath: filePath, memo: memoInput.Text));
            SaveChannel(channel);
            UpdateCards();
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"{channel.DisplayName} 영상 추가", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                titleInput, urlInput,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 8,
                    Children = { filePathInput, chooseFile }
                },
                dateRow,
                memoInput,
                calendarPopup, error,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
            }
        };
        Grid.SetColumn(chooseFile, 1);
        await dialog.ShowDialog(this);
    }

    private async void AddChannel_Click(object? sender, RoutedEventArgs e)
    {
        var platformPicker = new ComboBox { MinHeight = 38 };
        platformPicker.Items.Add("치지직");
        platformPicker.Items.Add("SOOP");
        platformPicker.SelectedIndex = GetSelectedPlatform() == "SOOP" ? 1 : 0;

        var channelIdInput = new TextBox { PlaceholderText = "채널 ID 또는 전체 주소", MinHeight = 38 };
        string? profileImageUrl = null;
        var isLive = false;
        var profileLookupSucceeded = false;
        var example = new TextBlock { FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void UpdateExample()
        {
            var isSoop = platformPicker.SelectedIndex == 1;
            example.Inlines?.Clear();
            example.Inlines?.Add(new Run(isSoop ? "https://www.sooplive.com/station/" : "https://chzzk.naver.com/"));
            example.Inlines?.Add(new Run(isSoop ? "{스트리머 ID}" : "{채널 ID}")
            {
                Foreground = Avalonia.Media.Brushes.MediumPurple,
                FontWeight = Avalonia.Media.FontWeight.Bold
            });
        }
        platformPicker.SelectionChanged += (_, _) =>
        {
            profileImageUrl = null;
            isLive = false;
            profileLookupSucceeded = false;
            UpdateExample();
        };
        channelIdInput.TextChanged += (_, _) =>
        {
            profileImageUrl = null;
            isLive = false;
            profileLookupSucceeded = false;
        };
        UpdateExample();

        var nameInput = new TextBox { PlaceholderText = "채널명", MinHeight = 38 };
        var descriptionInput = new TextBox
        {
            PlaceholderText = "채널 설명",
            MinHeight = 82,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var message = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.Gray };
        var lookupButton = new Button { Content = "채널 정보 불러오기", MinHeight = 36 };
        async Task<bool> LookupProfileAsync()
        {
            var platformKey = platformPicker.SelectedIndex == 1 ? "SOOP" : "CHZZK";
            if (!TryCreateChannelUrl(platformKey, channelIdInput.Text, out var lookupUri, out var error))
            {
                message.Text = error;
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return false;
            }

            var channelId = GetPlatformChannelId(platformKey, lookupUri!);
            try
            {
                lookupButton.IsEnabled = false;
                message.Text = $"{platformKey} 채널 정보를 조회하고 있습니다…";
                var profile = await LookupChannelProfileAsync(platformKey, channelId);
                nameInput.Text = profile.Name;
                descriptionInput.Text = profile.Description;
                profileImageUrl = profile.ImageUrl;
                isLive = profile.IsLive;
                profileLookupSucceeded = true;
                message.Text = $"{platformKey} 채널명과 설명을 불러왔습니다.";
                message.Foreground = Avalonia.Media.Brushes.DarkGreen;
                return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                var failure = exception is TaskCanceledException
                    ? $"{platformKey} 요청 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요."
                    : $"{platformKey} 채널 정보를 가져오지 못했습니다. ID 또는 네트워크 연결을 확인해 주세요.";
                message.Text = $"채널 ID 또는 전체 주소를 확인해 주세요. {failure}";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return false;
            }
            finally
            {
                lookupButton.IsEnabled = true;
            }
        }
        lookupButton.Click += async (_, _) => await LookupProfileAsync();

        var cancelButton = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var saveButton = new Button { Content = "채널 추가", MinWidth = 92, IsDefault = true };
        var dialog = new Window
        {
            Title = "채널 추가",
            Width = 520,
            Height = 560,
            MinWidth = 520,
            MinHeight = 560,
            MaxWidth = 520,
            MaxHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        cancelButton.Click += (_, _) => dialog.Close();
        saveButton.Click += async (_, _) =>
        {
            var platform = platformPicker.SelectedItem?.ToString() ?? "치지직";
            var platformKey = platformPicker.SelectedIndex == 1 ? "SOOP" : "CHZZK";
            if (!TryCreateChannelUrl(platformKey, channelIdInput.Text, out var channelUri, out var error))
            {
                message.Text = error;
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
            }
            var channelId = GetPlatformChannelId(platformKey, channelUri!);
            if (_channels.Any(channel => channel.Platform == platform && channel.ChannelUrl == channelUri!.AbsoluteUri))
            {
                message.Text = "이미 등록된 채널 ID입니다.";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
            }

            if (!profileLookupSucceeded)
            {
                saveButton.IsEnabled = false;
                var lookupSucceeded = await LookupProfileAsync();
                saveButton.IsEnabled = true;
                if (!lookupSucceeded) return;
            }
            var name = nameInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                message.Text = "채널 ID 또는 전체 주소를 확인해 주세요. 채널명을 가져오지 못했습니다.";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
            }

            var channel = new ChannelEntry(platform, channelId!, name, descriptionInput.Text?.Trim() ?? "", channelUri!.AbsoluteUri);
            channel.IsLive = isLive;
            _channels.Add(channel);
            SaveChannel(channel);
            UpdateVisibleChannels();
            dialog.Close();
            _ = LoadChannelAppearanceAsync(channel, platformKey, channelId!, profileImageUrl, profileLookupSucceeded);
        };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "채널 등록", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = "플랫폼을 선택하고 채널 ID 또는 전체 주소를 입력하세요.", FontSize = 12, Foreground = Avalonia.Media.Brushes.Gray },
                platformPicker,
                channelIdInput,
                example,
                lookupButton,
                nameInput,
                descriptionInput,
                message,
                buttons
            }
        };
        await dialog.ShowDialog(this);
    }

    private static bool TryCreateChannelUrl(string platform, string? rawId, out Uri? channelUri, out string error)
    {
        channelUri = null;
        var channelId = rawId?.Trim();
        if (string.IsNullOrWhiteSpace(channelId))
        {
            error = "채널 ID 또는 전체 주소를 입력해 주세요.";
            return false;
        }
        if (Uri.TryCreate(channelId, UriKind.Absolute, out var suppliedUri))
        {
            if (!TryValidateChannelUrl(suppliedUri.AbsoluteUri, platform, out var validatedUri, out error)) return false;
            if (!TryGetPlatformChannelId(platform, validatedUri!, out channelId))
            {
                error = "채널 주소에서 ID를 확인할 수 없습니다.";
                return false;
            }
        }
        else if (channelId.Any(char.IsWhiteSpace) || channelId.Contains('/') || channelId.Contains('?') || channelId.Contains('#'))
        {
            error = "올바른 채널 ID 또는 전체 주소를 입력해 주세요.";
            return false;
        }
        if (platform == "SOOP" && (channelId.Length is < 3 or > 20 || channelId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            error = "SOOP ID는 영문, 숫자, 밑줄(_)로 된 3~20자여야 합니다.";
            return false;
        }
        if (platform == "CHZZK" && (channelId.Length > 100 || channelId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            error = "치지직 채널 ID에 사용할 수 없는 문자가 있습니다.";
            return false;
        }

        var address = platform == "SOOP"
            ? $"https://www.sooplive.com/station/{Uri.EscapeDataString(channelId)}"
            : $"https://chzzk.naver.com/{Uri.EscapeDataString(channelId)}";
        return TryValidateChannelUrl(address, platform, out channelUri, out error);
    }
    private static async Task<(string Name, string Description, string? ImageUrl, bool IsLive)> LookupChannelProfileAsync(string platform, string channelId)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        var urls = platform == "CHZZK"
            ? new[] { $"https://api.chzzk.naver.com/service/v1/channels/{Uri.EscapeDataString(channelId)}" }
            : new[]
            {
                $"https://chapi.sooplive.com/api/{Uri.EscapeDataString(channelId)}/station",
                $"https://chapi.sooplive.co.kr/api/{Uri.EscapeDataString(channelId)}/station"
            };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    continue;
                }

                using var json = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                var root = json.RootElement;
                if (platform == "CHZZK")
                {
                    if (!root.TryGetProperty("code", out var responseCode) || !IsChzzkSuccessCode(responseCode))
                        throw new InvalidOperationException("CHZZK profile response did not indicate success.");
                    if (!root.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Object)
                        throw new InvalidOperationException("Channel content missing.");
                    var responseChannelId = content.TryGetProperty("channelId", out var channelIdValue) ? channelIdValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(responseChannelId) ||
                        !responseChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("CHZZK response channel ID did not match the requested ID.");
                    var name = content.TryGetProperty("channelName", out var channelName) ? channelName.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Channel name missing.");
                    var description = content.TryGetProperty("channelDescription", out var channelDescription)
                        ? channelDescription.GetString() ?? ""
                        : "";
                    var imageUrl = content.TryGetProperty("channelImageUrl", out var channelImageUrl)
                        ? channelImageUrl.GetString()
                        : null;
                    var isLive = content.TryGetProperty("openLive", out var openLive) && openLive.ValueKind == System.Text.Json.JsonValueKind.True;
                    return (name, description, imageUrl, isLive);
                }

                if (!root.TryGetProperty("station", out var station) || station.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidOperationException("SOOP station data missing.");
                var responseSoopId = ReadFirstString(station, "user_id", "userId", "station_user_id", "stationUserId", "bj_id", "bjId")
                    ?? ReadFirstString(root, "user_id", "userId", "station_user_id", "stationUserId", "bj_id", "bjId");
                if (string.IsNullOrWhiteSpace(responseSoopId) ||
                    !responseSoopId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SOOP response channel ID did not match the requested ID.");
                var soopName = station.TryGetProperty("user_nick", out var userNick) ? userNick.GetString() : null;
                if (string.IsNullOrWhiteSpace(soopName) && station.TryGetProperty("station_name", out var stationName))
                    soopName = stationName.GetString();
                if (string.IsNullOrWhiteSpace(soopName)) throw new InvalidOperationException("SOOP channel name missing.");
                var soopDescription = "";
                if (station.TryGetProperty("display", out var display) && display.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    display.TryGetProperty("profile_text", out var profileText))
                    soopDescription = profileText.GetString() ?? "";
                var soopImageUrl = root.TryGetProperty("profile_image", out var profileImage)
                    ? profileImage.GetString()
                    : station.TryGetProperty("profile_image", out var stationProfileImage)
                        ? stationProfileImage.GetString()
                        : null;
                var soopIsLive = root.TryGetProperty("broad", out var broad) && broad.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                 broad.TryGetProperty("broad_no", out var broadNumber) && IsPositiveNumber(broadNumber);
                return (soopName, soopDescription, soopImageUrl, soopIsLive);
            }
            catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
            {
                lastError = exception;
            }
        }

        throw new HttpRequestException("No channel profile endpoint returned usable data.", lastError);
    }

    private static bool IsChzzkSuccessCode(System.Text.Json.JsonElement code) => code.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => code.TryGetInt32(out var number) && number == 200,
        System.Text.Json.JsonValueKind.String => string.Equals(code.GetString(), "200", StringComparison.Ordinal),
        _ => false
    };

    private async void DriveCheckTimer_Tick(object? sender, EventArgs e) => await CheckDriveForChangesAsync();

    private async Task CheckDriveForChangesAsync()
    {
        if (!_isWindowActive || _isSyncingDrive || _isCheckingDrive || _driveSyncClient is null) return;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            ShowDriveSyncNotification("Google Drive 변경 확인 건너뜀 (오프라인)");
            return;
        }

        _isCheckingDrive = true;
        ShowGoogleDriveSyncProgress();
        DriveSyncNotificationText.Text = "Google Drive 변경 확인 중...";
        try
        {
            if (!await _driveSyncClient.HasRemoteChangesAsync())
            {
                ShowDriveSyncNotification("Google Drive 변경 확인 완료 (변경 없음)");
                return;
            }
            var credentials = OperatingSystem.IsWindows() ? WindowsCredentialVault.ReadGoogleDrive() : null;
            if (credentials is null)
            {
                ShowDriveSyncNotification("Google Drive 변경 확인 완료 (계정 연결 필요)", isError: true);
                return;
            }
            await SyncGoogleDriveLibraryAsync(credentials);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Google Drive change check failed: {exception.Message}");
            ShowDriveSyncNotification($"Google Drive sync failed: {exception.Message}", isError: true);
        }
        finally
        {
            _isCheckingDrive = false;
        }
    }

    private static string? ReadFirstString(System.Text.Json.JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static bool IsPositiveNumber(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Number => value.TryGetInt64(out var number) && number > 0,
        System.Text.Json.JsonValueKind.String => long.TryParse(value.GetString(), out var number) && number > 0,
        _ => false
    };

    private static bool TryGetSoopChannelId(Uri channelUri, out string channelId)
    {
        var segments = channelUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        channelId = segments.Length == 2 && segments[0].Equals("station", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : segments.Length == 1 ? segments[0] : "";
        return channelId.Length is >= 3 and <= 20 && channelId.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
    private static bool TryValidateChannelUrl(string? address, string platform, out Uri? uri, out string error)
    {
        uri = null;
        error = "올바른 채널 주소를 입력해 주세요.";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)) return false;

        var host = candidate.Host.ToLowerInvariant();
        var isChzzk = host == "chzzk.naver.com" || host.EndsWith(".chzzk.naver.com", StringComparison.Ordinal);
        var isSoop = host == "sooplive.com" || host.EndsWith(".sooplive.com", StringComparison.Ordinal) ||
                     host == "afreecatv.com" || host.EndsWith(".afreecatv.com", StringComparison.Ordinal);
        if (platform == "CHZZK" && !isChzzk || platform == "SOOP" && !isSoop)
        {
            error = $"{platform} 채널 주소인지 확인해 주세요.";
            return false;
        }
        if (candidate.AbsolutePath.Trim('/') is "" or "station")
        {
            error = "채널 경로가 포함된 주소를 입력해 주세요.";
            return false;
        }

        uri = candidate;
        error = "";
        return true;
    }
    private static string GetPlatformChannelId(string platform, Uri channelUri)
    {
        var isValid = platform == "SOOP"
            ? TryGetSoopChannelId(channelUri, out var channelId)
            : TryGetChzzkChannelId(channelUri, out channelId);
        return isValid ? channelId : "";
    }

    private static bool TryGetPlatformChannelId(string platform, Uri channelUri, out string channelId) =>
        platform == "SOOP" ? TryGetSoopChannelId(channelUri, out channelId) : TryGetChzzkChannelId(channelUri, out channelId);

    private static bool TryGetChzzkChannelId(Uri channelUri, out string channelId)
    {
        var segments = channelUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        channelId = segments.Length == 2 && segments[0].Equals("channel", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : segments.Length == 1 ? segments[0] : "";
        return channelId.Length > 0;
    }

    private async void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = new SettingsWindow(_isDarkTheme, ApplyTheme, _settingsStore, SyncGoogleDriveLibraryAsync, SetDriveCheckInterval);
        await settings.ShowDialog(this);
    }

    private void SetDriveCheckInterval(TimeSpan interval)
    {
        _driveCheckTimer.Interval = interval;
        if (_isWindowActive) _driveCheckTimer.Start();
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "CSV library export",
            SuggestedFileName = "vod-library.csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;
        try
        {
            LibraryTransfer.ExportCsv(file.Path.LocalPath, _localDatabase.LoadAll());
            await ShowTransferMessageAsync("Export complete", "The library was exported as CSV.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowTransferMessageAsync("Export failed", exception.Message);
        }
    }

    private async void DeleteAllLibrary_Click(object? sender, RoutedEventArgs e)
    {
        var confirmation = new Window
        {
            Title = "모든 채널 및 기록 삭제",
            Width = 440,
            Height = 220,
            MinWidth = 440,
            MinHeight = 220,
            MaxWidth = 440,
            MaxHeight = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancelButton = new Button { Content = "취소", MinWidth = 88, IsCancel = true };
        var confirmButton = new Button
        {
            Content = "확인",
            MinWidth = 88,
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#D84040")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#D84040"))
        };
        cancelButton.Click += (_, _) => confirmation.Close(false);
        confirmButton.Click += (_, _) => confirmation.Close(true);
        confirmation.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = "저장된 채널 및 VOD 시청 기록이 전부 삭제 됩니다.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, confirmButton }
                }
            }
        };
        if (await confirmation.ShowDialog<bool>(this) != true) return;

        try
        {
            _driveUploadTimer.Stop();
            _driveUploadPending = false;
            _localDatabase.DeleteAll();
            foreach (var channel in _channels) channel.Dispose();
            foreach (var card in _allCards) card.Dispose();
            _channels.Clear();
            _allCards.Clear();
            _visibleChannels.Clear();
            _visibleCards.Clear();
            _filteredCards.Clear();
            _selectedChannelUrls.Clear();
            _selectedChannel = null;
            _channelSelectionAnchor = null;
            _selectionAnchor = null;
            _soopNextPageByChannel.Clear();
            _soopHasMoreByChannel.Clear();
            _lastSavedDriveSnapshots.Clear();
            _isAllLibraryView = true;
            _isShowAllChannels = true;
            UpdateNavigationSelection("all");
            ChannelEmptyState.IsVisible = true;
            ChannelScroller.IsVisible = false;
            UpdateCards();
            UpdateVisibleChannels();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowTransferMessageAsync("기록 삭제 실패", exception.Message);
        }
    }

    private async void ImportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "CSV 라이브러리 가져오기", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV 파일") { Patterns = ["*.csv"] }]
        });
        if (files.Count == 0) return;
        try
        {
            var rows = LibraryTransfer.ImportCsv(files[0].Path.LocalPath);
            foreach (var group in rows.GroupBy(row => $"{row.Platform}:{row.ChannelId}", StringComparer.OrdinalIgnoreCase))
            {
                var urls = group.Select(row => row.ChannelUrl).Where(url => url.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (urls.Length > 1) throw new InvalidDataException($"{group.First().Platform}/{group.First().ChannelId}에 서로 다른 채널 주소가 포함되어 있습니다.");
                var existingChannel = _channels.FirstOrDefault(item => item.Platform.Equals(group.First().Platform, StringComparison.OrdinalIgnoreCase) && item.ChannelId.Equals(group.First().ChannelId, StringComparison.OrdinalIgnoreCase));
                if (existingChannel is not null && urls.Length == 1 && !existingChannel.ChannelUrl.Equals(urls[0], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{group.First().Platform}/{group.First().ChannelId}의 채널 주소가 기존 데이터와 일치하지 않습니다.");
            }
            foreach (var group in rows.GroupBy(row => $"{row.Platform}:{row.VodId}", StringComparer.OrdinalIgnoreCase))
                if (group.Select(row => row.ChannelId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    throw new InvalidDataException($"VOD ID {group.First().VodId}가 서로 다른 채널에 중복 지정되어 있습니다.");
            var added = 0;
            foreach (var row in rows)
            {
                var channel = _channels.FirstOrDefault(item => item.Platform.Equals(row.Platform, StringComparison.OrdinalIgnoreCase) && item.ChannelId.Equals(row.ChannelId, StringComparison.OrdinalIgnoreCase));
                var channelUrl = row.ChannelUrl;
                if (channel is null)
                {
                    if (string.IsNullOrWhiteSpace(channelUrl))
                        channelUrl = row.Platform == "SOOP" ? $"https://www.sooplive.com/station/{Uri.EscapeDataString(row.ChannelId)}" : $"https://chzzk.naver.com/{Uri.EscapeDataString(row.ChannelId)}";
                    channel = new ChannelEntry(row.Platform, row.ChannelId, string.IsNullOrWhiteSpace(row.ChannelName) ? row.ChannelId : row.ChannelName, "", channelUrl);
                    _channels.Add(channel);
                }
                else if (!string.IsNullOrWhiteSpace(channelUrl) && !channel.ChannelUrl.Equals(channelUrl, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{row.Platform}/{row.ChannelId}의 채널 주소가 기존 데이터와 일치하지 않습니다.");

                var existing = _allCards.FirstOrDefault(card => card.ChannelUrl == channel.ChannelUrl &&
                    card.VodId.Equals(row.VodId, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.UpdateDetails(row.Title, row.PublishedAt, row.Url, row.LocalFilePath, row.VodId);
                    existing.IsFavorite = row.Favorite ?? existing.IsFavorite;
                    // Existing watch state is user data; imports leave it untouched.
                }
                else
                {
                    var sourceType = row.Url.Length > 0 && row.LocalFilePath.Length > 0 ? "링크+파일" : row.Url.Length > 0 ? "링크" : "PC 파일";
                    var source = row.Url.Length > 0 ? row.Url : row.LocalFilePath;
                    _allCards.Add(new MediaCard(row.Title, row.Platform, sourceType,
                        row.Url.Length > 0 && Uri.TryCreate(row.Url, UriKind.Absolute, out var mediaUri) ? mediaUri.Host : Path.GetFileName(row.LocalFilePath),
                        row.LocalFilePath.Length > 0 ? File.Exists(row.LocalFilePath) ? "파일 확인됨" : "파일 확인 필요" : "링크 사용 가능",
                        row.WatchStatus, source, channel.ChannelUrl, row.PublishedAt, onlineUrl: row.Url, localFilePath: row.LocalFilePath,
                        vodId: row.VodId, lastPositionSeconds: row.LastPositionSeconds, memo: row.Memo,
                        updatedAt: DateTimeOffset.TryParse(row.UpdatedAt, out var importedUpdated) ? importedUpdated : DateTimeOffset.UtcNow,
                        isFavorite: row.Favorite ?? false));
                    added++;
                }
                SaveChannel(channel);
            }
            ChannelEmptyState.IsVisible = _channels.Count == 0;
            ChannelScroller.IsVisible = _channels.Count > 0;
            UpdateCards();
            await ShowTransferMessageAsync("가져오기 완료", $"{rows.Count}개 항목을 확인했고, 새로 추가한 항목은 {added}개입니다. 기존 항목의 시청 상태는 유지했습니다.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            await ShowTransferMessageAsync("가져오기 실패", exception.Message);
        }
    }

    private async Task ShowTransferMessageAsync(string title, string message)
    {
        var close = new Button { Content = "확인", MinWidth = 80, IsDefault = true };
        var dialog = new Window
        {
            Title = title, Width = 440, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(22), Spacing = 16, Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { close } }
            }}
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void OpenLicenseReference_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            await Launcher.LaunchUriAsync(uri);
    }

    private static LicenseEntry[] BuildLicenseEntries() =>
    [
        new("Avalonia UI", "MIT · Avalonia 12.0.2 and related packages", "UI framework and related platform, theme, font, and rendering packages.", "고지 포함", "https://github.com/AvaloniaUI/Avalonia", "Avalonia Team", "https://github.com/AvaloniaUI", "https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md"),
        new("kimcore/chzzk", "MIT · API terms are separate", "Referenced for the public channel profile endpoint and response field names. No upstream source code is included.", "참고 코드", "https://github.com/kimcore/chzzk", "kimcore", "https://github.com/kimcore", "https://github.com/kimcore/chzzk/blob/main/LICENSE"),
        new("reindeer002/soop", "MIT · API terms are separate", "Referenced for the public station endpoint and response fields. No upstream source code is included.", "참고 코드", "https://github.com/reindeer002/soop", "reindeer002", "https://github.com/reindeer002", "https://github.com/reindeer002/soop/blob/develop/LICENSE"),
        new("SOOP broadcast notifier userscript", "MIT per script metadata", "Referenced only for the API host fallback behavior; no script source is included.", "참고 코드", "https://greasyfork.org/scripts/588909/code", "Eldirna", "https://greasyfork.org/en/users?search=Eldirna", "https://greasyfork.org/scripts/588909/code"),
        new("ANGLE · Avalonia.Angle.Windows.Natives", "BSD 3-Clause style · package notice included", "Native ANGLE package 2.1.25547.20250602 includes its license notice.", "고지 포함", "https://github.com/google/angle", "Google and ANGLE contributors", "https://github.com/google", "https://github.com/google/angle/blob/main/LICENSE"),
        new("SkiaSharp and HarfBuzzSharp", "MIT · SkiaSharp 3.119.4-preview.1.1, HarfBuzzSharp 8.3.1.3", "Rendering and text shaping libraries; package metadata identifies Microsoft as publisher.", "고지 포함", "https://github.com/mono/SkiaSharp", "Microsoft", "https://github.com/microsoft", "https://github.com/mono/SkiaSharp/blob/main/LICENSE"),
        new("Microsoft.Extensions packages", "MIT · 8.0.0", "Dependency injection and logging abstractions from the .NET project.", "고지 포함", "https://github.com/dotnet/extensions", ".NET Foundation", "https://github.com/dotnet", "https://github.com/dotnet/extensions/blob/main/LICENSE.TXT"),
        new("Microsoft.IO.RecyclableMemoryStream", "MIT · 3.0.1", "Stream pooling library.", "고지 포함", "https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream", "Microsoft", "https://github.com/microsoft", "https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream/blob/main/LICENSE"),
        new("MicroCom.Runtime", "MIT · 0.11.4", "COM interop support library.", "고지 포함", "https://github.com/kekekeks/MicroCom", "kekekeks", "https://github.com/kekekeks", "https://github.com/kekekeks/MicroCom/blob/master/LICENSE"),
        new("Tmds.DBus.Protocol", "MIT · 0.92.0", "D-Bus protocol library.", "고지 포함", "https://github.com/tmds/Tmds.DBus", "Tom Deseyn", "https://github.com/tmds", "https://github.com/tmds/Tmds.DBus/blob/main/LICENSE")
    ];
    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateCards();
        if (_isLoadingSettings) return;
        _searchDurationRefreshTimer.Stop();
        _searchDurationRefreshTimer.Start();
    }

    private void SearchDurationRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _searchDurationRefreshTimer.Stop();
        RestartVisibleCardDurations();
    }

    private void StatusFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateCards();
        RefreshUnknownVisibleCardDurations();
    }

    private void SortOrder_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && SortOrder.SelectedIndex >= 0)
        {
            var value = SortOrder.SelectedIndex switch { 1 => "RecentAdded", 2 => "Title", _ => "PublishedAt" };
            _settingsStore.SaveSortOrder(value);
        }
        UpdateCards();
        ResetCardScrollPosition();
        if (!_isLoadingSettings) RestartVisibleCardDurations();
    }

    private void SortDirectionButton_Click(object? sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        _settingsStore.SaveSortDescending(_sortDescending);
        UpdateSortDirectionButton();
        UpdateCards();
        ResetCardScrollPosition();
        RestartVisibleCardDurations();
    }

    private void ResetCardScrollPosition()
    {
        if (CardsScroller is null || CardsListScroller is null) return;
        CardsScroller.Offset = new Vector(CardsScroller.Offset.X, 0);
        CardsListScroller.Offset = new Vector(CardsListScroller.Offset.X, 0);
    }

    private void RestartVisibleCardDurations()
    {
        RefreshUnknownVisibleCardDurations();
    }

    private void RefreshUnknownVisibleCardDurations()
    {
        var viewportCards = GetViewportVisibleCards();
        foreach (var card in viewportCards.Where(card => card.IsDurationPending &&
                     (card.ChannelUrl is null || !_durationRefreshInProgress.Contains(card.ChannelUrl))))
            _durationRefreshAttempted.Remove(card);
        var visibleChannels = viewportCards.Where(card => !card.HasKnownDuration && card.ChannelUrl is not null)
            .Select(card => card.ChannelUrl!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var request in _durationRefreshCancellation.Where(item => !visibleChannels.Contains(item.Key)).ToArray())
            request.Value.Cancel();

        var cards = viewportCards
            .Where(card => !card.HasKnownDuration && _durationRefreshAttempted.Add(card))
            .ToArray();
        foreach (var card in cards.Where(card => !string.IsNullOrWhiteSpace(card.ChannelUrl)))
            card.SetDurationDiagnostic("\uB300\uAE30 \uC911");
        foreach (var group in cards.Where(card => !string.IsNullOrWhiteSpace(card.ChannelUrl))
                     .GroupBy(card => card.ChannelUrl!, StringComparer.OrdinalIgnoreCase))
        {
            var channel = _channels.FirstOrDefault(item => item.ChannelUrl.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (channel is not null) _ = RefreshChannelDurationsAsync(channel, group.ToArray());
            else foreach (var card in group) _ = RefreshCardDurationAsync(card);
        }
        foreach (var card in cards.Where(card => string.IsNullOrWhiteSpace(card.ChannelUrl)))
        {
            if (card.HasLocalFilePath) _ = DetectLocalDurationAsync(card);
            else _ = RefreshCardDurationAsync(card);
        }
    }

    private MediaCard[] GetViewportVisibleCards()
    {
        var scroller = _isListView ? CardsListScroller : CardsScroller;
        if (scroller is null || scroller.Viewport.Width <= 0 || scroller.Viewport.Height <= 0) return [];
        var viewportOrigin = scroller.TranslatePoint(new Point(0, 0), CardsArea);
        if (viewportOrigin is not { } origin) return [];
        var viewport = new Rect(origin, scroller.Viewport);
        var expectedTag = _isListView ? "list" : "grid";
        return _cardVisuals
            .Where(border => border.Tag?.ToString() == expectedTag &&
                             border.DataContext is MediaCard card && _visibleCards.Contains(card))
            .Select(border => (Border: border, Card: (MediaCard)border.DataContext!))
            .Where(item => item.Border.TranslatePoint(new Point(0, 0), CardsArea) is { } position &&
                           new Rect(position, item.Border.Bounds.Size).Intersects(viewport))
            .Select(item => item.Card)
            .Distinct()
            .ToArray();
    }

    private void UpdateSortDirectionButton()
    {
        if (SortDirectionButton is null) return;
        SortDirectionButton.Content = _sortDescending ? "↓" : "↑";
        ToolTip.SetTip(SortDirectionButton, _sortDescending ? "내림차순" : "오름차순");
    }

    private string? GetSelectedPlatform() => _isAllLibraryView
        ? null
        : PlatformTabs.SelectedItem is TabItem tab
        ? tab.Tag?.ToString() switch
        {
            "chzzk" => "치지직",
            "soop" => "SOOP",
            _ => null
        }
        : null;

    private void UpdateCards()
    {
        if (CardsList is null) return;

        UpdateVisibleChannels();

        var selectedPlatform = GetSelectedPlatform();
        var query = SearchBox?.Text?.Trim();
        var normalizedDateQuery = query?.Replace('/', '-').Replace('.', '-');
        var digitDateQuery = query is null ? "" : new string(query.Where(char.IsDigit).ToArray());
        var statusFilter = StatusFilter?.SelectedIndex ?? 0;
        var matchingCards = _allCards.Where(card =>
            (_isShowAllChannels || _selectedChannelUrls.Contains(card.ChannelUrl ?? string.Empty)) &&
            (selectedPlatform is null || card.PlatformLabel == selectedPlatform) &&
            (string.IsNullOrWhiteSpace(query) ||
             card.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             card.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                 .Contains(normalizedDateQuery!, StringComparison.OrdinalIgnoreCase) == true ||
             (digitDateQuery.Length >= 4 && card.PublishedAt?.ToLocalTime().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                 .Contains(digitDateQuery, StringComparison.OrdinalIgnoreCase) == true)))
            .ToArray();

        ListTotalCountText.Text = $"총 {matchingCards.Length}개";
        UnwatchedCountText.Text = matchingCards.Count(card => card.WatchStatusLabel == "미시청").ToString();
        WatchingCountText.Text = matchingCards.Count(card => card.WatchStatusLabel == "시청중").ToString();
        CompletedCountText.Text = matchingCards.Count(card => card.WatchStatusLabel == "시청완료").ToString();

        IEnumerable<MediaCard> filtered = matchingCards.Where(card => statusFilter == 0 || card.WatchStatusLabel == (statusFilter switch
        {
            1 => "미시청",
            2 => "시청중",
            3 => "시청완료",
            _ => ""
        }));
        if (_showFavoritesOnly) filtered = filtered.Where(card => card.IsFavorite);
        filtered = SortOrder?.SelectedIndex switch
        {
            1 => _sortDescending
                ? filtered.OrderByDescending(card => _allCards.IndexOf(card))
                : filtered.OrderBy(card => _allCards.IndexOf(card)),
            2 => _sortDescending
                ? filtered.OrderByDescending(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => _sortDescending
                ? filtered.OrderByDescending(card => card.PublishedAt.HasValue)
                    .ThenByDescending(card => card.PublishedAt)
                    .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderByDescending(card => card.PublishedAt.HasValue)
                    .ThenBy(card => card.PublishedAt)
                    .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
        };
        _filteredCards.Clear();
        _filteredCards.AddRange(filtered);
        _visibleCards.Clear();
        foreach (var card in _filteredCards.Take(_cardDisplayLimit))
        {
            _visibleCards.Add(card);
        }
        var visibleCards = _filteredCards.ToHashSet();
        foreach (var card in _allCards.Where(card => card.IsSelected && !visibleCards.Contains(card)))
            card.IsSelected = false;
        if (_selectionAnchor is not null && !visibleCards.Contains(_selectionAnchor))
            _selectionAnchor = null;
        UpdateSelectionButtons();

        var hasCards = _visibleCards.Count > 0;
        foreach (var card in _visibleCards) card.ShowThumbnails = _showThumbnails;
        EmptyState.IsVisible = !hasCards;
        var hasChannelScope = _isShowAllChannels || _selectedChannelUrls.Count > 0;
        DeleteChannelsButton.IsVisible = !_isShowAllChannels && _selectedChannelUrls.Count > 0;
        EmptyStateTitle.Text = hasChannelScope ? "\uC544\uC9C1 \uB4F1\uB85D\uB41C \uC601\uC0C1\uC774 \uC5C6\uC2B5\uB2C8\uB2E4" : "\uCC44\uB110\uC744 \uCD94\uAC00\uD558\uAC70\uB098 \uC120\uD0DD\uD574\uC8FC\uC138\uC694";
        EmptyStateDescription.Text = !hasChannelScope
            ? "\uC67C\uCABD \uCC44\uB110 \uBAA9\uB85D\uC5D0\uC11C \uCC44\uB110\uC744 \uC120\uD0DD\uD558\uAC70\uB098 \uC0C8 \uCC44\uB110\uC744 \uCD94\uAC00\uD574 \uC8FC\uC138\uC694."
            : "\uC67C\uCABD \uCD94\uAC00 \uBC84\uD2BC\uC73C\uB85C \uB2E4\uC2DC\uBCF4\uAE30\uB97C \uB4F1\uB85D\uD558\uAC70\uB098 \uC0C8\uB85C\uACE0\uCE68\uC744 \uB20C\uB7EC \uBD88\uB7EC\uC624\uC138\uC694.";
        CardsScroller.IsVisible = hasCards && !_isListView;
        CardsListScroller.IsVisible = hasCards && _isListView;
        ChannelActionBar.IsVisible = hasChannelScope;
        var hasSingleChannelContext = _selectedChannel is not null && _selectedChannelUrls.Count == 1 && !_isShowAllChannels;
        ChannelEmptyActions.IsVisible = !hasCards && hasSingleChannelContext;
        EmptyStateChannelAddButton.IsVisible = !hasChannelScope;
        if (_selectedChannel is { } emptyChannel)
            EmptyStatePlatformRefreshButton.Content = emptyChannel.Platform == "SOOP" ? "SOOP에서 불러오기" : "치지직에서 불러오기";
        EmptyStatePlatformRefreshButton.IsEnabled = hasSingleChannelContext;
        EmptyStateAddButton.IsVisible = false;
        RefreshChannelButton.IsVisible = true;
        RefreshChannelButton.IsEnabled = hasSingleChannelContext;
        DirectAddButton.IsVisible = true;
        DirectAddButton.IsEnabled = hasSingleChannelContext;
        ThumbnailViewButton.IsVisible = hasChannelScope;
        FavoriteFilterButton.IsVisible = hasChannelScope;
        GridViewButton.IsVisible = hasChannelScope;
        ListViewButton.IsVisible = hasChannelScope;
        ShowAllChannelsCard.Classes.Set("selected", _isShowAllChannels);
    }

    private void UpdateVisibleChannels()
    {
        if (ChannelCards is null) return;

        var selectedPlatform = GetSelectedPlatform();
        _visibleChannels.Clear();
        foreach (var channel in _channels.Where(channel => selectedPlatform is null ||
                     (channel.Platform == "SOOP" ? "SOOP" : "치지직") == selectedPlatform))
            _visibleChannels.Add(channel);

        ChannelEmptyState.IsVisible = _visibleChannels.Count == 0;
        ChannelScroller.IsVisible = _visibleChannels.Count > 0;
    }

}

public sealed class MediaCard : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private static readonly string[] WatchStatuses = ["\uBBF8\uC2DC\uCCAD", "\uC2DC\uCCAD\uC911", "\uC2DC\uCCAD\uC644\uB8CC"];
    private static readonly string[] WatchStatusColors = ["#D84040", "#D99B18", "#2E9B57"];
    private bool _showThumbnails = true;
    private Bitmap? _thumbnail;
    private int _watchStatusIndex;
    private bool _isSelected;
    private string _title;
    private DateTimeOffset? _publishedAt;
    private string? _onlineUrl;
    private string? _localFilePath;
    private string _sourceTypeLabel;
    private string _sourceLabel;
    private string _availabilityLabel;
    private string _memo;
    private string _vodId;
    private double _lastPositionSeconds;
    private double? _durationSeconds;
    private string _durationDiagnostic = "";
    private bool _isPositionSliderInteracting;
    private bool _positionSliderHasPendingChanges;
    private bool _isPlaybackProgressHovered;
    private bool _isPlaybackProgressDragging;
    private bool _hasPendingPositionInput;
    private string _lastPositionHours = "00";
    private string _lastPositionMinutes = "00";
    private string _lastPositionRemainderSeconds = "00";
    private DateTimeOffset _updatedAt;
    private bool _isFavorite;
    private CancellationTokenSource? _thumbnailRequest;

    public MediaCard(string title, string platformLabel, string sourceTypeLabel, string sourceLabel,
        string availabilityLabel, string watchStatusLabel, string sourceValue, string? channelUrl = null,
        DateTimeOffset? publishedAt = null, string? thumbnailUrl = null, string? thumbnailPath = null,
        string? onlineUrl = null, string? localFilePath = null, string? vodId = null,
        double lastPositionSeconds = 0, string? memo = null, DateTimeOffset? updatedAt = null,
        double? durationSeconds = null, bool isFavorite = false)
    {
        _title = title;
        PlatformLabel = platformLabel;
        _sourceTypeLabel = sourceTypeLabel;
        _sourceLabel = sourceLabel;
        _availabilityLabel = availabilityLabel;
        _watchStatusIndex = Array.IndexOf(WatchStatuses, watchStatusLabel);
        if (_watchStatusIndex < 0) _watchStatusIndex = 0;
        ChannelUrl = channelUrl;
        _publishedAt = publishedAt;
        ThumbnailUrl = thumbnailUrl;
        ThumbnailPath = thumbnailPath;
        _onlineUrl = NullIfBlank(onlineUrl) ?? (sourceTypeLabel == "PC 파일" ? null : IsHttpUrl(sourceValue) ? sourceValue : null);
        _localFilePath = NullIfBlank(localFilePath) ?? (sourceTypeLabel == "PC 파일" ? NullIfBlank(sourceValue) : null);
        _vodId = string.IsNullOrWhiteSpace(vodId) ? LibraryTransfer.GetVodId(platformLabel, _onlineUrl ?? "", _localFilePath ?? "", title) : vodId;
        _lastPositionSeconds = Math.Max(0, lastPositionSeconds);
        SetLastPositionPartsFromSeconds(_lastPositionSeconds);
        _durationSeconds = durationSeconds > 0 ? durationSeconds : null;
        _memo = memo ?? "";
        _updatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        _isFavorite = isFavorite;
        RefreshSourceLabels();
    }

    public string Title => _title;
    public string PlatformLabel { get; }
    public string SourceTypeLabel => _sourceTypeLabel;
    public string SourceLabel => _sourceLabel;
    public string AvailabilityLabel => _availabilityLabel;
    public string SourceValue => _onlineUrl ?? _localFilePath ?? "";
    public string? OnlineUrl => _onlineUrl;
    public bool HasOnlineUrl => !string.IsNullOrWhiteSpace(_onlineUrl);
    public bool NeedsOnlineUrl => !HasOnlineUrl;
    public string? LocalFilePath => _localFilePath;
    public string VodId => _vodId;
    public double LastPositionSeconds => _lastPositionSeconds;
    public double? DurationSeconds => _durationSeconds;
    public bool HasKnownDuration => _durationSeconds is > 0;
    public bool IsDurationPending => string.Equals(_durationDiagnostic, "\uB300\uAE30 \uC911", StringComparison.Ordinal);
    public string DurationLabel => _durationSeconds is > 0
        ? FormatDuration(_durationSeconds.Value)
        : string.IsNullOrWhiteSpace(_durationDiagnostic) ? "\uC54C \uC218 \uC5C6\uC74C" : _durationDiagnostic;
    public string DurationTooltip => "\uC601\uC0C1 \uC2DC\uAC04";
    public double SliderMaximumSeconds => _durationSeconds is > 0 ? _durationSeconds.Value : 1;
    public double PlaybackPositionSliderValue
    {
        get => _lastPositionSeconds;
        set => SetLastPositionFromSlider(value);
    }
    public double PlaybackProgressRatio => _durationSeconds is > 0
        ? Math.Clamp(_lastPositionSeconds / _durationSeconds.Value, 0, 1)
        : 0;
    public double PlaybackProgressBarHeight => _isPlaybackProgressHovered || _isPlaybackProgressDragging ? 8 : 4;
    public double PlaybackProgressOpacity => IsLastPositionVisible ? 1 : 0;
    public double PlaybackProgressRowHeight => 20;
    public double OnlineSourceRowHeight => 18;
    public double LocalSourceRowHeight => 18;
    public IBrush PlaybackAccentBrush => new SolidColorBrush(Color.Parse(PlatformLabel == "SOOP" ? "#398FE0" : "#35B879"));
    public bool IsPositionSliderVisible => IsLastPositionVisible && _durationSeconds is > 0;
    public bool IsPositionSliderInteracting => _isPositionSliderInteracting;
    public string LastPositionHours { get => _lastPositionHours; set => SetLastPositionPart("hours", value); }
    public string LastPositionMinutes { get => _lastPositionMinutes; set => SetLastPositionPart("minutes", value); }
    public string LastPositionRemainderSeconds { get => _lastPositionRemainderSeconds; set => SetLastPositionPart("seconds", value); }
    public bool IsLastPositionVisible => WatchStatusLabel == "시청중";
    public string Memo => _memo;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            _updatedAt = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(FavoriteForeground));
            OnPropertyChanged(nameof(UpdatedAt));
        }
    }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public IBrush FavoriteForeground => new SolidColorBrush(Color.Parse(IsFavorite ? "#F4C542" : "#9699A6"));
    public string MemoOverlayText => string.IsNullOrWhiteSpace(_memo) ? "메모하기" : _memo;
    public IBrush MemoIconForeground => new SolidColorBrush(Color.Parse(string.IsNullOrWhiteSpace(_memo) ? "#85899B" : "#7156E8"));
    public void SetMemo(string memo)
    {
        var normalized = memo.Trim();
        if (string.Equals(_memo, normalized, StringComparison.Ordinal)) return;
        _memo = normalized;
        _updatedAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(Memo));
        OnPropertyChanged(nameof(MemoOverlayText));
        OnPropertyChanged(nameof(MemoIconForeground));
        OnPropertyChanged(nameof(UpdatedAt));
    }
    public DateTimeOffset UpdatedAt => _updatedAt;
    public string LocalFilePathLabel => _localFilePath ?? "";
    public bool HasLocalFilePath => !string.IsNullOrWhiteSpace(_localFilePath);
    public bool NeedsLocalFilePath => !HasLocalFilePath;
    public string? ChannelUrl { get; }
    public DateTimeOffset? PublishedAt => _publishedAt;
    public string? ThumbnailUrl { get; private set; }
    public string? ThumbnailPath { get; private set; }
    public bool ShowThumbnails
    {
        get => _showThumbnails;
        set
        {
            if (_showThumbnails == value) return;
            _showThumbnails = value;
            OnPropertyChanged(nameof(ShowThumbnails));
            OnPropertyChanged(nameof(ShowThumbnailPlaceholder));
            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(CardMinHeight));
        }
    }
    public Bitmap? Thumbnail => _thumbnail;
    public bool HasThumbnail => _thumbnail is not null;
    public bool ShowThumbnailPlaceholder => ShowThumbnails && _thumbnail is null;
    public double CardWidth => ShowThumbnails ? 286 : 246;
    public double CardMinHeight => ShowThumbnails ? 310 : 174;
    public string WatchStatusLabel => WatchStatuses[_watchStatusIndex];
    public IBrush WatchStatusBackground => new SolidColorBrush(Color.Parse(WatchStatusColors[_watchStatusIndex]));
    public IBrush WatchStatusForeground => Brushes.White;

    public void UpdateDetails(string title, DateTimeOffset? publishedAt, string? onlineUrl, string? localFilePath, string? vodId = null)
    {
        var normalizedOnlineUrl = NullIfBlank(onlineUrl);
        var normalizedLocalFilePath = NullIfBlank(localFilePath);
        var normalizedVodId = string.IsNullOrWhiteSpace(vodId)
            ? LibraryTransfer.GetVodId(PlatformLabel, normalizedOnlineUrl ?? "", normalizedLocalFilePath ?? "", title)
            : vodId;
        var detailsChanged = !string.Equals(_title, title, StringComparison.Ordinal) ||
                             _publishedAt != publishedAt ||
                             !string.Equals(_onlineUrl, normalizedOnlineUrl, StringComparison.Ordinal) ||
                             !string.Equals(_localFilePath, normalizedLocalFilePath, StringComparison.Ordinal) ||
                             !string.Equals(_vodId, normalizedVodId, StringComparison.Ordinal);
        if (!detailsChanged) return;

        var sourceChanged = !string.Equals(_onlineUrl, normalizedOnlineUrl, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_localFilePath, normalizedLocalFilePath, StringComparison.OrdinalIgnoreCase);
        _title = title;
        _publishedAt = publishedAt;
        _onlineUrl = normalizedOnlineUrl;
        _localFilePath = normalizedLocalFilePath;
        if (sourceChanged)
            SetDuration(null);
        _vodId = normalizedVodId;
        _updatedAt = DateTimeOffset.UtcNow;
        RefreshSourceLabels();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PublishedAt));
        OnPropertyChanged(nameof(PublishedAtLabel));
        OnPropertyChanged(nameof(OnlineUrl));
        OnPropertyChanged(nameof(HasOnlineUrl));
        OnPropertyChanged(nameof(NeedsOnlineUrl));
        OnPropertyChanged(nameof(OnlineSourceRowHeight));
        OnPropertyChanged(nameof(LocalFilePath));
        OnPropertyChanged(nameof(LocalFilePathLabel));
        OnPropertyChanged(nameof(HasLocalFilePath));
        OnPropertyChanged(nameof(NeedsLocalFilePath));
        OnPropertyChanged(nameof(LocalSourceRowHeight));
        OnPropertyChanged(nameof(SourceValue));
        OnPropertyChanged(nameof(SourceTypeLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(AvailabilityLabel));
    }

    private void RefreshSourceLabels()
    {
        _sourceTypeLabel = _onlineUrl is not null && _localFilePath is not null ? "링크+파일" :
            _onlineUrl is not null ? "링크" : "PC 파일";
        _sourceLabel = _onlineUrl is not null && Uri.TryCreate(_onlineUrl, UriKind.Absolute, out _)
            ? _onlineUrl
            : _localFilePath is not null ? Path.GetFileName(_localFilePath) : "링크 없음";
        _availabilityLabel = _localFilePath is not null
            ? File.Exists(_localFilePath) ? "파일 확인됨" : "파일 확인 필요"
            : "링크 사용 가능";
    }

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void CycleWatchStatus()
    {
        _watchStatusIndex = (_watchStatusIndex + 1) % WatchStatuses.Length;
        _updatedAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(WatchStatusLabel));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(WatchStatusBackground));
        OnPropertyChanged(nameof(IsLastPositionVisible));
        OnPropertyChanged(nameof(PlaybackProgressOpacity));
        OnPropertyChanged(nameof(PlaybackProgressRowHeight));
        OnPropertyChanged(nameof(IsPositionSliderVisible));
    }

    public void SetThumbnail(Bitmap bitmap)
    {
        _thumbnail?.Dispose();
        _thumbnail = bitmap;
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowThumbnailPlaceholder));
    }

    public void SetThumbnailPath(string path) => ThumbnailPath = path;

    public void UpdateThumbnailUrl(string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) || string.Equals(ThumbnailUrl, thumbnailUrl, StringComparison.OrdinalIgnoreCase)) return;
        ThumbnailUrl = thumbnailUrl;
        OnPropertyChanged(nameof(ThumbnailUrl));
    }

    public void SetThumbnailRequest(CancellationTokenSource cancellation)
    {
        _thumbnailRequest?.Cancel();
        _thumbnailRequest = cancellation;
    }

    public void ClearThumbnailRequest(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_thumbnailRequest, cancellation)) _thumbnailRequest = null;
    }

    public void CancelThumbnailRequest() => _thumbnailRequest?.Cancel();

    public void ToggleFavorite() => IsFavorite = !IsFavorite;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(CardBorderThickness));
        }
    }

    public IBrush CardBorderBrush => IsSelected
        ? new SolidColorBrush(Color.Parse(PlatformLabel == "SOOP" ? "#398FE0" : "#35B879"))
        : new SolidColorBrush(Color.Parse(MainWindow.UnselectedCardBorderColor));
    public Thickness CardBorderThickness => new(IsSelected ? 2 : 1);
    public void RefreshTheme() => OnPropertyChanged(nameof(CardBorderBrush));
    public string PublishedAtLabel => PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
    public void SetDuration(double? seconds)
    {
        if (seconds is not > 0 && !string.IsNullOrWhiteSpace(_durationDiagnostic))
            AppLogger.Warn("DurationStatus", $"VOD={VodId}; diagnostic cleared by SetDuration(null). Previous={_durationDiagnostic}");
        _durationSeconds = seconds > 0 ? seconds : null;
        _durationDiagnostic = "";
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(HasKnownDuration));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(SliderMaximumSeconds));
        OnPropertyChanged(nameof(PlaybackProgressRatio));
        OnPropertyChanged(nameof(IsPositionSliderVisible));
    }

    public void SetDurationDiagnostic(string diagnostic)
    {
        if (string.Equals(_durationDiagnostic, diagnostic, StringComparison.Ordinal)) return;
        AppLogger.Info("DurationStatus", $"VOD={VodId}; { _durationDiagnostic} -> {diagnostic}");
        _durationDiagnostic = diagnostic;
        OnPropertyChanged(nameof(DurationLabel));
    }

    public void SetPlaybackProgressHovered(bool isHovered)
    {
        SetPlaybackProgressPointerOver(isHovered);
    }

    public void SetPlaybackProgressPointerOver(bool isPointerOver)
    {
        if (_isPlaybackProgressHovered == isPointerOver) return;
        _isPlaybackProgressHovered = isPointerOver;
        OnPropertyChanged(nameof(PlaybackProgressBarHeight));
    }

    public void SetPlaybackProgressDragging(bool isDragging)
    {
        if (_isPlaybackProgressDragging == isDragging) return;
        _isPlaybackProgressDragging = isDragging;
        OnPropertyChanged(nameof(PlaybackProgressBarHeight));
    }

    public void SetLastPositionPart(string part, string value)
    {
        if (value.Length > 0 && value[0] == '-') value = "00";
        if (value.Length > 2 || value.Any(character => !char.IsAsciiDigit(character))) return;
        switch (part)
        {
            case "hours": _lastPositionHours = value; break;
            case "minutes": _lastPositionMinutes = value; break;
            case "seconds": _lastPositionRemainderSeconds = value; break;
            default: return;
        }
        if (!int.TryParse(_lastPositionHours, out var hours) || !int.TryParse(_lastPositionMinutes, out var minutes) ||
            !int.TryParse(_lastPositionRemainderSeconds, out var seconds) || minutes > 59 || seconds > 59) return;
        _lastPositionSeconds = hours * 3600d + minutes * 60d + seconds;
        _hasPendingPositionInput = true;
        _updatedAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(LastPositionSeconds));
        OnPropertyChanged(nameof(PlaybackPositionSliderValue));
        OnPropertyChanged(nameof(PlaybackProgressRatio));
    }

    public void BeginPositionSliderInteraction()
    {
        _isPositionSliderInteracting = true;
        _positionSliderHasPendingChanges = false;
    }

    public bool EndPositionSliderInteraction()
    {
        _isPositionSliderInteracting = false;
        var changed = _positionSliderHasPendingChanges;
        _positionSliderHasPendingChanges = false;
        return changed;
    }

    public bool CommitPositionInput()
    {
        var changed = _hasPendingPositionInput;
        _hasPendingPositionInput = false;
        return changed;
    }

    private void SetLastPositionFromSlider(double value)
    {
        var maximum = _durationSeconds is > 0 ? _durationSeconds.Value : double.MaxValue;
        var seconds = Math.Clamp(Math.Round(value), 0, maximum);
        if (Math.Abs(_lastPositionSeconds - seconds) < 0.5) return;
        _lastPositionSeconds = seconds;
        SetLastPositionPartsFromSeconds(seconds);
        _updatedAt = DateTimeOffset.UtcNow;
        if (_isPositionSliderInteracting) _positionSliderHasPendingChanges = true;
        OnPropertyChanged(nameof(LastPositionSeconds));
        OnPropertyChanged(nameof(PlaybackPositionSliderValue));
        OnPropertyChanged(nameof(PlaybackProgressRatio));
        OnPropertyChanged(nameof(LastPositionHours));
        OnPropertyChanged(nameof(LastPositionMinutes));
        OnPropertyChanged(nameof(LastPositionRemainderSeconds));
    }

    private static string FormatDuration(double seconds)
    {
        var total = Math.Max(0, (long)Math.Round(seconds));
        return TimeSpan.FromSeconds(total).ToString(@"hh\:mm\:ss");
    }

    private void SetLastPositionPartsFromSeconds(double value)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, value));
        _lastPositionHours = Math.Min(99, (int)time.TotalHours).ToString("00");
        _lastPositionMinutes = time.Minutes.ToString("00");
        _lastPositionRemainderSeconds = time.Seconds.ToString("00");
    }
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    public void Dispose()
    {
        CancelThumbnailRequest();
        _thumbnail?.Dispose();
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChannelEntry : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private bool _isLive;
    private bool _isSelected;
    private Bitmap? _profileImage;
    private string _displayName;
    private string _description;

    public ChannelEntry(string platform, string channelId, string displayName, string description, string channelUrl)
    {
        Platform = platform;
        ChannelId = channelId;
        _displayName = displayName;
        _description = description;
        ChannelUrl = channelUrl;
    }

    public string Platform { get; }
    public string ChannelId { get; }
    public IBrush PlatformBadgeBackground => new SolidColorBrush(Color.Parse(Platform == "SOOP" ? "#398FE0" : "#35B879"));
    public bool IsChzzk => Platform != "SOOP";
    public bool IsSoop => Platform == "SOOP";
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Initial)));
        }
    }
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value) return;
            _description = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Description)));
        }
    }
    public string ChannelUrl { get; }
    public string? ProfileImageUrl { get; set; }
    public string? ProfileImagePath { get; set; }
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName[..1];
    public Bitmap? ProfileImage => _profileImage;
    public bool HasProfileImage => _profileImage is not null;
    public IBrush AvatarBorderBrush => IsLive
        ? new SolidColorBrush(Color.Parse(Platform == "SOOP" ? "#55B8F2" : "#43CDB4"))
        : new SolidColorBrush(Color.Parse("#D8DBE4"));
    public IBrush CardBorderBrush => IsSelected
        ? new SolidColorBrush(Color.Parse(Platform == "SOOP" ? "#398FE0" : "#35B879"))
        : new SolidColorBrush(Color.Parse(MainWindow.UnselectedCardBorderColor));
    public Thickness CardBorderThickness => new(IsSelected ? 2 : 1);
    public void RefreshTheme() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardBorderBrush)));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardBorderBrush)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CardBorderThickness)));
        }
    }

    public bool IsLive
    {
        get => _isLive;
        set
        {
            if (_isLive == value) return;
            _isLive = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsLive)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AvatarBorderBrush)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void SetProfileImage(Bitmap image, string? imagePath = null)
    {
        var oldImage = _profileImage;
        _profileImage = image;
        if (imagePath is not null) ProfileImagePath = imagePath;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProfileImage)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasProfileImage)));
        oldImage?.Dispose();
    }

    public void Dispose() => _profileImage?.Dispose();
}
public sealed record LicenseEntry(string ProjectName, string LicenseLabel, string UsageNote, string StatusLabel, string ProjectUrl, string CreatorName, string CreatorUrl, string LicenseUrl)
{
    public string CreatorLinkLabel => $"제작자: {CreatorName}";
    public bool HasLicenseUrl => Uri.TryCreate(LicenseUrl, UriKind.Absolute, out _);
}
