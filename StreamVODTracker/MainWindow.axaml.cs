using System.Collections.ObjectModel;
using System.Diagnostics;
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

namespace StreamVODTracker;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MediaCard> _allCards = [];
    private readonly ObservableCollection<MediaCard> _visibleCards = [];
    private readonly ObservableCollection<ChannelEntry> _channels = [];
    private readonly AppSettingsStore _settingsStore = new(AppContext.BaseDirectory);
    private readonly List<Border> _cardVisuals = [];
    private readonly HashSet<MediaCard> _dragSelectionBase = [];
    private readonly LocalCsvDatabase _localDatabase;
    private readonly IVodRepository _vodRepository;
    private static readonly SemaphoreSlim ThumbnailLoadLimiter = new(4);
    private ChannelEntry? _selectedChannel;
    private bool _isListView;
    private bool _showThumbnails = true;
    private readonly DispatcherTimer _channelStatusTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private bool _isDarkTheme;
    private bool _isLoadingSettings = true;
    private bool _sortDescending = true;

    public MainWindow()
    {
        InitializeComponent();
        _sortDescending = _settingsStore.LoadSortDescending();
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
        ChannelCards.ItemsSource = _channels;
        LicenseEntries.ItemsSource = BuildLicenseEntries();
        LoadLocalDatabase();
        PlatformTabs.SelectedIndex = 0;
        ApplyTheme(_settingsStore.LoadIsDarkTheme());
        _channelStatusTimer.Tick += ChannelStatusTimer_Tick;
        _channelStatusTimer.Start();
        Closed += (_, _) =>
        {
            _channelStatusTimer.Stop();
            foreach (var channel in _channels) channel.Dispose();
            foreach (var card in _allCards) card.Dispose();
        };
        UpdateCards();
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
        foreach (var saved in _vodRepository.LoadAll())
        {
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
                    item.VodId, item.LastPositionSeconds, item.Memo, item.UpdatedAt);
                _allCards.Add(card);
                if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    try { card.SetThumbnail(new Bitmap(item.ThumbnailPath)); }
                    catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
                }
                else if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
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

    private void SaveChannel(ChannelEntry channel) =>
        _vodRepository.Save(channel, _allCards.Where(card => card.ChannelUrl == channel.ChannelUrl));

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
            _selectedChannel = null;
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
            return;
        }

        if (section == "licenses")
        {
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

        PlatformTabs.SelectedIndex = section == "soop" ? 1 : 0;
    }
    private void PlatformTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageTitle is null || PlatformTabs.SelectedItem is not TabItem tab) return;

        var section = tab.Tag?.ToString();
        if (_selectedChannel is not null &&
            ((section == "soop") != (_selectedChannel.Platform == "SOOP")))
        {
            _selectedChannel.IsSelected = false;
            _selectedChannel = null;
        }
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
        var urlInput = new TextBox { PlaceholderText = "https://...", MinHeight = 38 };
        var validation = new TextBlock { Foreground = Avalonia.Media.Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cancelButton = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var dialog = new Window
        {
            Title = $"{platform} 링크 추가",
            Width = 460,
            Height = 300,
            MinWidth = 460,
            MinHeight = 300,
            MaxWidth = 460,
            MaxHeight = 300,
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

            _allCards.Add(new MediaCard(title, platform, "링크", uri.Host, "링크 사용 가능", "안 봄", uri.AbsoluteUri));
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
        UpdateCards();
    }

    private void ChannelCard_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ChannelEntry selectedChannel }) return;
        _selectedChannel = selectedChannel;
        foreach (var channel in _channels) channel.IsSelected = ReferenceEquals(channel, selectedChannel);
        PlatformTabs.SelectedIndex = selectedChannel.Platform == "SOOP" ? 1 : 0;
        UpdateCards();
    }

    private async void ChannelAvatar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Image { DataContext: ChannelEntry channel } ||
            !Uri.TryCreate(channel.ChannelUrl, UriKind.Absolute, out var uri)) return;
        e.Handled = true;
        _selectedChannel = channel;
        foreach (var item in _channels) item.IsSelected = ReferenceEquals(item, channel);
        PlatformTabs.SelectedIndex = channel.Platform == "SOOP" ? 1 : 0;
        UpdateCards();
        await Launcher.LaunchUriAsync(uri);
    }

    private MediaCard? _selectionAnchor;
    private bool _isLassoSelecting;
    private Point _lassoStart;

    private void MediaCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: MediaCard selectedCard }) return;
        // Let interactive controls inside the card receive their normal click events.
        // Marking the pointer press handled here prevents Button.Click from firing.
        if (IsInsideButton(e.Source)) return;

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
            IsInsideScrollBar(e.Source) || IsInsideButton(e.Source) || e.Source is TextBlock { Cursor: not null }) return;

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

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isLassoSelecting || CardsArea.IsPointerOver) return;
        if (DeleteSelectedButton.IsPointerOver || EditSelectedButton.IsPointerOver) return;
        ClearCardSelection();
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isLassoSelecting && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            UpdateLasso(e.GetPosition(CardsArea));
    }

    private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isLassoSelecting) return;
        UpdateLasso(e.GetPosition(CardsArea));
        _isLassoSelecting = false;
        SelectionRectangle.IsVisible = false;
        e.Pointer.Capture(null);
        UpdateSelectionButtons();
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
        var selectedCount = _allCards.Count(card => card.IsSelected && _visibleCards.Contains(card));
        DeleteSelectedButton.IsVisible = selectedCount > 0;
        EditSelectedButton.IsVisible = selectedCount > 0;
        EditSelectedButton.IsEnabled = selectedCount == 1;
    }

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

    private async void EditSelected_Click(object? sender, RoutedEventArgs e)
    {
        var card = _allCards.SingleOrDefault(item => item.IsSelected && _visibleCards.Contains(item));
        if (card is null) return;

        var titleInput = new TextBox { Text = card.Title, PlaceholderText = "영상 제목", MinHeight = 38 };
        var urlInput = new TextBox { Text = card.OnlineUrl ?? "", PlaceholderText = "온라인 영상 URL (선택)", MinHeight = 38 };
        var filePathInput = new TextBox { Text = card.LocalFilePath ?? "", PlaceholderText = "파일 링크/경로 (선택)", MinHeight = 38 };
        var dateInput = new TextBox
        {
            Text = card.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            PlaceholderText = "방송 날짜 (선택, YYYY-MM-DD)",
            MinHeight = 38
        };
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "영상 수정",
            Width = 520,
            Height = 390,
            MinWidth = 520,
            MinHeight = 390,
            MaxWidth = 520,
            MaxHeight = 390,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var chooseFile = new Button { Content = "추가", MinWidth = 66 };
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
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(item => item.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
            UpdateCards();
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
                dateInput,
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
        ToolTip.SetTip(GridViewButton, "현재 그리드 보기");
        ToolTip.SetTip(ListViewButton, "리스트 보기");
        UpdateCards();
    }

    private void ListView_Click(object? sender, RoutedEventArgs e)
    {
        _isListView = true;
        ToolTip.SetTip(GridViewButton, "그리드 보기");
        ToolTip.SetTip(ListViewButton, "현재 리스트 보기");
        UpdateCards();
    }

    private void ThumbnailView_Click(object? sender, RoutedEventArgs e)
    {
        _showThumbnails = !_showThumbnails;
        foreach (var card in _allCards) card.ShowThumbnails = _showThumbnails;
        ThumbnailViewButton.IsEnabled = true;
        ToolTip.SetTip(ThumbnailViewButton, _showThumbnails ? "썸네일 숨기기" : "썸네일 표시");
    }

    private void WatchStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaCard card })
        {
            card.CycleWatchStatus();
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } channel)
                SaveChannel(channel);
        }
    }

    private async void MediaSource_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: MediaCard card }) return;
        e.Handled = true;
        foreach (var item in _allCards) item.IsSelected = ReferenceEquals(item, card);
        if (IsHttpUrl(card.OnlineUrl) && Uri.TryCreate(card.OnlineUrl, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
            return;
        }

        OpenLocalFile(card.LocalFilePath);
    }

    private void MediaFile_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { DataContext: MediaCard card }) return;
        e.Handled = true;
        OpenLocalFile(card.LocalFilePath);
    }

    private static void OpenLocalFile(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private async Task LoadThumbnailAsync(MediaCard card, string? thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl)) return;
        var normalizedUrl = thumbnailUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{thumbnailUrl}" : thumbnailUrl;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;

        await ThumbnailLoadLimiter.WaitAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } ownerChannel)
                card.SetThumbnailPath(_localDatabase.SaveThumbnail(ownerChannel.Platform, ownerChannel.ChannelId,
                    card.Title, card.PublishedAt, normalizedUrl, bytes, response.Content.Headers.ContentType?.MediaType));
            using var stream = new MemoryStream(bytes);
            card.SetThumbnail(new Bitmap(stream));
            if (card.ChannelUrl is not null && _channels.FirstOrDefault(channel => channel.ChannelUrl == card.ChannelUrl) is { } owner)
                SaveChannel(owner);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or ArgumentException)
        {
            // Leave the thumbnail placeholder visible when an image cannot be loaded.
        }
        finally
        {
            ThumbnailLoadLimiter.Release();
        }
    }

    private async void RefreshChannelVideos_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedChannel is null) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var channel = _selectedChannel;
            var foundVideos = await FetchChannelVideosAsync(channel);
            var addedCount = 0;
            foreach (var video in foundVideos)
            {
                if (_allCards.Any(card => string.Equals(card.SourceValue.TrimEnd('/'), video.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) continue;
                var card = new MediaCard(video.Title, channel.Platform, "VOD", video.Url,
                    "", "미시청", video.Url, channel.ChannelUrl, video.PublishedAt, video.ThumbnailUrl);
                _allCards.Add(card);
                _ = LoadThumbnailAsync(card, video.ThumbnailUrl);
                addedCount++;
            }
            SaveChannel(channel);
            if (button is not null) ToolTip.SetTip(button, addedCount == 0 ? "새 영상이 없습니다" : $"새 영상 {addedCount}개 추가");
            UpdateCards();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (button is not null) ToolTip.SetTip(button, "새로고침 실패");
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private static async Task<List<(string Title, string Url, DateTimeOffset? PublishedAt, string? ThumbnailUrl)>> FetchChannelVideosAsync(ChannelEntry channel)
    {
        if (!Uri.TryCreate(channel.ChannelUrl, UriKind.Absolute, out var channelUri) ||
            !TryGetPlatformChannelId(channel.Platform == "SOOP" ? "SOOP" : "CHZZK", channelUri, out var channelId))
            throw new InvalidOperationException("Channel ID could not be extracted.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        var videos = new List<(string Title, string Url, DateTimeOffset? PublishedAt, string? ThumbnailUrl)>();
        if (channel.Platform == "SOOP")
        {
            var url = $"https://chapi.sooplive.com/api/{Uri.EscapeDataString(channelId)}/vods/all/streamer?page=1&per_page=60&orderby=reg_date";
            using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(url));
            if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("title_no", out var titleNo) ? titleNo.ToString() : null;
                    var title = item.TryGetProperty("title_name", out var titleName) ? titleName.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                    var dateValue = item.TryGetProperty("reg_date", out var regDate) ? regDate.ToString() : null;
                    var thumbnailUrl = GetFirstString(item, "thumb", "thumbnail", "thumb_url", "thumbnail_url", "title_img", "title_thumbnail");
                    videos.Add((title, $"https://vod.sooplive.com/player/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue), thumbnailUrl));
                }
            }
            return videos;
        }

        for (var page = 0; page < 5; page++)
        {
            var url = $"https://api.chzzk.naver.com/service/v1/channels/{Uri.EscapeDataString(channelId)}/videos?sortType=LATEST&pagingType=PAGE&page={page}&size=30";
            using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync(url));
            if (!json.RootElement.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array || data.GetArrayLength() == 0) break;
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("videoNo", out var videoNo) ? videoNo.ToString() : null;
                var title = item.TryGetProperty("videoTitle", out var videoTitle) ? videoTitle.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
                var dateValue = item.TryGetProperty("publishDate", out var publishDate) ? publishDate.GetString() : null;
                var thumbnailUrl = item.TryGetProperty("thumbnailImageUrl", out var thumbnail) ? thumbnail.GetString() : null;
                videos.Add((title, $"https://chzzk.naver.com/video/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue), thumbnailUrl));
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
        var calendar = new Calendar { SelectedDate = DateTime.Today };
        var todayButton = new Button
        {
            Content = "오늘",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 64,
            Padding = new Thickness(12, 6),
            Foreground = new SolidColorBrush(Color.Parse("#7C5CFC"))
        };
        var calendarPopup = new Popup
        {
            PlacementTarget = calendarButton,
            Placement = PlacementMode.Bottom,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.LightGray,
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
            Title = "채널 영상 직접 추가", Width = 500, Height = 440, MinWidth = 500, MinHeight = 440,
            MaxWidth = 500, MaxHeight = 440, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var save = new Button { Content = "추가", MinWidth = 82, IsDefault = true };
        var chooseFile = new Button { Content = "추가" };
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
                "안 봄", primarySource, channel.ChannelUrl, publishedAt, onlineUrl: address, localFilePath: filePath));
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
        lookupButton.Click += async (_, _) =>
        {
            var platformKey = platformPicker.SelectedIndex == 1 ? "SOOP" : "CHZZK";
            if (!TryCreateChannelUrl(platformKey, channelIdInput.Text, out var lookupUri, out var error))
            {
                message.Text = error;
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
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
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                message.Text = exception is TaskCanceledException
                    ? $"{platformKey} 요청 시간이 초과되었습니다. 잠시 후 다시 시도해 주세요."
                    : $"{platformKey} 채널 정보를 가져오지 못했습니다. ID 또는 네트워크 연결을 확인해 주세요.";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
            }
            finally
            {
                lookupButton.IsEnabled = true;
            }
        };

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
            var name = nameInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                message.Text = "채널명을 입력하거나 채널 정보 불러오기를 눌러 주세요.";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
            }
            if (_channels.Any(channel => channel.Platform == platform && channel.ChannelUrl == channelUri!.AbsoluteUri))
            {
                message.Text = "이미 등록된 채널 ID입니다.";
                message.Foreground = Avalonia.Media.Brushes.IndianRed;
                return;
            }

            var channel = new ChannelEntry(platform, channelId!, name, descriptionInput.Text?.Trim() ?? "", channelUri!.AbsoluteUri);
            channel.IsLive = isLive;
            _channels.Add(channel);
            SaveChannel(channel);
            ChannelEmptyState.IsVisible = false;
            ChannelScroller.IsVisible = true;
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
                    if (!root.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Object)
                        throw new InvalidOperationException("Channel content missing.");
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
        var settings = new SettingsWindow(_isDarkTheme, ApplyTheme);
        await settings.ShowDialog(this);
    }

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e) => await ExportLibraryAsync("csv");

    private async void ExportXlsx_Click(object? sender, RoutedEventArgs e) => await ExportLibraryAsync("xlsx");

    private async Task ExportLibraryAsync(string format)
    {
        var isExcel = format == "xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = isExcel ? "Excel 라이브러리 내보내기" : "CSV 라이브러리 내보내기",
            SuggestedFileName = $"vod-library.{format}",
            FileTypeChoices = [new FilePickerFileType(isExcel ? "Excel 통합 문서" : "CSV 파일") { Patterns = [$"*.{format}"] }]
        });
        if (file is null) return;
        try
        {
            var saved = _localDatabase.LoadAll();
            if (isExcel) LibraryTransfer.ExportXlsx(file.Path.LocalPath, saved);
            else LibraryTransfer.ExportCsv(file.Path.LocalPath, saved);
            await ShowTransferMessageAsync("내보내기 완료", "라이브러리를 파일로 내보냈습니다.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowTransferMessageAsync("내보내기 실패", exception.Message);
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
                        updatedAt: DateTimeOffset.TryParse(row.UpdatedAt, out var importedUpdated) ? importedUpdated : DateTimeOffset.UtcNow));
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
    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateCards();

    private void StatusFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateCards();

    private void SortOrder_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && SortOrder.SelectedIndex >= 0)
        {
            var value = SortOrder.SelectedIndex switch { 1 => "RecentAdded", 2 => "Title", _ => "PublishedAt" };
            _settingsStore.SaveSortOrder(value);
        }
        UpdateCards();
    }

    private void SortDirectionButton_Click(object? sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        _settingsStore.SaveSortDescending(_sortDescending);
        UpdateSortDirectionButton();
        UpdateCards();
    }

    private void UpdateSortDirectionButton()
    {
        if (SortDirectionButton is null) return;
        SortDirectionButton.Content = _sortDescending ? "↓" : "↑";
        ToolTip.SetTip(SortDirectionButton, _sortDescending ? "내림차순" : "오름차순");
    }

    private string? GetSelectedPlatform() => PlatformTabs.SelectedItem is TabItem tab
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

        var selectedPlatform = GetSelectedPlatform();
        var query = SearchBox?.Text?.Trim();
        var normalizedDateQuery = query?.Replace('/', '-').Replace('.', '-');
        var digitDateQuery = query is null ? "" : new string(query.Where(char.IsDigit).ToArray());
        var statusFilter = StatusFilter?.SelectedIndex ?? 0;
        IEnumerable<MediaCard> filtered = _allCards.Where(card =>
            _selectedChannel is not null &&
            (selectedPlatform is null || card.PlatformLabel == selectedPlatform) &&
            card.ChannelUrl == _selectedChannel.ChannelUrl &&
            (statusFilter == 0 || card.WatchStatusLabel == (statusFilter switch
            {
                1 => "미시청",
                2 => "시청중",
                3 => "시청완료",
                _ => ""
            })) &&
            (string.IsNullOrWhiteSpace(query) ||
             card.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             card.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                 .Contains(normalizedDateQuery!, StringComparison.OrdinalIgnoreCase) == true ||
             (digitDateQuery.Length >= 4 && card.PublishedAt?.ToLocalTime().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
                 .Contains(digitDateQuery, StringComparison.OrdinalIgnoreCase) == true)));
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
        _visibleCards.Clear();
        foreach (var card in filtered)
        {
            _visibleCards.Add(card);
        }
        var visibleCards = _visibleCards.ToHashSet();
        foreach (var card in _allCards.Where(card => card.IsSelected && !visibleCards.Contains(card)))
            card.IsSelected = false;
        if (_selectionAnchor is not null && !visibleCards.Contains(_selectionAnchor))
            _selectionAnchor = null;
        UpdateSelectionButtons();

        var hasCards = _visibleCards.Count > 0;
        foreach (var card in _visibleCards) card.ShowThumbnails = _showThumbnails;
        EmptyState.IsVisible = !hasCards;
        EmptyStateTitle.Text = _selectedChannel is null ? "채널을 추가하거나 선택해주세요" : "아직 등록된 영상이 없습니다";
        EmptyStateDescription.Text = _selectedChannel is null
            ? "왼쪽 채널 목록에서 채널을 선택하거나 새 채널을 추가해 주세요."
            : "위쪽 추가 버튼으로 다시보기를 등록하거나 새로고침을 눌러 불러오세요.";
        CardsScroller.IsVisible = hasCards && !_isListView;
        CardsListScroller.IsVisible = hasCards && _isListView;
        ChannelActionBar.IsVisible = _selectedChannel is not null;
        ChannelEmptyActions.IsVisible = false;
        EmptyStateAddButton.IsVisible = false;
        RefreshChannelButton.IsVisible = _selectedChannel is not null;
        DirectAddButton.IsVisible = _selectedChannel is not null;
        ThumbnailViewButton.IsVisible = _selectedChannel is not null;
        GridViewButton.IsVisible = _selectedChannel is not null;
        ListViewButton.IsVisible = _selectedChannel is not null;
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
    private DateTimeOffset _updatedAt;

    public MediaCard(string title, string platformLabel, string sourceTypeLabel, string sourceLabel,
        string availabilityLabel, string watchStatusLabel, string sourceValue, string? channelUrl = null,
        DateTimeOffset? publishedAt = null, string? thumbnailUrl = null, string? thumbnailPath = null,
        string? onlineUrl = null, string? localFilePath = null, string? vodId = null,
        double lastPositionSeconds = 0, string? memo = null, DateTimeOffset? updatedAt = null)
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
        _memo = memo ?? "";
        _updatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        if (_onlineUrl is not null && _localFilePath is not null)
            RefreshSourceLabels();
    }

    public string Title => _title;
    public string PlatformLabel { get; }
    public string SourceTypeLabel => _sourceTypeLabel;
    public string SourceLabel => _sourceLabel;
    public string AvailabilityLabel => _availabilityLabel;
    public string SourceValue => _onlineUrl ?? _localFilePath ?? "";
    public string? OnlineUrl => _onlineUrl;
    public string? LocalFilePath => _localFilePath;
    public string VodId => _vodId;
    public double LastPositionSeconds => _lastPositionSeconds;
    public string Memo => _memo;
    public DateTimeOffset UpdatedAt => _updatedAt;
    public string LocalFilePathLabel => _localFilePath ?? "";
    public bool HasLocalFilePath => !string.IsNullOrWhiteSpace(_localFilePath);
    public string? ChannelUrl { get; }
    public DateTimeOffset? PublishedAt => _publishedAt;
    public string? ThumbnailUrl { get; }
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
        _title = title;
        _publishedAt = publishedAt;
        _onlineUrl = NullIfBlank(onlineUrl);
        _localFilePath = NullIfBlank(localFilePath);
        _vodId = string.IsNullOrWhiteSpace(vodId) ? LibraryTransfer.GetVodId(PlatformLabel, _onlineUrl ?? "", _localFilePath ?? "", title) : vodId;
        _updatedAt = DateTimeOffset.UtcNow;
        RefreshSourceLabels();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PublishedAt));
        OnPropertyChanged(nameof(PublishedAtLabel));
        OnPropertyChanged(nameof(OnlineUrl));
        OnPropertyChanged(nameof(LocalFilePath));
        OnPropertyChanged(nameof(LocalFilePathLabel));
        OnPropertyChanged(nameof(HasLocalFilePath));
        OnPropertyChanged(nameof(SourceValue));
        OnPropertyChanged(nameof(SourceTypeLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(AvailabilityLabel));
    }

    private void RefreshSourceLabels()
    {
        _sourceTypeLabel = _onlineUrl is not null && _localFilePath is not null ? "링크+파일" :
            _onlineUrl is not null ? "링크" : "PC 파일";
        _sourceLabel = _onlineUrl is not null && Uri.TryCreate(_onlineUrl, UriKind.Absolute, out var uri)
            ? uri.Host
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
        OnPropertyChanged(nameof(WatchStatusLabel));
        OnPropertyChanged(nameof(WatchStatusBackground));
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
        : new SolidColorBrush(Color.Parse("#E5E6ED"));
    public Thickness CardBorderThickness => new(IsSelected ? 2 : 1);
    public string PublishedAtLabel => PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    public void Dispose() => _thumbnail?.Dispose();
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
        : new SolidColorBrush(Color.Parse("#E5E6ED"));
    public Thickness CardBorderThickness => new(IsSelected ? 2 : 1);

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
