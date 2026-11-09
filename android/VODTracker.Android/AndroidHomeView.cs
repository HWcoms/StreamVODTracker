using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VODTracker.Core;
using Button = Avalonia.Controls.Button;

namespace VODTracker.Android;

internal sealed class AndroidHomeView : UserControl
{
    private static bool _isDarkTheme;
    private readonly AndroidSettingsStore _settings;
    private readonly AndroidLibraryStore _libraryStore = new();
    private readonly ContentControl _pageHost = new() { Background = Brush("#F5F6FA") };
    private readonly TextBlock _pageTitle = new();
    private readonly Grid _root = new();
    private Grid? _header;
    private Border? _headerBorder;
    private Grid? _drawerOverlay;
    private Button? _drawerThemeButton;
    private Button? _drawerSettingsButton;
    private Button? _headerThemeButton;
    private Button? _headerSettingsButton;
    private TextBlock? _libraryStatus;
    private Point? _swipeStart;
    private string _selectedSection = "all";
    private string? _selectedChannelKey;
    private bool _isSettingsActive;
    private readonly Dictionary<string, Button> _drawerItems = new(StringComparer.Ordinal);
    private readonly List<(string SearchText, Control View)> _channelSearchItems = [];
    private readonly Dictionary<string, DateTimeOffset> _profileRefreshStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _liveStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ChannelCardVisual>> _channelCardVisuals = new(StringComparer.OrdinalIgnoreCase);

    public AndroidHomeView(AndroidSettingsStore settings)
    {
        _settings = settings;
        _isDarkTheme = settings.LoadIsDarkTheme();
        ApplyThemeColors();
        _pageTitle.FontSize = 23;
        _pageTitle.FontWeight = FontWeight.Bold;
        _pageTitle.Foreground = Brush("#202230");

        var logo = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(12),
            Background = Brush("#7C5CFC"),
            Child = new TextBlock
            {
                Text = "V",
                Foreground = Brushes.White,
                FontSize = 21,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var brand = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = "VOD TRACKER", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeight.Bold },
                new TextBlock { Text = "PERSONAL LIBRARY", Foreground = Brush("#A8ABBA"), FontSize = 10 }
            }
        };
        var menuButton = new Button
        {
            Content = "☰",
            FontSize = 24,
            Width = 42,
            Height = 42,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        menuButton.Click += (_, _) => SetDrawerOpen(!(_drawerOverlay?.IsVisible ?? false));
        var headerThemeButton = new Button
        {
            Content = _isDarkTheme ? "☀" : "☾",
            FontSize = 21,
            Width = 42,
            Height = 42,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerThemeButton.Click += (_, _) => ToggleTheme();
        _headerThemeButton = headerThemeButton;
        var settingsButton = new Button
        {
            Content = "⚙",
            FontSize = 21,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        settingsButton.Click += (_, _) => ToggleSettings();
        _headerSettingsButton = settingsButton;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            Background = Brush("#171923"),
            MinHeight = 64,
            ColumnSpacing = 12
        };
        _header = header;
        header.Children.Add(menuButton);
        header.Children.Add(logo);
        header.Children.Add(brand);
        header.Children.Add(headerThemeButton);
        header.Children.Add(settingsButton);
        Grid.SetColumn(logo, 1);
        Grid.SetColumn(brand, 2);
        Grid.SetColumn(headerThemeButton, 3);
        Grid.SetColumn(settingsButton, 4);

        var titleBorder = new Border
        {
            Padding = new Thickness(20, 16, 20, 4),
            Child = _pageTitle
        };
        Grid.SetRow(titleBorder, 1);

        var headerBorder = new Border { Background = Brush("#171923"), Padding = new Thickness(18, 10), Child = header };
        _headerBorder = headerBorder;

        _root.RowDefinitions = new RowDefinitions("Auto,Auto,*");
        _root.Background = Brush("#F5F6FA");
        _root.Children.Add(headerBorder);
        _root.Children.Add(titleBorder);
        _root.Children.Add(_pageHost);
        _drawerOverlay = BuildDrawerOverlay();
        Grid.SetRowSpan(_drawerOverlay, 3);
        _drawerOverlay.ZIndex = 10;
        _root.Children.Add(_drawerOverlay);
        _root.PointerPressed += Root_PointerPressed;
        _root.PointerReleased += Root_PointerReleased;
        Content = _root;
        Grid.SetRow(_pageHost, 2);

        ShowLibrary();
    }

    private void ShowLibrary()
    {
        _isSettingsActive = false;
        UpdateDrawerSelection();
        var channels = _libraryStore.LoadAll();
        var mediaCount = channels.Sum(channel => channel.Media.Count);
        _libraryStatus = new TextBlock
        {
            Text = $"이 기기에 {channels.Count}개 채널, {mediaCount}개 VOD가 저장되어 있습니다.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#777B8B")
        };
        if (_selectedChannelKey is not null)
        {
            var selectedChannel = channels.FirstOrDefault(item => ChannelKey(item.Channel) == _selectedChannelKey);
            if (selectedChannel is not null)
            {
                _pageHost.Content = BuildPage(selectedChannel.Channel.Name, BuildChannelDetails(selectedChannel));
                return;
            }
            _selectedChannelKey = null;
        }

        _channelSearchItems.Clear();
        _channelCardVisuals.Clear();
        var sectionLabel = _selectedSection switch { "chzzk" => "치지직", "soop" => "SOOP", _ => "전체 보관함" };
        var content = new StackPanel { Spacing = 14 };

        if (channels.Count == 0)
        {
            content.Children.Add(_libraryStatus);
            content.Children.Add(Card(new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "등록된 채널이 없습니다", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A") },
                    new TextBlock { Text = "Windows 앱에서 내보낸 CSV를 가져오면 채널과 VOD 정보가 여기에 표시됩니다.", FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777B8B") },
                    MakeButton("CSV 가져오기", () => _ = ImportCsvAsync(), primary: true)
                }
            }));
        }
        else
        {
            var search = new TextBox
            {
                PlaceholderText = "채널 이름 또는 ID 검색",
                MinHeight = 42,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brush("#FFFFFF"),
                Foreground = Brush("#292B3A"),
                BorderBrush = Brush("#E5E6ED"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9)
            };
            search.TextChanged += (_, _) => ApplyChannelSearch(search.Text ?? string.Empty);
            content.Children.Add(search);
            content.Children.Add(_libraryStatus);

            var matchingChannels = channels
                .Where(item => _selectedSection == "all" || LibraryTransfer.NormalizePlatform(item.Channel.Platform) == (_selectedSection == "soop" ? "SOOP" : "CHZZK"))
                .OrderBy(item => LibraryTransfer.NormalizePlatform(item.Channel.Platform) == "SOOP" ? 1 : 0)
                .ThenBy(item => item.Channel.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (matchingChannels.Length == 0)
            {
                content.Children.Add(Card(new TextBlock
                {
                    Text = "이 플랫폼에 등록된 채널이 없습니다.",
                    FontSize = 14,
                    Foreground = Brush("#777B8B"),
                    TextWrapping = TextWrapping.Wrap
                }));
            }
            foreach (var channel in matchingChannels)
            {
                var view = BuildChannelListItem(channel);
                var searchText = $"{channel.Channel.Name} {channel.Channel.ChannelId} {channel.Channel.Description}";
                _channelSearchItems.Add((searchText, view));
                content.Children.Add(view);
            }
        }

        _pageHost.Content = BuildPage(sectionLabel, content);
    }

    private Control BuildChannelListItem(SavedChannelData data)
    {
        var platform = LibraryTransfer.NormalizePlatform(data.Channel.Platform);
        var platformName = platform == "SOOP" ? "SOOP" : "치지직";
        var key = ChannelKey(data.Channel);
        var nameText = new TextBlock { Text = data.Channel.Name, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A"), TextTrimming = TextTrimming.CharacterEllipsis };
        var descriptionText = new TextBlock { Text = string.IsNullOrWhiteSpace(data.Channel.Description) ? $"채널 ID: {data.Channel.ChannelId}" : data.Channel.Description, FontSize = 13, Foreground = Brush("#777B8B"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        var vodCountText = new TextBlock { Text = $"VOD {data.Media.Count}개  ·  {data.Channel.Url}", FontSize = 12, Foreground = Brush("#777B8B"), TextTrimming = TextTrimming.CharacterEllipsis };
        var initial = new TextBlock { Text = string.IsNullOrWhiteSpace(data.Channel.Name) ? "?" : data.Channel.Name[..1], Foreground = Brush("#684ED8"), FontSize = 19, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var avatarImage = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var isLive = _liveStatus.TryGetValue(key, out var live) && live;
        var avatarBorder = new Border
        {
            Width = 62,
            Height = 62,
            CornerRadius = new CornerRadius(31),
            BorderThickness = new Thickness(3),
            BorderBrush = GetLiveBorderBrush(platform, isLive),
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                CornerRadius = new CornerRadius(25),
                ClipToBounds = true,
                Background = Brush("#F0EDFF"),
                Child = new Grid { Children = { initial, avatarImage } }
            }
        };
        var cachedImagePath = _libraryStore.GetChannelImagePath(platform, data.Channel.ChannelId);
        if (!File.Exists(cachedImagePath) && !string.IsNullOrWhiteSpace(data.Channel.ProfileImagePath) && File.Exists(data.Channel.ProfileImagePath))
            cachedImagePath = data.Channel.ProfileImagePath;
        if (File.Exists(cachedImagePath))
        {
            try
            {
                avatarImage.Source = new Bitmap(cachedImagePath);
                avatarImage.IsVisible = true;
                initial.IsVisible = false;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
        }
        var visual = new ChannelCardVisual(avatarBorder, avatarImage, initial, nameText, descriptionText, vodCountText);
        if (!_channelCardVisuals.TryGetValue(key, out var visuals)) _channelCardVisuals[key] = visuals = [];
        visuals.Add(visual);
        var button = new Button
        {
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E8E9EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("70,*"),
                Children =
                {
                    avatarBorder,
                    new StackPanel
                    {
                        Spacing = 7,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                                Children =
                                {
                                    nameText,
                                    new Border
                                    {
                                        Background = Brush(platform == "SOOP" ? "#398FE0" : "#35B879"),
                                        CornerRadius = new CornerRadius(8),
                                        Padding = new Thickness(9, 4),
                                        Child = new TextBlock { Text = platformName, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
                                    }
                                }
                            },
                            descriptionText,
                            vodCountText
                        }
                    }
                }
            }
        };
        Grid.SetColumn(avatarBorder, 0);
        Grid.SetColumn((Control)((Grid)button.Content).Children[1], 1);
        button.Click += (_, _) =>
        {
            _selectedChannelKey = ChannelKey(data.Channel);
            ShowLibrary();
        };
        _ = RefreshChannelCardAsync(data, visual);
        return button;
    }

    private Control BuildChannelDetails(SavedChannelData data)
    {
        var platform = LibraryTransfer.NormalizePlatform(data.Channel.Platform);
        var platformName = platform == "SOOP" ? "SOOP" : "치지직";
        var accent = platform == "SOOP" ? "#398FE0" : "#35B879";
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(MakeButton("← 채널 목록", () => { _selectedChannelKey = null; ShowLibrary(); }));

        var avatarInitial = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(data.Channel.Name) ? "?" : data.Channel.Name[..1],
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = Brush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var avatarImage = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var avatarPath = _libraryStore.GetChannelImagePath(platform, data.Channel.ChannelId);
        if (File.Exists(avatarPath))
        {
            try
            {
                avatarImage.Source = new Bitmap(avatarPath);
                avatarImage.IsVisible = true;
                avatarInitial.IsVisible = false;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
        }
        var avatar = new Border
        {
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(34),
            BorderThickness = new Thickness(3),
            BorderBrush = Brush(accent),
            Padding = new Thickness(3),
            Child = new Border
            {
                CornerRadius = new CornerRadius(30),
                ClipToBounds = true,
                Background = Brush("#F0EDFF"),
                Child = new Grid { Children = { avatarInitial, avatarImage } }
            }
        };
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            Children =
            {
                avatar,
                new StackPanel
                {
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = platformName, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brush(accent) },
                        new TextBlock { Text = data.Channel.Name, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Brush("#292B3A"), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = string.IsNullOrWhiteSpace(data.Channel.Description) ? $"채널 ID: {data.Channel.ChannelId}" : data.Channel.Description, FontSize = 13, Foreground = Brush("#777B8B"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 }
                    }
                }
            }
        };
        Grid.SetColumn(heading.Children[1], 1);
        content.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                heading,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = $"저장된 VOD {data.Media.Count}개", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("#55596A"), VerticalAlignment = VerticalAlignment.Center },
                        MakeButton("채널 열기", () => _ = OpenAddressAsync(data.Channel.Url))
                    }
                }
            }
        }));
        var detailsCardContent = (StackPanel)((Border)content.Children[^1]).Child!;
        var channelActions = (Grid)detailsCardContent.Children[1];
        Grid.SetColumn(channelActions.Children[1], 1);

        var search = new TextBox
        {
            PlaceholderText = "VOD 제목 검색",
            MinHeight = 42,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        var statusFilter = new ComboBox
        {
            MinHeight = 40,
            SelectedIndex = 0,
            ItemsSource = new[] { "전체 상태", "미시청", "시청중", "시청완료" }
        };
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140"), ColumnSpacing = 8 };
        filters.Children.Add(search);
        filters.Children.Add(statusFilter);
        Grid.SetColumn(statusFilter, 1);
        content.Children.Add(filters);

        var mediaList = new StackPanel { Spacing = 12 };
        void RefreshMediaList()
        {
            mediaList.Children.Clear();
            var query = search.Text?.Trim() ?? string.Empty;
            var selectedStatus = statusFilter.SelectedItem as string ?? "전체 상태";
            var mediaItems = data.Media
                .Where(item => (query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
                    (selectedStatus == "전체 상태" || (string.IsNullOrWhiteSpace(item.WatchStatus) ? "미시청" : item.WatchStatus) == selectedStatus))
                .OrderByDescending(item => item.PublishedAt)
                .ToArray();
            if (mediaItems.Length == 0)
            {
                mediaList.Children.Add(Card(new TextBlock
                {
                    Text = data.Media.Count == 0 ? "이 채널에 저장된 VOD가 없습니다." : "검색 조건에 맞는 VOD가 없습니다.",
                    FontSize = 14,
                    Foreground = Brush("#777B8B"),
                    TextWrapping = TextWrapping.Wrap
                }));
                return;
            }

            foreach (var media in mediaItems)
                mediaList.Children.Add(BuildMediaCard(data.Channel, media, accent));
        }
        search.TextChanged += (_, _) => RefreshMediaList();
        statusFilter.SelectionChanged += (_, _) => RefreshMediaList();
        content.Children.Add(mediaList);
        RefreshMediaList();
        return content;
    }

    private Control BuildMediaCard(SavedChannel channel, SavedMedia media, string accent)
    {
        var status = string.IsNullOrWhiteSpace(media.WatchStatus) ? "미시청" : media.WatchStatus;
        var statusColor = status switch
        {
            "시청완료" => "#2E9B57",
            "시청중" => "#D99B18",
            _ => "#D84040"
        };
        var thumbnailImage = new Image { Stretch = Stretch.UniformToFill, IsVisible = false };
        var playIndicator = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(27),
            Background = Brush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "▶", FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var thumbnailContent = new Grid
        {
            Background = Brush("#202536"),
            Height = 176,
            Children =
            {
                thumbnailImage,
                playIndicator,
                new TextBlock { Text = "VOD", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
                new Border
                {
                    Background = Brush(statusColor),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 4),
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = status, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(media.ThumbnailUrl))
            _ = LoadMediaThumbnailAsync(channel.Platform, media.ThumbnailUrl, thumbnailImage, playIndicator);
        var published = media.PublishedAt?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? "날짜 정보 없음";
        var sourceLabel = string.IsNullOrWhiteSpace(media.OnlineUrl) ? "PC 파일" : "온라인 VOD";
        var cardContent = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock { Text = media.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A"), TextWrapping = TextWrapping.Wrap, MaxLines = 3 },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock { Text = published, FontSize = 12, Foreground = Brush("#777B8B"), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = sourceLabel, FontSize = 11, Foreground = Brush(accent), VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            }
        };
        Grid.SetColumn(cardContent.Children[0], 0);
        Grid.SetColumn(((Grid)cardContent.Children[1]).Children[1], 1);
        if (!string.IsNullOrWhiteSpace(media.Memo))
            cardContent.Children.Add(new TextBlock { Text = media.Memo, FontSize = 12, Foreground = Brush("#777B8B"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });

        var statusButton = MakeButton("상태 변경 · " + status, () =>
        {
            var nextStatus = status switch { "미시청" => "시청중", "시청중" => "시청완료", _ => "미시청" };
            _libraryStore.UpdateWatchStatus(channel.Platform, channel.ChannelId, media.VodId, nextStatus);
            ShowLibrary();
        });
        statusButton.PointerPressed += (_, e) => e.Handled = true;
        cardContent.Children.Add(statusButton);

        var card = Card(new StackPanel { Spacing = 0, Children = { thumbnailContent, new Border { Height = 1, Background = Brush("#E8E9EF") }, new Border { Padding = new Thickness(14), Child = cardContent } } });
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed || string.IsNullOrWhiteSpace(media.OnlineUrl)) return;
            e.Handled = true;
            _ = OpenAddressAsync(media.OnlineUrl);
        };
        return card;
    }

    private static Task OpenAddressAsync(string? address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView, global::Android.Net.Uri.Parse(uri.AbsoluteUri));
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        return Task.CompletedTask;
    }

    private async Task LoadMediaThumbnailAsync(string platform, string thumbnailUrl, Image image, Control placeholder)
    {
        if (!Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;

        var imagePath = _libraryStore.GetMediaThumbnailPath(thumbnailUrl);
        string? temporaryPath = null;
        try
        {
            if (!File.Exists(imagePath))
            {
                var bytes = await AndroidChannelProfileService.DownloadImageAsync(thumbnailUrl, LibraryTransfer.NormalizePlatform(platform));
                temporaryPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporaryPath, bytes);
                File.Move(temporaryPath, imagePath, overwrite: true);
                temporaryPath = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    image.Source = new Bitmap(imagePath);
                    image.IsVisible = true;
                    placeholder.IsVisible = false;
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException) { }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private void ApplyChannelSearch(string query)
    {
        foreach (var (searchText, view) in _channelSearchItems)
            view.IsVisible = query.Length == 0 || searchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string ChannelKey(SavedChannel channel) =>
        $"{LibraryTransfer.NormalizePlatform(channel.Platform)}:{channel.ChannelId}";

    private IBrush GetLiveBorderBrush(string platform, bool isLive) => Brush(!isLive
        ? "#D8DBE4"
        : LibraryTransfer.NormalizePlatform(platform) == "SOOP" ? "#55B8F2" : "#43CDB4");

    private async Task RefreshChannelCardAsync(SavedChannelData data, ChannelCardVisual visual)
    {
        var platform = LibraryTransfer.NormalizePlatform(data.Channel.Platform);
        var key = ChannelKey(data.Channel);
        if (_profileRefreshStarted.TryGetValue(key, out var lastStarted) && DateTimeOffset.UtcNow - lastStarted < TimeSpan.FromMinutes(2)) return;
        _profileRefreshStarted[key] = DateTimeOffset.UtcNow;

        AndroidChannelProfile? profile = null;
        try { profile = await AndroidChannelProfileService.LookupAsync(platform, data.Channel.ChannelId); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException) { }

        var name = profile?.Name ?? data.Channel.Name;
        var description = profile?.Description ?? data.Channel.Description;
        var imageUrl = profile?.ImageUrl ?? data.Channel.ProfileImageUrl;
        var imagePath = _libraryStore.GetChannelImagePath(platform, data.Channel.ChannelId);

        if (!File.Exists(imagePath))
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(imageUrl)) candidates.Add(imageUrl);
            if (platform == "SOOP")
            {
                var fallback = AndroidChannelProfileService.BuildSoopFallbackImageUrl(data.Channel.ChannelId);
                if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase)) candidates.Add(fallback);
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    var bytes = await AndroidChannelProfileService.DownloadImageAsync(candidate, platform);
                    var temporaryPath = imagePath + ".tmp";
                    await File.WriteAllBytesAsync(temporaryPath, bytes);
                    File.Move(temporaryPath, imagePath, overwrite: true);
                    imageUrl = candidate;
                    break;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        }

        try { _libraryStore.UpdateChannelProfile(platform, data.Channel.ChannelId, name, description, imageUrl, File.Exists(imagePath) ? imagePath : null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        if (profile is not null) _liveStatus[key] = profile.IsLive;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_channelCardVisuals.TryGetValue(key, out var visuals)) return;
            foreach (var card in visuals)
            {
                card.Name.Text = name;
                card.Description.Text = string.IsNullOrWhiteSpace(description) ? $"채널 ID: {data.Channel.ChannelId}" : description;
                card.AvatarBorder.BorderBrush = GetLiveBorderBrush(platform, _liveStatus.TryGetValue(key, out var live) && live);
                if (File.Exists(imagePath) && !card.AvatarImage.IsVisible)
                {
                    try
                    {
                        card.AvatarImage.Source = new Bitmap(imagePath);
                        card.AvatarImage.IsVisible = true;
                        card.Initial.IsVisible = false;
                    }
                    catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException) { }
                }
            }
        });
    }

    private sealed record ChannelCardVisual(Border AvatarBorder, Image AvatarImage, TextBlock Initial,
        TextBlock Name, TextBlock Description, TextBlock VodCount);

    private void ShowSettings()
    {
        _isSettingsActive = true;
        UpdateDrawerSelection();
        var clientIdInput = new TextBox
        {
            Text = _settings.LoadGoogleOAuthClientId(),
            PlaceholderText = "Android OAuth Client ID",
            MinHeight = 42,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        var status = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(clientIdInput.Text) ? "Client ID가 설정되지 않았습니다." : "Client ID가 이 기기에 저장되어 있습니다.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#777B8B")
        };
        var saveClientId = MakeButton("Client ID 저장", () =>
        {
            _settings.SaveGoogleOAuthClientId(clientIdInput.Text ?? string.Empty);
            status.Text = string.IsNullOrWhiteSpace(_settings.LoadGoogleOAuthClientId())
                ? "Client ID가 비워졌습니다."
                : "Android OAuth Client ID를 저장했습니다.";
        }, primary: true);

        _pageHost.Content = BuildPage("설정", new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Card(new StackPanel
                {
                    Spacing = 11,
                    Children =
                    {
                        new TextBlock { Text = "화면 테마", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A") },
                        new TextBlock { Text = "앱 화면의 배경과 글자 색을 함께 변경합니다.", FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777B8B") },
                        MakeButton(_isDarkTheme ? "라이트 모드로 전환" : "다크 모드로 전환", ToggleTheme)
                    }
                }),
                Card(new StackPanel
                {
                    Spacing = 11,
                    Children =
                    {
                        new TextBlock { Text = "Google Drive 동기화", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A") },
                        new TextBlock
                        {
                            Text = "Windows 앱과 같은 Google Drive 파일을 사용합니다. Android 전용 OAuth Client ID를 입력하세요. Client Secret은 Android 앱에 저장하지 않습니다.",
                            FontSize = 14,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brush("#777B8B")
                        },
                        new TextBlock { Text = "Android OAuth Client ID", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("#55596A") },
                        clientIdInput,
                        saveClientId,
                        status,
                        new Separator(),
                        new TextBlock { Text = "계정 연결 및 Drive 동기화는 다음 구현 단계에서 연결됩니다.", FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777B8B") }
                    }
                })
            }
        });
    }

    private Control BuildPage(string title, Control content)
    {
        _pageTitle.Text = title;
        return new ScrollViewer
        {
            Content = content,
            Background = Brush("#F5F6FA"),
            Padding = new Thickness(0, 8, 0, 20),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private Grid BuildDrawerOverlay()
    {
        var scrim = new Border { Background = new SolidColorBrush(Color.Parse("#99000000")) };
        scrim.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SetDrawerOpen(false);
        };

        var drawerContent = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new Border
                {
                    Margin = new Thickness(0, 0, 0, 22),
                    Child = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 12,
                        Children =
                        {
                            new Border
                            {
                                Width = 38, Height = 38, CornerRadius = new CornerRadius(12),
                                Background = Brush("#7C5CFC"),
                                Child = new TextBlock { Text = "V", Foreground = Brushes.White, FontSize = 21, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = "VOD TRACKER", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeight.Bold },
                                    new TextBlock { Text = "PERSONAL LIBRARY", Foreground = Brush("#A8ABBA"), FontSize = 10 }
                                }
                            }
                        }
                    }
                },
                new TextBlock { Text = "내 라이브러리", Foreground = Brush("#85899B"), FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0, 0, 7) },
                DrawerItem("⌂", "전체 보관함", "all"),
                DrawerItem("◉", "치지직", "chzzk"),
                DrawerItem("◉", "SOOP", "soop"),
                new Border { Height = 1, Background = Brush("#303342"), Margin = new Thickness(0, 14, 0, 8) },
                DrawerAction("↓", "CSV 가져오기", () => _ = ImportCsvAsync()),
                DrawerAction("↑", "CSV 내보내기", () => _ = ExportCsvAsync())
            }
        };
        var themeButton = new Button
        {
            Content = _isDarkTheme ? "☀" : "☾",
            Width = 48,
            Height = 48,
            FontSize = 23,
            Background = Brush("#2A2D3B"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#3A3D4B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        themeButton.Click += (_, _) => ToggleTheme();
        _drawerThemeButton = themeButton;
        var settingsButton = DrawerAction("⚙", "설정", ToggleSettings);
        settingsButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _drawerSettingsButton = settingsButton;
        var drawerFooter = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { themeButton, settingsButton }
        };
        var drawerLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children = { drawerContent, drawerFooter }
        };
        Grid.SetRow(drawerFooter, 1);
        var drawer = new Border
        {
            Width = 285,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush("#171923"),
            Padding = new Thickness(20, 24),
            Child = drawerLayout
        };
        var overlay = new Grid
        {
            IsVisible = false,
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { scrim, drawer }
        };
        Grid.SetColumnSpan(scrim, 2);
        Grid.SetColumn(drawer, 0);
        drawer.ZIndex = 1;
        return overlay;
    }

    private Button DrawerItem(string icon, string label, string section)
    {
        var button = DrawerAction(icon, label, () =>
        {
            _selectedSection = section;
            _selectedChannelKey = null;
            ShowLibrary();
            SetDrawerOpen(false);
        });
        button.Tag = section;
        _drawerItems[section] = button;
        return button;
    }

    private static Button DrawerAction(string icon, string label, Action action)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 44,
            Padding = new Thickness(12, 8),
            Background = Brush("#2A2D3B"),
            Foreground = Brush("#FFFFFF"),
            BorderBrush = Brush("#3A3D4B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = icon, Width = 20, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brush("#FFFFFF") },
                    new TextBlock { Text = label, Foreground = Brush("#FFFFFF") }
                }
            }
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetDrawerOpen(bool isOpen)
    {
        if (_drawerOverlay is not null) _drawerOverlay.IsVisible = isOpen;
        UpdateDrawerSelection();
    }

    private void ToggleSettings()
    {
        if (_isSettingsActive) ShowLibrary();
        else ShowSettings();
        SetDrawerOpen(false);
    }

    private async Task ImportCsvAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "CSV 라이브러리 가져오기",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("CSV 파일") { Patterns = ["*.csv"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            var importedRows = LibraryTransfer.ImportCsv(stream);
            ValidateImportedRows(importedRows);
            var added = _libraryStore.Import(importedRows);
            _selectedSection = "all";
            _selectedChannelKey = null;
            ShowLibrary();
            if (_libraryStatus is not null)
                _libraryStatus.Text = $"CSV 가져오기 완료: {added}개 VOD를 추가했습니다. 기존 VOD의 시청 상태와 메모는 유지했습니다.";
            SetDrawerOpen(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ShowLibraryWithStatus($"CSV 가져오기 실패: {exception.Message}");
        }
    }

    private async Task ExportCsvAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "CSV 라이브러리 내보내기",
                SuggestedFileName = "vod-library.csv",
                FileTypeChoices = [new FilePickerFileType("CSV 파일") { Patterns = ["*.csv"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) { stream.Position = 0; stream.SetLength(0); }
            _libraryStore.Export(stream);
            ShowLibraryWithStatus("CSV 내보내기를 완료했습니다.");
            SetDrawerOpen(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ShowLibraryWithStatus($"CSV 내보내기 실패: {exception.Message}");
        }
    }

    private static void ValidateImportedRows(IReadOnlyList<TransferMedia> rows)
    {
        foreach (var group in rows.GroupBy(row => $"{row.Platform}:{row.ChannelId}", StringComparer.OrdinalIgnoreCase))
            if (group.Select(row => row.ChannelUrl).Where(url => url.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException($"{group.First().Platform}/{group.First().ChannelId}에 서로 다른 채널 주소가 포함되어 있습니다.");
        foreach (var group in rows.GroupBy(row => $"{row.Platform}:{row.VodId}", StringComparer.OrdinalIgnoreCase))
            if (group.Select(row => row.ChannelId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException($"VOD ID {group.First().VodId}가 서로 다른 채널에 중복 지정되어 있습니다.");
    }

    private void ShowLibraryWithStatus(string message)
    {
        _selectedSection = "all";
        ShowLibrary();
        if (_libraryStatus is not null) _libraryStatus.Text = message;
    }

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        _settings.SaveIsDarkTheme(_isDarkTheme);
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = _isDarkTheme ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        ApplyThemeColors();
        if (_isSettingsActive) ShowSettings();
        else ShowLibrary();
    }

    private void UpdateDrawerSelection()
    {
        foreach (var (section, item) in _drawerItems)
            item.Background = Brush(section == _selectedSection ? "#49358C" : "#2A2D3B");
        if (_drawerSettingsButton is not null)
            _drawerSettingsButton.Background = Brush(_isSettingsActive ? "#49358C" : "#2A2D3B");
        if (_headerSettingsButton is not null)
            _headerSettingsButton.Background = _isSettingsActive ? Brush("#49358C") : Brushes.Transparent;
        if (_drawerThemeButton is not null)
        {
            _drawerThemeButton.Content = _isDarkTheme ? "☀" : "☾";
            _drawerThemeButton.Background = Brush("#2A2D3B");
            _drawerThemeButton.Foreground = Brushes.White;
        }
        if (_headerThemeButton is not null)
            _headerThemeButton.Content = _isDarkTheme ? "☀" : "☾";
    }

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _swipeStart = e.GetPosition(_root);
    }

    private void Root_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_swipeStart is not { } start) return;
        _swipeStart = null;
        var end = e.GetPosition(_root);
        var deltaX = end.X - start.X;
        if (deltaX > 80 && start.X < 36) SetDrawerOpen(true);
        else if (deltaX < -80 && (_drawerOverlay?.IsVisible ?? false)) SetDrawerOpen(false);
    }

    private void ApplyThemeColors()
    {
        _root.Background = Brush("#F5F6FA");
        _pageHost.Background = Brush("#F5F6FA");
        _pageTitle.Foreground = Brush("#202230");
        if (_header is not null) _header.Background = Brush("#171923");
        if (_headerBorder is not null) _headerBorder.Background = Brush("#171923");
        UpdateDrawerSelection();
    }

    private static Border Card(Control child) => new()
    {
        Background = Brush("#FFFFFF"),
        BorderBrush = Brush("#E8E9EF"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(16),
        Child = child
    };

    private static Button MakeButton(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 40,
            Padding = new Thickness(16, 8),
            Background = primary ? Brush("#7C5CFC") : Brush("#F7F7FA"),
            Foreground = primary ? Brushes.White : Brush("#292B3A"),
            BorderBrush = primary ? Brush("#7C5CFC") : Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static SolidColorBrush Brush(string color)
    {
        if (!_isDarkTheme)
            return new SolidColorBrush(Color.Parse(color));

        var darkColor = color.ToUpperInvariant() switch
        {
            "#F5F6FA" => "#12141B",
            "#FFFFFF" => "#242731",
            "#F7F7FA" => "#2A2D38",
            "#E8E9EF" or "#E5E6ED" => "#353946",
            "#202230" or "#292B3A" => "#F1F2F7",
            "#55596A" or "#777B8B" => "#A8ABBA",
            "#171923" => "#0D0F15",
            "#2A2D3B" => "#292C36",
            "#3A3D4B" => "#454955",
            _ => color
        };
        return new SolidColorBrush(Color.Parse(darkColor));
    }
}
