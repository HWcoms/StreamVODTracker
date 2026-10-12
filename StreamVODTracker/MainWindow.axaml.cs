using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace StreamVODTracker;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<MediaCard> _allCards = [];
    private readonly ObservableCollection<MediaCard> _visibleCards = [];
    private readonly ObservableCollection<ChannelEntry> _channels = [];
    private ChannelEntry? _selectedChannel;
    private readonly DispatcherTimer _channelStatusTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private bool _isDarkTheme;

    public MainWindow()
    {
        InitializeComponent();
        CardsList.ItemsSource = _visibleCards;
        ChannelCards.ItemsSource = _channels;
        LicenseEntries.ItemsSource = BuildLicenseEntries();
        PlatformTabs.SelectedIndex = 0;
        ApplyTheme(false);
        _channelStatusTimer.Tick += ChannelStatusTimer_Tick;
        _channelStatusTimer.Start();
        Closed += (_, _) =>
        {
            _channelStatusTimer.Stop();
            foreach (var channel in _channels) channel.Dispose();
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
                var id = new Uri(channel.ChannelUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                var platformKey = channel.Platform == "SOOP" ? "SOOP" : "CHZZK";
                var profile = await LookupChannelProfileAsync(platformKey, Uri.UnescapeDataString(id));
                channel.IsLive = profile.IsLive;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // Keep the last known live state if the service is temporarily unavailable.
            }
        });
        await Task.WhenAll(refreshTasks);
    }

    private static async Task LoadChannelImageAsync(ChannelEntry channel, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return;
        var normalizedImageUrl = imageUrl.StartsWith("//", StringComparison.Ordinal) ? $"https:{imageUrl}" : imageUrl;
        if (!Uri.TryCreate(normalizedImageUrl, UriKind.Absolute, out var imageUri) ||
            (imageUri.Scheme != Uri.UriSchemeHttps && imageUri.Scheme != Uri.UriSchemeHttp))
            return;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var imageBytes = await client.GetByteArrayAsync(imageUri);
            using var imageStream = new MemoryStream(imageBytes);
            channel.SetProfileImage(new Bitmap(imageStream));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or ArgumentException)
        {
            // Keep the initials placeholder when the profile image cannot be loaded.
        }
    }

    private static async Task LoadChannelAppearanceAsync(ChannelEntry channel, string platformKey, string channelId,
        string? imageUrl, bool profileLookupSucceeded)
    {
        if (!profileLookupSucceeded)
        {
            try
            {
                var profile = await LookupChannelProfileAsync(platformKey, channelId);
                imageUrl = profile.ImageUrl;
                channel.IsLive = profile.IsLive;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
            await LoadChannelImageAsync(channel, imageUrl);
    }

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e) => ApplyTheme(!_isDarkTheme);

    private void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;
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
            AddMediaButton.IsVisible = true;
            SearchFiltersPanel.IsVisible = true;
            PageTitle.Text = "전체 보관함";
            PageDescription.Text = "치지직과 SOOP에 등록한 영상을 모아봅니다.";
            ListTitle.Text = "최근 영상";
            UpdateCards();
            return;
        }

        if (section == "licenses")
        {
            PlatformTabs.SelectedIndex = -1;
            PlatformTabs.IsVisible = false;
            LibraryPanel.IsVisible = false;
            LicensePanel.IsVisible = true;
            AddMediaButton.IsVisible = false;
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
        if (AddMediaButton is not null) AddMediaButton.IsVisible = true;
        if (SearchFiltersPanel is not null) SearchFiltersPanel.IsVisible = true;

        (PageTitle.Text, PageDescription.Text, ListTitle.Text) = section switch
        {
            "chzzk" => ("치지직", "치지직 채널과 영상을 관리하세요.", "치지직 영상"),
            "soop" => ("SOOP", "SOOP 채널과 영상을 관리하세요.", "SOOP 영상"),
            _ => ("전체 보관함", "치지직과 SOOP에 등록한 영상을 모아봅니다.", "최근 영상")
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

    private void MediaCard_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: MediaCard selectedCard }) return;
        foreach (var card in _allCards) card.IsSelected = ReferenceEquals(card, selectedCard);
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
                _allCards.Add(new MediaCard(video.Title, channel.Platform, "VOD", video.Url,
                    "링크 사용 가능", "안 봄", video.Url, channel.ChannelUrl, video.PublishedAt));
                addedCount++;
            }
            if (button is not null) button.Content = addedCount == 0 ? "새 영상 없음" : $"새 영상 {addedCount}개 추가";
            UpdateCards();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (button is not null) button.Content = "새로고침 실패";
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private static async Task<List<(string Title, string Url, DateTimeOffset? PublishedAt)>> FetchChannelVideosAsync(ChannelEntry channel)
    {
        if (!Uri.TryCreate(channel.ChannelUrl, UriKind.Absolute, out var channelUri) ||
            !TryGetPlatformChannelId(channel.Platform == "SOOP" ? "SOOP" : "CHZZK", channelUri, out var channelId))
            throw new InvalidOperationException("Channel ID could not be extracted.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        var videos = new List<(string Title, string Url, DateTimeOffset? PublishedAt)>();
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
                    videos.Add((title, $"https://vod.sooplive.com/player/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue)));
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
                videos.Add((title, $"https://chzzk.naver.com/video/{Uri.EscapeDataString(id)}", ParsePublishedDate(dateValue)));
            }
            if (data.GetArrayLength() < 30) break;
        }
        return videos;
    }

    private static DateTimeOffset? ParsePublishedDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var date) ? date : null;

    private async void DirectAddChannelVideo_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedChannel is { } channel) await AddChannelVideoAsync(channel);
    }

    private async Task AddChannelVideoAsync(ChannelEntry channel)
    {
        var titleInput = new TextBox { PlaceholderText = "영상 제목", MinHeight = 38 };
        var urlInput = new TextBox { PlaceholderText = "영상 링크 (파일을 고르면 비워 둬도 됩니다)", MinHeight = 38 };
        var dateInput = new TextBox { PlaceholderText = "방송 날짜 (선택, 예: 2026-10-13)", MinHeight = 38 };
        var fileLabel = new TextBlock { Text = "선택된 파일 없음", FontSize = 12, Foreground = Brushes.Gray, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        string? selectedPath = null;
        var error = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window
        {
            Title = "채널 영상 직접 추가", Width = 500, Height = 440, MinWidth = 500, MinHeight = 440,
            MaxWidth = 500, MaxHeight = 440, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "취소", MinWidth = 82, IsCancel = true };
        var save = new Button { Content = "추가", MinWidth = 82, IsDefault = true };
        var chooseFile = new Button { Content = "파일 선택" };
        chooseFile.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "영상 파일 선택", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("영상 파일") { Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.webm", "*.m4v"] }, FilePickerFileTypes.All]
            });
            if (files.Count == 0) return;
            selectedPath = files[0].Path.LocalPath;
            fileLabel.Text = files[0].Name;
            if (string.IsNullOrWhiteSpace(titleInput.Text)) titleInput.Text = Path.GetFileNameWithoutExtension(files[0].Name);
        };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            var title = titleInput.Text?.Trim();
            var address = urlInput.Text?.Trim();
            DateTimeOffset? publishedAt = null;
            if (!string.IsNullOrWhiteSpace(dateInput.Text))
            {
                if (!DateTimeOffset.TryParse(dateInput.Text, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var parsedDate))
                {
                    error.Text = "날짜를 YYYY-MM-DD 형식으로 입력해 주세요.";
                    return;
                }
                publishedAt = parsedDate;
            }
            if (string.IsNullOrWhiteSpace(title)) { error.Text = "영상 제목을 입력해 주세요."; return; }
            if (selectedPath is null && (!Uri.TryCreate(address, UriKind.Absolute, out var mediaUri) ||
                                         (mediaUri.Scheme != Uri.UriSchemeHttp && mediaUri.Scheme != Uri.UriSchemeHttps)))
            { error.Text = "HTTP/HTTPS 영상 링크를 입력하거나 파일을 선택해 주세요."; return; }

            var isFile = selectedPath is not null;
            var source = isFile ? selectedPath! : address!;
            var label = isFile ? Path.GetFileName(source) : new Uri(source).Host;
            _allCards.Add(new MediaCard(title!, channel.Platform, isFile ? "PC 파일" : "링크", label,
                isFile ? "파일 확인 필요" : "링크 사용 가능", "안 봄", source, channel.ChannelUrl, publishedAt));
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
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { chooseFile, fileLabel } },
                dateInput, error,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
            }
        };
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

            var channel = new ChannelEntry(platform, name, descriptionInput.Text?.Trim() ?? "", channelUri!.AbsoluteUri, isLive);
            _channels.Add(channel);
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
        var settings = new SettingsWindow();
        await settings.ShowDialog(this);
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
        _visibleCards.Clear();
        foreach (var card in _allCards.Where(card =>
                     (selectedPlatform is null || card.PlatformLabel == selectedPlatform) &&
                     (_selectedChannel is null || card.ChannelUrl == _selectedChannel.ChannelUrl) &&
                     (string.IsNullOrWhiteSpace(query) ||
                      card.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                      card.SourceLabel.Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            _visibleCards.Add(card);
        }

        var hasCards = _visibleCards.Count > 0;
        EmptyState.IsVisible = !hasCards;
        CardsScroller.IsVisible = hasCards;
        var channelEmpty = _selectedChannel is not null &&
                           !_allCards.Any(card => card.ChannelUrl == _selectedChannel.ChannelUrl);
        ChannelEmptyActions.IsVisible = channelEmpty;
        EmptyStateAddButton.IsVisible = !channelEmpty;
        RefreshChannelButton.IsVisible = _selectedChannel is not null;
        DirectAddButton.IsVisible = _selectedChannel is not null;
    }

}

public sealed record MediaCard(
    string Title,
    string PlatformLabel,
    string SourceTypeLabel,
    string SourceLabel,
    string AvailabilityLabel,
    string WatchStatusLabel,
    string SourceValue,
    string? ChannelUrl = null,
    DateTimeOffset? PublishedAt = null) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

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

    public IBrush CardBorderBrush => IsSelected
        ? new SolidColorBrush(Color.Parse(PlatformLabel == "SOOP" ? "#398FE0" : "#35B879"))
        : new SolidColorBrush(Color.Parse("#E5E6ED"));
    public Thickness CardBorderThickness => new(IsSelected ? 2 : 1);
    public string PublishedAtLabel => PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChannelEntry : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private bool _isLive;
    private bool _isSelected;
    private Bitmap? _profileImage;

    public ChannelEntry(string platform, string displayName, string description, string channelUrl, bool isLive)
    {
        Platform = platform;
        DisplayName = displayName;
        Description = description;
        ChannelUrl = channelUrl;
        _isLive = isLive;
    }

    public string Platform { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string ChannelUrl { get; }
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

    public void SetProfileImage(Bitmap image)
    {
        var oldImage = _profileImage;
        _profileImage = image;
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
