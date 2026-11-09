using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VODTracker.Core;
using Button = Avalonia.Controls.Button;
using ProgressBar = Avalonia.Controls.ProgressBar;

namespace VODTracker.Android;

internal sealed class AndroidHomeView : UserControl
{
    private static bool _isDarkTheme;
    private readonly AndroidSettingsStore _settings;
    private readonly AndroidLibraryStore _libraryStore = new();
    private readonly ContentControl _pageHost = new() { Background = Brush("#F5F6FA") };
    private readonly TextBlock _pageTitle = new();
    private Button? _channelBackButton;
    private readonly Grid _root = new();
    private Grid? _header;
    private Border? _headerBorder;
    private Grid? _drawerOverlay;
    private Button? _drawerThemeButton;
    private Button? _drawerSettingsButton;
    private Button? _headerThemeButton;
    private Button? _headerSettingsButton;
    private TextBlock? _libraryStatus;
    private ScrollViewer? _activePageScrollViewer;
    private bool _thumbnailRefreshScheduled;
    private Point? _swipeStart;
    private MediaCardTapCandidate? _mediaCardTapCandidate;
    private ScrollViewer? _pullRefreshScrollViewer;
    private Point? _pullRefreshStart;
    private IPointer? _pullRefreshPointer;
    private Border? _pullRefreshIndicator;
    private Grid? _pullRefreshIcon;
    private Ellipse? _pullRefreshSpinnerDot;
    private double _pullRefreshDistance;
    private bool _isRefreshingChannelVods;
    private int _channelVodsRefreshGeneration;
    private Func<bool, Task>? _refreshCurrentChannelVods;
    private readonly DispatcherTimer _pullRefreshSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(65) };
    private double _pullRefreshSpinnerAngle;
    private string _selectedSection = "all";
    private string? _selectedChannelKey;
    private bool _isSettingsActive;
    private bool _showThumbnails = true;
    private bool _showFavoritesOnly;
    private readonly Dictionary<string, Button> _drawerItems = new(StringComparer.Ordinal);
    private readonly List<(string SearchText, Control View)> _channelSearchItems = [];
    private readonly Dictionary<string, DateTimeOffset> _profileRefreshStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _liveStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ChannelCardVisual>> _channelCardVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MediaCardVisual> _mediaCardVisuals = [];
    private Action? _loadMoreMediaCards;

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
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        menuButton.Click += (_, _) => SetDrawerOpen(!(_drawerOverlay?.IsVisible ?? false));
        var headerThemeButton = new Button
        {
            Content = _isDarkTheme ? "☀" : "☾",
            FontSize = 21,
            Width = 42,
            Height = 42,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        headerThemeButton.Click += (_, _) => ToggleTheme();
        _headerThemeButton = headerThemeButton;
        var settingsButton = new Button
        {
            Content = "⚙",
            FontSize = 21,
            Width = 42,
            Height = 42,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
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

        var channelBackButton = new Button
        {
            Content = "\u2190",
            Width = 42,
            Height = 42,
            Padding = new Thickness(0),
            FontSize = 26,
            Background = Brushes.Transparent,
            Foreground = Brush("#202230"),
            BorderThickness = new Thickness(0),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        channelBackButton.Click += (_, _) =>
        {
            _selectedChannelKey = null;
            ShowLibrary();
        };
        _channelBackButton = channelBackButton;
        var pageTitleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Children = { channelBackButton, _pageTitle }
        };
        Grid.SetColumn(_pageTitle, 1);
        var titleBorder = new Border
        {
            Padding = new Thickness(20, 16, 20, 4),
            Child = pageTitleRow
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
        _pullRefreshSpinnerTimer.Tick += (_, _) =>
        {
            _pullRefreshSpinnerAngle = (_pullRefreshSpinnerAngle + 25) % 360;
            if (_pullRefreshIcon?.RenderTransform is RotateTransform rotation)
                rotation.Angle = _pullRefreshSpinnerAngle;
        };
        _root.PointerPressed += Root_PointerPressed;
        _root.AddHandler(InputElement.PointerMovedEvent, Root_PointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _root.AddHandler(InputElement.PointerReleasedEvent, Root_PointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Content = _root;
        Grid.SetRow(_pageHost, 2);

        ShowLibrary();
    }

    private void ShowLibrary()
    {
        _loadMoreMediaCards = null;
        ReleaseMediaThumbnailVisuals();
        _refreshCurrentChannelVods = null;
        _pullRefreshScrollViewer = null;
        _pullRefreshStart = null;
        _pullRefreshPointer = null;
        _pullRefreshIndicator = null;
        _pullRefreshIcon = null;
        _pullRefreshDistance = 0;
        _pullRefreshSpinnerTimer.Stop();
        _pullRefreshSpinnerAngle = 0;
        _isRefreshingChannelVods = false;
        _channelVodsRefreshGeneration++;
        if (_channelBackButton is not null) _channelBackButton.IsVisible = false;
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
                _pageTitle.Text = selectedChannel.Channel.Name;
                if (_channelBackButton is not null) _channelBackButton.IsVisible = true;
                _pageHost.Content = BuildChannelDetails(selectedChannel);
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
        var vodCountText = new TextBlock { Text = $"VOD {data.Media.Count}\uAC1C", FontSize = 12, Foreground = Brush("#777B8B"), TextTrimming = TextTrimming.CharacterEllipsis };
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
        var platformBadge = new Border
        {
            Background = Brush(platform == "SOOP" ? "#398FE0" : "#35B879"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 4),
            Child = new TextBlock { Text = platformName, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
        };
        Grid.SetColumn(platformBadge, 1);
        var visual = new ChannelCardVisual(avatarBorder, avatarImage, initial, nameText, descriptionText, vodCountText, platformBadge);
        if (!_channelCardVisuals.TryGetValue(key, out var visuals)) _channelCardVisuals[key] = visuals = [];
        visuals.Add(visual);
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E8E9EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
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
                                    platformBadge
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
        var pullRefreshDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brush("#777B8B"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0)
        };
        var pullRefreshIcon = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new RotateTransform(),
            Children =
            {
                new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Stroke = Brush("#D9DCE5"),
                    StrokeThickness = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                pullRefreshDot
            }
        };
        var pullRefreshIndicator = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = new TranslateTransform(0, -23),
            Child = pullRefreshIcon
        };
        _pullRefreshIndicator = pullRefreshIndicator;
        _pullRefreshIcon = pullRefreshIcon;
        _pullRefreshSpinnerDot = pullRefreshDot;

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
            BorderBrush = GetLiveBorderBrush(platform, _liveStatus.TryGetValue(ChannelKey(data.Channel), out var live) && live),
            Padding = new Thickness(3),
            Child = new Border
            {
                CornerRadius = new CornerRadius(30),
                ClipToBounds = true,
                Background = Brush("#F0EDFF"),
                Child = new Grid { Children = { avatarInitial, avatarImage } }
            }
        };
        var detailName = new TextBlock { Text = data.Channel.Name, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = Brush("#292B3A"), TextWrapping = TextWrapping.Wrap };
        var detailDescription = new TextBlock { Text = "\uCC44\uB110 \uC815\uBCF4\uB97C \uBD88\uB7EC\uC624\uB294 \uC911...", FontSize = 13, Foreground = Brush("#777B8B"), TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        var detailVodCount = new TextBlock { Text = $"VOD {data.Media.Count}\uAC1C", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("#55596A"), VerticalAlignment = VerticalAlignment.Center };
        var platformBadge = new Border
        {
            Background = Brush(platform == "SOOP" ? "#398FE0" : "#35B879"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 4),
            Child = new TextBlock { Text = platformName, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White }
        };
        Grid.SetColumn(platformBadge, 1);
        var heading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            Children =
            {
                avatar,
                new StackPanel
                {
                    Spacing = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { detailName, platformBadge } },
                        detailDescription
                    }
                }
            }
        };
        Grid.SetColumn(heading.Children[1], 1);
        _ = RefreshSelectedChannelStatusAsync(data.Channel, avatar, detailName, detailDescription, detailVodCount, data.Media.Count);
        content.Children.Add(Card(new StackPanel
        {
            Spacing = 12,
            Children = { heading, detailVodCount }
        }));

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
            ItemsSource = new[] { "\uC804\uCCB4 \uC0C1\uD0DC", "\uBBF8\uC2DC\uCCAD", "\uC2DC\uCCAD \uC911", "\uC2DC\uCCAD \uC644\uB8CC" }
        };
        var sortOrder = new ComboBox
        {
            MinHeight = 40,
            SelectedIndex = 0,
            ItemsSource = new[] { "\uCD5C\uC2E0\uC21C", "\uC624\uB798\uB41C\uC21C", "\uC81C\uBAA9\uC21C" }
        };
        var mediaList = new StackPanel { Spacing = 12 };
        var mediaCardBatchSize = _settings.LoadCardBatchSize();
        Button favoriteFilter = null!;
        Button thumbnailToggle = null!;
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,44,44"), ColumnSpacing = 6 };
        statusFilter.MinWidth = 0;
        statusFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        statusFilter.HorizontalContentAlignment = HorizontalAlignment.Left;
        statusFilter.FontSize = 12;
        sortOrder.MinWidth = 0;
        sortOrder.HorizontalAlignment = HorizontalAlignment.Stretch;
        sortOrder.HorizontalContentAlignment = HorizontalAlignment.Left;
        sortOrder.FontSize = 12;
        filters.Children.Add(statusFilter);
        filters.Children.Add(sortOrder);
        Grid.SetColumn(sortOrder, 1);
        favoriteFilter = MakeButton(_showFavoritesOnly ? "\u2605" : "\u2606", () =>
        {
            _showFavoritesOnly = !_showFavoritesOnly;
            favoriteFilter.Content = _showFavoritesOnly ? "\u2605" : "\u2606";
            favoriteFilter.Foreground = Brush(_showFavoritesOnly ? "#F4C542" : "#9699A6");
            var offset = _activePageScrollViewer?.Offset.Y ?? 0;
            _loadMoreMediaCards = null;
            RefreshMediaList();
            Dispatcher.UIThread.Post(() => RestoreLibraryScrollOffset(offset));
        });
        favoriteFilter.Width = 44;
        favoriteFilter.Height = 44;
        favoriteFilter.Padding = new Thickness(0);
        favoriteFilter.FontSize = 22;
        favoriteFilter.Foreground = Brush(_showFavoritesOnly ? "#F4C542" : "#9699A6");
        favoriteFilter.Background = Brushes.Transparent;
        favoriteFilter.BorderThickness = new Thickness(0);
        favoriteFilter.HorizontalContentAlignment = HorizontalAlignment.Center;
        favoriteFilter.VerticalContentAlignment = VerticalAlignment.Center;
        favoriteFilter.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(favoriteFilter, "\uC990\uACA8\uCC3E\uAE30\uD55C VOD\uB9CC \uBCF4\uAE30");
        filters.Children.Add(favoriteFilter);
        Grid.SetColumn(favoriteFilter, 2);
        thumbnailToggle = MakeButton("", () =>
        {
            _showThumbnails = !_showThumbnails;
            thumbnailToggle.Content = BuildThumbnailIcon(_showThumbnails ? accent : "#9699A6");
            var offset = _activePageScrollViewer?.Offset.Y ?? 0;
            _loadMoreMediaCards = null;
            RefreshMediaList();
            Dispatcher.UIThread.Post(() => RestoreLibraryScrollOffset(offset));
        });
        thumbnailToggle.Content = BuildThumbnailIcon(_showThumbnails ? accent : "#9699A6");
        thumbnailToggle.Width = 44;
        thumbnailToggle.Height = 44;
        thumbnailToggle.Padding = new Thickness(0);
        thumbnailToggle.Background = Brushes.Transparent;
        thumbnailToggle.BorderThickness = new Thickness(0);
        thumbnailToggle.HorizontalContentAlignment = HorizontalAlignment.Center;
        thumbnailToggle.VerticalContentAlignment = VerticalAlignment.Center;
        thumbnailToggle.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(thumbnailToggle, "\uC378\uB124\uC77C \uD45C\uC2DC \uD1A0\uAE00");
        filters.Children.Add(thumbnailToggle);
        Grid.SetColumn(thumbnailToggle, 3);
        void RefreshMediaList()
        {
            ReleaseMediaThumbnailVisuals();
            mediaList.Children.Clear();
            var query = search.Text?.Trim() ?? string.Empty;
            var selectedStatusIndex = statusFilter.SelectedIndex;
            var selectedStatus = statusFilter.SelectedItem as string ?? string.Empty;
            var currentChannelData = _libraryStore.LoadAll().FirstOrDefault(item => ChannelKey(item.Channel) == ChannelKey(data.Channel)) ?? data;
            var filteredItems = currentChannelData.Media
                .Where(item => (query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
                    (!_showFavoritesOnly || item.Favorite) &&
                    (selectedStatusIndex == 0 || (string.IsNullOrWhiteSpace(item.WatchStatus) ? "\uBBF8\uC2DC\uCCAD" : item.WatchStatus) == selectedStatus))
                .ToArray();
            var mediaItems = sortOrder.SelectedIndex switch
            {
                1 => filteredItems.OrderBy(item => item.PublishedAt).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                2 => filteredItems.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(item => item.PublishedAt).ToArray(),
                _ => filteredItems.OrderByDescending(item => item.PublishedAt).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray()
            };
            var displayedCount = 0;
            Button? loadMoreButton = null;
            void AppendNextBatch()
            {
                if (loadMoreButton is not null) mediaList.Children.Remove(loadMoreButton);
                var end = Math.Min(displayedCount + mediaCardBatchSize, mediaItems.Length);
                for (; displayedCount < end; displayedCount++)
                    mediaList.Children.Add(BuildMediaCard(data.Channel, mediaItems[displayedCount], accent,
                        isFavoriteChange =>
                        {
                            if ((isFavoriteChange && _showFavoritesOnly) || (!isFavoriteChange && (_showFavoritesOnly || statusFilter.SelectedIndex != 0)))
                                RefreshMediaList();
                        }));
                if (displayedCount < mediaItems.Length)
                {
                    loadMoreButton = MakeButton($"더 불러오기 ({displayedCount}/{mediaItems.Length})", AppendNextBatch);
                    mediaList.Children.Add(loadMoreButton);
                }
                else
                {
                    loadMoreButton = null;
                    _loadMoreMediaCards = null;
                }
                ScheduleRefreshVisibleMediaCards();
            }
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
            _loadMoreMediaCards = displayedCount < mediaItems.Length ? AppendNextBatch : null;
            AppendNextBatch();
        }
        search.TextChanged += (_, _) => { _loadMoreMediaCards = null; RefreshMediaList(); };
        statusFilter.SelectionChanged += (_, _) => { _loadMoreMediaCards = null; RefreshMediaList(); };
        sortOrder.SelectionChanged += (_, _) => { _loadMoreMediaCards = null; RefreshMediaList(); };
        content.Children.Add(mediaList);
        RefreshMediaList();

        var pinnedControls = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(12, 8, 12, 0),
            Children = { search, filters }
        };
        var mediaScrollViewer = new ScrollViewer
        {
            Content = content,
            Background = Brush("#F5F6FA"),
            Padding = new Thickness(12, 8, 12, 20),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _activePageScrollViewer = mediaScrollViewer;
        mediaScrollViewer.ScrollChanged += (_, e) =>
        {
            ScheduleRefreshVisibleMediaCards();
            if (e.OffsetDelta.Y > 0 && _loadMoreMediaCards is not null &&
                mediaScrollViewer.Extent.Height - mediaScrollViewer.Offset.Y - mediaScrollViewer.Viewport.Height < 240)
                _loadMoreMediaCards();
        };
        mediaScrollViewer.LayoutUpdated += (_, _) => ScheduleRefreshVisibleMediaCards();
        var mediaScrollOverlay = new Grid
        {
            ClipToBounds = true,
            Children = { mediaScrollViewer, pullRefreshIndicator }
        };
        pullRefreshIndicator.ZIndex = 1;
        var channelPage = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { pinnedControls, mediaScrollOverlay }
        };
        Grid.SetRow(mediaScrollOverlay, 1);
        mediaScrollViewer.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            _pullRefreshScrollViewer = null;
            _pullRefreshStart = null;
            _pullRefreshPointer = null;
            if (_isRefreshingChannelVods || mediaScrollViewer.Offset.Y > 0.5) return;
            _pullRefreshScrollViewer = mediaScrollViewer;
            _pullRefreshStart = e.GetPosition(mediaScrollViewer);
            _pullRefreshPointer = e.Pointer;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        mediaScrollViewer.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (_pullRefreshPointer is null || !ReferenceEquals(e.Pointer, _pullRefreshPointer) ||
                _pullRefreshStart is not { } start) return;
            var distance = Math.Clamp(e.GetPosition(mediaScrollViewer).Y - start.Y, 0, 76);
            SetPullRefreshIndicatorPosition(distance, visible: true);
            pullRefreshDot.Fill = Brush(distance >= 76 ? "#7C5CFC" : "#777B8B");
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        mediaScrollViewer.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (_pullRefreshPointer is null || !ReferenceEquals(e.Pointer, _pullRefreshPointer) ||
                _pullRefreshStart is not { } start) return;
            var distance = e.GetPosition(mediaScrollViewer).Y - start.Y;
            var shouldRefresh = mediaScrollViewer.Offset.Y <= 0.5 && distance >= 76;
            _pullRefreshPointer = null;
            _pullRefreshStart = null;
            _pullRefreshScrollViewer = null;
            if (shouldRefresh && _refreshCurrentChannelVods is { } refresh)
                _ = refresh(true);
            else
            {
                SetPullRefreshIndicatorPosition(0);
                pullRefreshDot.Fill = Brush("#777B8B");
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        async Task RefreshChannelVodsAsync(bool refreshThumbnails)
        {
            if (_isRefreshingChannelVods) return;
            var refreshGeneration = _channelVodsRefreshGeneration;
            _isRefreshingChannelVods = true;
            // Keep the overlay pinned in its visible loading position for the full request.
            SetPullRefreshIndicatorPosition(76, visible: true);
            pullRefreshDot.Fill = Brush("#7C5CFC");
            _pullRefreshSpinnerAngle = 0;
            _pullRefreshSpinnerTimer.Start();
            try
            {
                var videos = await AndroidVodListingService.FetchLatestAsync(data.Channel);
                if (!ReferenceEquals(_activePageScrollViewer, mediaScrollViewer)) return;
                if (refreshThumbnails)
                {
                    var thumbnailUrls = videos.Select(video => video.ThumbnailUrl)
                        .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    await Task.Run(() =>
                    {
                        foreach (var url in thumbnailUrls) _libraryStore.InvalidateMediaThumbnailCache(url!);
                    });
                }

                var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var rows = videos.Select(video => new TransferMedia(
                    platform,
                    data.Channel.ChannelId,
                    data.Channel.Name,
                    data.Channel.Url,
                    video.VodId,
                    video.Title,
                    video.Url,
                    video.PublishedAt,
                    "\uBBF8\uC2DC\uCCAD",
                    0,
                    string.Empty,
                    string.Empty,
                    now,
                    video.ThumbnailUrl ?? string.Empty)).ToArray();
                // Importing a large channel library parses/merges many records. Keep it off
                // the UI thread so the indeterminate spinner can continue animating.
                var addedCount = await Task.Run(() => _libraryStore.Import(rows));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_activePageScrollViewer, mediaScrollViewer)) return;
                    RefreshMediaList();
                    if (refreshThumbnails && MainActivity.Current is { IsFinishing: false, IsDestroyed: false } activity)
                    {
                        var resultText = addedCount == 0 ? "최신 VOD입니다" : $"새 VOD {addedCount}개 추가";
                        global::Android.Widget.Toast.MakeText(activity, resultText, global::Android.Widget.ToastLength.Short)?.Show();
                    }
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or IOException or InvalidOperationException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (refreshThumbnails && MainActivity.Current is { IsFinishing: false, IsDestroyed: false } activity)
                        global::Android.Widget.Toast.MakeText(activity, "새로고침에 실패했습니다", global::Android.Widget.ToastLength.Short)?.Show();
                });
            }
            finally
            {
                await Task.Delay(1400);
                if (refreshGeneration == _channelVodsRefreshGeneration)
                {
                    _isRefreshingChannelVods = false;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(_activePageScrollViewer, mediaScrollViewer)) return;
                        _pullRefreshSpinnerTimer.Stop();
                        _pullRefreshSpinnerAngle = 0;
                        if (pullRefreshIcon.RenderTransform is RotateTransform rotation) rotation.Angle = 0;
                        pullRefreshDot.Fill = Brush("#777B8B");
                        SetPullRefreshIndicatorPosition(0);
                    });
                }
            }
        }
        _refreshCurrentChannelVods = RefreshChannelVodsAsync;
        Dispatcher.UIThread.Post(() => _ = RefreshChannelVodsAsync(false), DispatcherPriority.Background);
        return channelPage;
    }

    private static Control BuildThumbnailIcon(string color)
    {
        var stroke = Brush(color);
        return new Border
        {
            Width = 26,
            Height = 21,
            CornerRadius = new CornerRadius(2),
            BorderBrush = stroke,
            BorderThickness = new Thickness(1.5),
            Child = new Grid
            {
                Children =
                {
                    new Ellipse { Width = 5, Height = 5, Fill = stroke, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 3, 0) },
                    new Polyline { Points = new[] { new Point(2, 17), new Point(8, 10), new Point(12, 14), new Point(16, 9), new Point(23, 17) }, Stroke = stroke, StrokeThickness = 1.8 }
                }
            }
        };
    }

    private Control BuildMediaCard(SavedChannel channel, SavedMedia media, string accent, Action<bool> refreshFilteredList)
    {
        var status = string.IsNullOrWhiteSpace(media.WatchStatus) ? "\uBBF8\uC2DC\uCCAD" : media.WatchStatus;
        var statusColor = status switch
        {
            "\uC2DC\uCCAD \uC644\uB8CC" => "#2E9B57",
            "\uC2DC\uCCAD \uC911" => "#D99B18",
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
            Child = new TextBlock { Text = "\u25B6", FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var statusBadgeText = new TextBlock { Text = status, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        var statusBadge = new Border
        {
            Background = Brush(statusColor), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4),
            Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = statusBadgeText
        };
        var thumbnailContent = new Grid
        {
            Background = Brush("#202536"),
            Height = 176,
            IsVisible = _showThumbnails,
            Children =
            {
                thumbnailImage,
                playIndicator,
                new TextBlock { Text = "VOD", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
                statusBadge
            }
        };
        var isWatching = status == "\uC2DC\uCCAD \uC911";
        MediaCardVisual? mediaVisual = null;
        Button playbackTime = null!;
        playbackTime = MakeButton(FormatWatchTime(media.LastPositionSeconds), () => OpenPositionPicker(channel, media,
            () => mediaVisual?.DurationSeconds,
            position =>
        {
            media = media with { LastPositionSeconds = position };
            playbackTime.Content = FormatWatchTime(position);
            if (mediaVisual is not null)
            {
                mediaVisual.PositionSeconds = position;
                if (mediaVisual.DurationSeconds is > 0)
                    mediaVisual.ProgressBar.Value = Math.Clamp(position / mediaVisual.DurationSeconds.Value, 0, 1);
            }
        }));
        playbackTime.MinHeight = 32;
        playbackTime.Padding = new Thickness(8, 3);
        playbackTime.FontSize = 12;
        playbackTime.HorizontalContentAlignment = HorizontalAlignment.Center;
        playbackTime.Background = Brush("#F7F7FA");
        playbackTime.Foreground = Brush(accent);
        playbackTime.BorderBrush = Brush("#D9D2FA");
        playbackTime.BorderThickness = new Thickness(1);
        playbackTime.CornerRadius = new CornerRadius(8);
        playbackTime.IsVisible = isWatching;
        playbackTime.PointerPressed += (_, e) => e.Handled = true;
        var inlineDuration = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(media.OnlineUrl) ? "--:--:--" : "--:--:--",
            FontSize = 12,
            Foreground = Brush("#777B8B"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var playbackPositionRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 5,
            IsVisible = isWatching,
            Children =
            {
                new TextBlock { Text = "\uC2DC\uCCAD \uC704\uCE58", FontSize = 12, Foreground = Brush("#777B8B"), VerticalAlignment = VerticalAlignment.Center },
                playbackTime,
                new TextBlock { Text = "/", FontSize = 12, Foreground = Brush("#777B8B"), VerticalAlignment = VerticalAlignment.Center },
                inlineDuration
            }
        };
        var totalDuration = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(media.OnlineUrl) ? "\uC601\uC0C1 \uAE38\uC774 \uD655\uC778 \uBD88\uAC00" : "\uC601\uC0C1 \uAE38\uC774 \uBD88\uB7EC\uC624\uB294 \uC911...",
            FontSize = 11,
            Foreground = Brush("#777B8B"),
            IsVisible = !isWatching
        };
        var playbackProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 5,
            IsVisible = isWatching,
            Foreground = Brush(accent),
            Background = Brush("#E8E9EF")
        };
        var published = media.PublishedAt?.ToLocalTime().ToString("yyyy.MM.dd HH:mm") ?? string.Empty;
        var sourceLabel = string.IsNullOrWhiteSpace(media.OnlineUrl) ? "PC \uD30C\uC77C" : "\uC628\uB77C\uC778 VOD";
        Button favoriteButton = null!;
        favoriteButton = MakeButton(media.Favorite ? "\u2605" : "\u2606", () =>
        {
            media = media with { Favorite = !media.Favorite };
            _libraryStore.UpdateFavorite(channel.Platform, channel.ChannelId, media.VodId, media.Favorite);
            favoriteButton.Content = media.Favorite ? "\u2605" : "\u2606";
            favoriteButton.Foreground = Brush(media.Favorite ? "#F4C542" : "#9699A6");
            refreshFilteredList(true);
        });
        favoriteButton.Width = 36;
        favoriteButton.Height = 36;
        favoriteButton.Padding = new Thickness(0);
        favoriteButton.FontSize = 22;
        favoriteButton.Foreground = Brush(media.Favorite ? "#F4C542" : "#9699A6");
        favoriteButton.Background = Brushes.Transparent;
        favoriteButton.BorderThickness = new Thickness(0);
        favoriteButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        favoriteButton.VerticalContentAlignment = VerticalAlignment.Center;
        favoriteButton.PointerPressed += (_, e) => e.Handled = true;
        var titleText = new TextBlock
        {
            Text = media.Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#292B3A"),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 4,
            Children =
            {
                titleText,
                favoriteButton
            }
        };
        Grid.SetColumn(favoriteButton, 1);
        var noteText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(media.Memo) ? "\uB178\uD2B8 \uCD94\uAC00" : media.Memo,
            FontSize = 12,
            Foreground = string.IsNullOrWhiteSpace(media.Memo) ? Brush("#9699A6") : Brush("#777B8B"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        noteText.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ShowMemoEditor(channel, media, _activePageScrollViewer?.Offset.Y ?? 0, memo =>
            {
                media = media with { Memo = memo };
                noteText.Text = string.IsNullOrWhiteSpace(memo) ? "\uB178\uD2B8 \uCD94\uAC00" : memo;
                noteText.Foreground = string.IsNullOrWhiteSpace(memo) ? Brush("#9699A6") : Brush("#777B8B");
            });
        };
        var noteButton = MakeButton("\uD83D\uDCDD", () => ShowMemoEditor(channel, media, _activePageScrollViewer?.Offset.Y ?? 0, memo =>
        {
            media = media with { Memo = memo };
            noteText.Text = string.IsNullOrWhiteSpace(memo) ? "\uB178\uD2B8 \uCD94\uAC00" : memo;
            noteText.Foreground = string.IsNullOrWhiteSpace(memo) ? Brush("#9699A6") : Brush("#777B8B");
        }));
        noteButton.Width = 36;
        noteButton.Height = 36;
        noteButton.Padding = new Thickness(0);
        noteButton.FontSize = 17;
        noteButton.Background = Brushes.Transparent;
        noteButton.BorderThickness = new Thickness(0);
        noteButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        noteButton.VerticalContentAlignment = VerticalAlignment.Center;
        noteButton.PointerPressed += (_, e) => e.Handled = true;
        ToolTip.SetTip(noteButton, "\uB178\uD2B8 \uD3B8\uC9D1");
        Button statusButton = null!;
        statusButton = MakeButton(status, () =>
        {
            var nextStatus = (string.IsNullOrWhiteSpace(media.WatchStatus) ? "\uBBF8\uC2DC\uCCAD" : media.WatchStatus) switch
            {
                "\uBBF8\uC2DC\uCCAD" => "\uC2DC\uCCAD \uC911",
                "\uC2DC\uCCAD \uC911" => "\uC2DC\uCCAD \uC644\uB8CC",
                _ => "\uBBF8\uC2DC\uCCAD"
            };
            media = media with { WatchStatus = nextStatus };
            _libraryStore.UpdateWatchStatus(channel.Platform, channel.ChannelId, media.VodId, nextStatus);
            statusButton.Content = nextStatus;
            statusBadgeText.Text = nextStatus;
            var color = nextStatus switch { "\uC2DC\uCCAD \uC911" => "#D99B18", "\uC2DC\uCCAD \uC644\uB8CC" => "#2E9B57", _ => "#D84040" };
            statusButton.Background = Brush(color);
            statusButton.BorderBrush = Brush(color);
            statusBadge.Background = Brush(color);
            var watching = nextStatus == "\uC2DC\uCCAD \uC911";
            if (mediaVisual is not null) mediaVisual.IsWatching = watching;
            playbackPositionRow.IsVisible = watching;
            playbackTime.IsVisible = watching;
            playbackProgress.IsVisible = watching;
            totalDuration.IsVisible = !watching;
            if (watching && mediaVisual?.DurationSeconds is > 0)
                playbackProgress.Value = Math.Clamp(media.LastPositionSeconds / mediaVisual.DurationSeconds.Value, 0, 1);
            refreshFilteredList(false);
            if (watching && !string.IsNullOrWhiteSpace(media.OnlineUrl)) ScheduleRefreshVisibleMediaCards();
        }, primary: true);
        statusButton.Padding = new Thickness(10, 5);
        statusButton.FontSize = 12;
        statusButton.MinHeight = 34;
        statusButton.Background = Brush(statusColor);
        statusButton.BorderBrush = Brush(statusColor);
        statusButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        statusButton.PointerPressed += (_, e) => e.Handled = true;
        var bottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                new TextBlock { Text = published, FontSize = 12, Foreground = Brush("#777B8B"), VerticalAlignment = VerticalAlignment.Center },
                noteButton,
                statusButton
            }
        };
        Grid.SetColumn(noteButton, 1);
        Grid.SetColumn(statusButton, 2);
        var cardContent = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                titleRow,
                new TextBlock { Text = sourceLabel, FontSize = 11, Foreground = Brush(accent), VerticalAlignment = VerticalAlignment.Center },
                new StackPanel { Spacing = 4, Children = { playbackPositionRow, playbackProgress, totalDuration } },
                noteText,
                bottomRow
            }
        };
        mediaVisual = new MediaCardVisual(thumbnailContent, thumbnailImage, playIndicator,
            LibraryTransfer.NormalizePlatform(channel.Platform), media.ThumbnailUrl, media.OnlineUrl,
            media.LastPositionSeconds, isWatching, playbackTime, playbackProgress, totalDuration, inlineDuration);
        _mediaCardVisuals.Add(mediaVisual);
        var card = Card(new StackPanel
        {
            Spacing = 0,
            Children = { thumbnailContent, new Border { Height = 1, Background = Brush("#E8E9EF") }, new Border { Padding = new Thickness(14), Child = cardContent } }
        });
        void RegisterMediaOpenTap(Control target)
        {
            target.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(target).Properties.IsLeftButtonPressed || string.IsNullOrWhiteSpace(media.OnlineUrl)) return;
                var address = media.WatchStatus == "\uC2DC\uCCAD \uC911"
                    ? AddVodStartPosition(media.OnlineUrl, channel.Platform, media.LastPositionSeconds)
                    : media.OnlineUrl;
                _mediaCardTapCandidate = new MediaCardTapCandidate(e.GetPosition(_root), address, e.Pointer);
            };
        }
        RegisterMediaOpenTap(thumbnailContent);
        RegisterMediaOpenTap(titleText);
        return card;
    }

    private void ShowMemoEditor(SavedChannel channel, SavedMedia media, double scrollOffset, Action<string> onSaved)
    {
        var previousPage = _pageHost.Content;
        var previousScrollViewer = _activePageScrollViewer;
        var previousPageTitle = _pageTitle.Text;
        var wasChannelBackVisible = _channelBackButton?.IsVisible ?? false;
        void ReturnToLibraryPage()
        {
            _pageHost.Content = previousPage;
            _activePageScrollViewer = previousScrollViewer;
            _pageTitle.Text = previousPageTitle;
            if (_channelBackButton is not null) _channelBackButton.IsVisible = wasChannelBackVisible;
            Dispatcher.UIThread.Post(() => RestoreLibraryScrollOffset(scrollOffset));
        }
        var editor = new TextBox
        {
            Text = media.Memo ?? string.Empty,
            PlaceholderText = "\uB178\uD2B8\uB97C \uC785\uB825\uD558\uC138\uC694",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 180,
            MaxLength = 10000,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        var cancelButton = MakeButton("\uCDE8\uC18C", () =>
        {
            ReturnToLibraryPage();
        });
        var saveButton = MakeButton("\uC800\uC7A5", () =>
        {
            var memo = editor.Text ?? string.Empty;
            _libraryStore.UpdateMemo(channel.Platform, channel.ChannelId, media.VodId, memo);
            onSaved(memo);
            ReturnToLibraryPage();
        }, primary: true);
        var footer = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        _pageHost.Content = BuildPage("\uB178\uD2B8 \uD3B8\uC9D1", new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = media.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A"), TextWrapping = TextWrapping.Wrap },
                editor,
                footer
            }
        });
        editor.Focus();
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

    private void ScheduleRefreshVisibleMediaCards()
    {
        if (_thumbnailRefreshScheduled) return;
        _thumbnailRefreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _thumbnailRefreshScheduled = false;
            RefreshVisibleMediaCards();
        });
    }

    private void RefreshVisibleMediaCards()
    {
        var scrollViewer = _activePageScrollViewer;
        if (scrollViewer is null || _mediaCardVisuals.Count == 0) return;

        var viewport = scrollViewer.Bounds;
        foreach (var visual in _mediaCardVisuals)
        {
            var position = visual.ViewportElement.TranslatePoint(default, scrollViewer);
            var bounds = visual.ViewportElement.Bounds;
            var isVisible = position is { } point && bounds.Width > 0 && bounds.Height > 0 &&
                            point.X < viewport.Width && point.Y < viewport.Height &&
                            point.X + bounds.Width > 0 && point.Y + bounds.Height > 0;
            if (isVisible)
            {
                visual.IsVisible = true;
                if (_showThumbnails && !string.IsNullOrWhiteSpace(visual.ThumbnailUrl) && visual.Bitmap is null &&
                    visual.ThumbnailCancellation is null && !visual.ThumbnailHasFailed)
                {
                    visual.ThumbnailCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    _ = LoadMediaThumbnailAsync(visual, visual.ThumbnailCancellation);
                }
                if (!string.IsNullOrWhiteSpace(visual.OnlineUrl) &&
                    visual.DurationSeconds is null && visual.DurationCancellation is null && !visual.DurationHasFailed)
                {
                    visual.DurationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(22));
                    _ = LoadMediaDurationAsync(visual, visual.DurationCancellation);
                }
                continue;
            }

            visual.IsVisible = false;
            visual.DurationCancellation?.Cancel();
            if (visual.Bitmap is not null)
            {
                visual.Image.Source = null;
                visual.Bitmap.Dispose();
                visual.Bitmap = null;
                visual.Image.IsVisible = false;
                visual.Placeholder.IsVisible = true;
            }
        }
    }

    private async Task LoadMediaThumbnailAsync(MediaCardVisual visual, CancellationTokenSource cancellation)
    {
        Bitmap? bitmap = null;
        try
        {
            var cachePath = _libraryStore.GetMediaThumbnailPath(visual.ThumbnailUrl!);
            byte[] bytes;
            if (File.Exists(cachePath))
                bytes = await File.ReadAllBytesAsync(cachePath, cancellation.Token);
            else
            {
                bytes = await AndroidChannelProfileService.DownloadImageAsync(visual.ThumbnailUrl!,
                    LibraryTransfer.NormalizePlatform(visual.Platform), cancellation.Token);
                await File.WriteAllBytesAsync(cachePath, bytes, cancellation.Token);
            }
            using var stream = new MemoryStream(bytes);
            bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(visual.ThumbnailCancellation, cancellation) && !cancellation.IsCancellationRequested)
                {
                    visual.Bitmap = bitmap;
                    visual.Image.Source = bitmap;
                    visual.Image.IsVisible = true;
                    visual.Placeholder.IsVisible = false;
                    bitmap = null;
                }
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (visual.IsVisible && ReferenceEquals(visual.ThumbnailCancellation, cancellation))
                visual.ThumbnailHasFailed = true;
        }
        finally
        {
            bitmap?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(visual.ThumbnailCancellation, cancellation)) visual.ThumbnailCancellation = null;
                cancellation.Dispose();
                ScheduleRefreshVisibleMediaCards();
            });
        }
    }

    private async Task LoadMediaDurationAsync(MediaCardVisual visual, CancellationTokenSource cancellation)
    {
        double? duration = null;
        try
        {
            duration = await AndroidVodMetadataService.GetDurationSecondsAsync(visual.Platform, visual.OnlineUrl, cancellation.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (visual.IsVisible && ReferenceEquals(visual.DurationCancellation, cancellation))
                visual.DurationHasFailed = true;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (visual.IsVisible && ReferenceEquals(visual.DurationCancellation, cancellation) &&
                    !cancellation.IsCancellationRequested && duration is > 0)
                {
                    visual.DurationSeconds = duration;
                    visual.DurationLabel.Text = $"\uC601\uC0C1 \uAE38\uC774  {FormatWatchTime(duration.Value)}";
                    visual.InlineDurationLabel.Text = FormatWatchTime(duration.Value);
                    if (visual.IsWatching)
                    {
                        visual.PositionButton.Content = FormatWatchTime(visual.PositionSeconds);
                        visual.ProgressBar.Value = Math.Clamp(visual.PositionSeconds / duration.Value, 0, 1);
                        visual.ProgressBar.IsVisible = true;
                    }
                }
                else if (visual.IsVisible && ReferenceEquals(visual.DurationCancellation, cancellation) &&
                         !cancellation.IsCancellationRequested)
                {
                    visual.DurationHasFailed = true;
                    visual.DurationLabel.Text = "\uC601\uC0C1 \uAE38\uC774 \uD655\uC778 \uBD88\uAC00";
                }

                if (ReferenceEquals(visual.DurationCancellation, cancellation)) visual.DurationCancellation = null;
                cancellation.Dispose();
                ScheduleRefreshVisibleMediaCards();
            });
        }
    }

    private static string FormatWatchTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private void OpenPositionPicker(SavedChannel channel, SavedMedia media, Func<double?> getDuration, Action<double> onPositionUpdated)
    {
        var activity = MainActivity.Current;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed) return;
        var current = TimeSpan.FromSeconds(Math.Max(0, media.LastPositionSeconds));
        var hour = (int)Math.Clamp(Math.Floor(current.TotalHours), 0, 23);
        var minute = current.Minutes;
        var second = current.Seconds;
        var density = activity.Resources?.DisplayMetrics?.Density ?? 1f;
        int Dp(float value) => (int)(value * density + 0.5f);
        var selectors = new global::Android.Widget.LinearLayout(activity)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal
        };
        selectors.SetPadding(Dp(12), Dp(4), Dp(12), Dp(4));

        global::Android.Widget.NumberPicker AddSelector(int maxValue, int initialValue)
        {
            var column = new global::Android.Widget.LinearLayout(activity)
            {
                Orientation = global::Android.Widget.Orientation.Vertical
            };
            column.SetGravity(global::Android.Views.GravityFlags.CenterHorizontal);
            var picker = new global::Android.Widget.NumberPicker(activity)
            {
                MinValue = 0,
                MaxValue = maxValue,
                Value = initialValue,
                WrapSelectorWheel = true
            };
            picker.SetDisplayedValues(Enumerable.Range(0, maxValue + 1).Select(value => value.ToString("D2", CultureInfo.InvariantCulture)).ToArray());
            picker.DescendantFocusability = global::Android.Views.DescendantFocusability.AfterDescendants;
            column.AddView(picker, new global::Android.Widget.LinearLayout.LayoutParams(-1, Dp(150)));
            selectors.AddView(column, new global::Android.Widget.LinearLayout.LayoutParams(0, -2, 1f));
            return picker;
        }

        void AddTimeSeparator()
        {
            var separator = new global::Android.Widget.TextView(activity)
            {
                Text = ":",
                Gravity = global::Android.Views.GravityFlags.Center,
                TextSize = 24
            };
            selectors.AddView(separator, new global::Android.Widget.LinearLayout.LayoutParams(Dp(18), Dp(150)));
        }
        var hourPicker = AddSelector(23, hour);
        AddTimeSeparator();
        var minutePicker = AddSelector(59, minute);
        AddTimeSeparator();
        var secondPicker = AddSelector(59, second);
        var dialogBuilder = new global::Android.App.AlertDialog.Builder(activity);
        dialogBuilder.SetTitle("\uC2DC\uCCAD \uC704\uCE58");
        dialogBuilder.SetView(selectors);
        dialogBuilder.SetNegativeButton("\uCDE8\uC18C", (_, _) => { });
        dialogBuilder.SetPositiveButton("\uC800\uC7A5", (_, _) =>
        {
            var positionSeconds = hourPicker.Value * 3600d + minutePicker.Value * 60d + secondPicker.Value;
            if (getDuration() is > 0 and var duration)
                positionSeconds = Math.Min(positionSeconds, duration);
            _libraryStore.UpdateLastPosition(channel.Platform, channel.ChannelId, media.VodId, positionSeconds);
            onPositionUpdated(positionSeconds);
        });
        dialogBuilder.Create()?.Show();
    }

    private static string AddVodStartPosition(string address, string platform, double positionSeconds)
    {
        if (positionSeconds <= 0 || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return address;
        var parameter = LibraryTransfer.NormalizePlatform(platform) == "SOOP" ? "change_second" : "currentTime";
        var builder = new UriBuilder(uri);
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals(parameter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        queryParts.Add($"{parameter}={((long)Math.Floor(positionSeconds)).ToString(CultureInfo.InvariantCulture)}");
        builder.Query = string.Join('&', queryParts);
        return builder.Uri.AbsoluteUri;
    }

    private void ReleaseMediaThumbnailVisuals()
    {
        foreach (var visual in _mediaCardVisuals)
        {
            visual.IsVisible = false;
            visual.ThumbnailCancellation?.Cancel();
            visual.DurationCancellation?.Cancel();
            if (visual.Bitmap is not null)
            {
                visual.Image.Source = null;
                visual.Bitmap.Dispose();
                visual.Bitmap = null;
            }
        }
        _mediaCardVisuals.Clear();
    }

    private sealed class MediaCardVisual(Control viewportElement, Image image, Control placeholder, string platform,
        string? thumbnailUrl, string? onlineUrl, double positionSeconds, bool isWatching,
        Button positionButton, ProgressBar progressBar, TextBlock durationLabel, TextBlock inlineDurationLabel)
    {
        public Control ViewportElement { get; } = viewportElement;
        public Image Image { get; } = image;
        public Control Placeholder { get; } = placeholder;
        public string Platform { get; } = platform;
        public string? ThumbnailUrl { get; } = thumbnailUrl;
        public string? OnlineUrl { get; } = onlineUrl;
        public double PositionSeconds { get; set; } = positionSeconds;
        public bool IsWatching { get; set; } = isWatching;
        public Button PositionButton { get; } = positionButton;
        public ProgressBar ProgressBar { get; } = progressBar;
        public TextBlock DurationLabel { get; } = durationLabel;
        public TextBlock InlineDurationLabel { get; } = inlineDurationLabel;
        public bool IsVisible { get; set; }
        public bool ThumbnailHasFailed { get; set; }
        public bool DurationHasFailed { get; set; }
        public double? DurationSeconds { get; set; }
        public Bitmap? Bitmap { get; set; }
        public CancellationTokenSource? ThumbnailCancellation { get; set; }
        public CancellationTokenSource? DurationCancellation { get; set; }
    }

    private sealed class MediaCardTapCandidate(Point start, string url, IPointer pointer)
    {
        public Point Start { get; } = start;
        public string Url { get; } = url;
        public IPointer Pointer { get; } = pointer;
        public bool HasMoved { get; set; }
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

    private async Task RefreshSelectedChannelStatusAsync(SavedChannel channel, Border avatar, TextBlock nameText, TextBlock descriptionText, TextBlock vodCountText, int savedVodCount)
    {
        var platform = LibraryTransfer.NormalizePlatform(channel.Platform);
        var key = ChannelKey(channel);
        try
        {
            var profile = await AndroidChannelProfileService.LookupAsync(platform, channel.ChannelId);
            _liveStatus[key] = profile.IsLive;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                avatar.BorderBrush = GetLiveBorderBrush(platform, profile.IsLive);
                nameText.Text = profile.Name;
                descriptionText.Text = string.IsNullOrWhiteSpace(profile.Description) ? "" : profile.Description;
                vodCountText.Text = $"VOD {profile.VodCount ?? savedVodCount}\uAC1C";
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => avatar.BorderBrush = GetLiveBorderBrush(platform, false));
        }
    }

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
                card.VodCount.Text = $"VOD {profile?.VodCount ?? data.Media.Count}\uAC1C";
                card.PlatformBadge.Background = Brush(platform == "SOOP" ? "#398FE0" : "#35B879");
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
        TextBlock Name, TextBlock Description, TextBlock VodCount, Border PlatformBadge);

    private void ShowSettings()
    {
        ReleaseMediaThumbnailVisuals();
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
        var cardBatchSizeInput = new TextBox
        {
            Text = _settings.LoadCardBatchSize().ToString(),
            PlaceholderText = "80",
            MinHeight = 42,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("#FFFFFF"),
            Foreground = Brush("#292B3A"),
            BorderBrush = Brush("#E5E6ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        var cardBatchStatus = new TextBlock { Text = "처음 표시할 카드 수이며, 스크롤할 때 같은 수만큼 더 불러옵니다. (20~1000)", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#777B8B") };
        var saveCardBatchSize = MakeButton("카드 표시 수 저장", () =>
        {
            var size = int.TryParse(cardBatchSizeInput.Text, out var parsed) ? parsed : 80;
            size = Math.Clamp(size, 20, 1000);
            _settings.SaveCardBatchSize(size);
            cardBatchSizeInput.Text = size.ToString();
            cardBatchStatus.Text = $"카드 표시 수를 {size}개로 저장했습니다. 다음 채널 화면부터 적용됩니다.";
        }, primary: true);
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
                        new TextBlock { Text = "VOD 카드 표시 수", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brush("#292B3A") },
                        cardBatchSizeInput,
                        saveCardBatchSize,
                        cardBatchStatus
                    }
                }),
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
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            Background = Brush("#F5F6FA"),
            Padding = new Thickness(0, 8, 0, 20),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _activePageScrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += (_, e) =>
        {
            ScheduleRefreshVisibleMediaCards();
            if (e.OffsetDelta.Y > 0 && _loadMoreMediaCards is not null &&
                scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height < 240)
                _loadMoreMediaCards();
        };
        scrollViewer.LayoutUpdated += (_, _) => ScheduleRefreshVisibleMediaCards();
        return scrollViewer;
    }

    private void RestoreLibraryScrollOffset(double offset)
    {
        if (_activePageScrollViewer is not { } scrollViewer) return;
        void RestoreOffset()
        {
            if (ReferenceEquals(_activePageScrollViewer, scrollViewer))
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, offset));
        }

        EventHandler? afterLayout = null;
        afterLayout = (_, _) =>
        {
            scrollViewer.LayoutUpdated -= afterLayout;
            Dispatcher.UIThread.Post(RestoreOffset, DispatcherPriority.Background);
        };
        scrollViewer.LayoutUpdated += afterLayout;
        RestoreOffset();
        Dispatcher.UIThread.Post(RestoreOffset, DispatcherPriority.Background);
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
                DrawerAction("↑", "CSV 내보내기", () => _ = ExportCsvAsync()),
                DrawerAction("🗑", "모든 채널 및 기록 삭제", ShowDeleteAllLibraryConfirmation)
            }
        };
        var themeButton = new Button
        {
            Content = _isDarkTheme ? "☀" : "☾",
            Width = 48,
            Height = 48,
            Padding = new Thickness(0),
            FontSize = 23,
            Background = Brush("#2A2D3B"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#3A3D4B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        themeButton.Click += (_, _) => ToggleTheme();
        _drawerThemeButton = themeButton;
        var settingsButton = new Button
        {
            Content = "⚙",
            Width = 48,
            Height = 48,
            Padding = new Thickness(0),
            FontSize = 21,
            Background = Brush("#20232F"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#3B4050"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        settingsButton.Click += (_, _) => ToggleSettings();
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
            Background = Brush("#12141D"),
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
            MinHeight = 48,
            Padding = new Thickness(14, 9),
            Background = Brush("#20232F"),
            Foreground = Brush("#F5F6FA"),
            BorderBrush = Brush("#3B4050"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = icon, Width = 24, FontSize = 17, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White },
                    new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White }
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

    internal bool HandleSystemBack()
    {
        if (_drawerOverlay?.IsVisible == true)
        {
            SetDrawerOpen(false);
            return true;
        }

        if (_isSettingsActive)
        {
            ShowLibrary();
            return true;
        }

        if (_selectedChannelKey is not null)
        {
            _selectedChannelKey = null;
            ShowLibrary();
            return true;
        }

        return false;
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

    private void ShowDeleteAllLibraryConfirmation()
    {
        var activity = MainActivity.Current;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed) return;
        var builder = new global::Android.App.AlertDialog.Builder(activity);
        builder.SetTitle("모든 채널 및 기록 삭제");
        builder.SetMessage("저장된 채널 및 VOD 시청 기록이 전부 삭제 됩니다.");
        builder.SetNegativeButton("취소", (_, _) => { });
        builder.SetPositiveButton("확인", (_, _) => _ = DeleteAllLibraryAsync());
        var dialog = builder.Create();
        dialog?.Show();
        dialog?.GetButton((int)global::Android.Content.DialogButtonType.Positive)
            ?.SetTextColor(global::Android.Graphics.Color.Rgb(216, 64, 64));
    }

    private async Task DeleteAllLibraryAsync()
    {
        try
        {
            await _libraryStore.DeleteAllAsync();
            _selectedChannelKey = null;
            _selectedSection = "all";
            _showFavoritesOnly = false;
            _loadMoreMediaCards = null;
            ShowLibrary();
            SetDrawerOpen(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var activity = MainActivity.Current;
            if (activity is null || activity.IsFinishing || activity.IsDestroyed) return;
            var errorDialog = new global::Android.App.AlertDialog.Builder(activity);
            errorDialog.SetTitle("기록 삭제 실패");
            errorDialog.SetMessage(exception.Message);
            errorDialog.SetPositiveButton("확인", (_, _) => { });
            errorDialog.Show();
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
            item.Background = Brush(section == _selectedSection ? "#6046C8" : "#20232F");
        if (_drawerSettingsButton is not null)
            _drawerSettingsButton.Background = Brush(_isSettingsActive ? "#6046C8" : "#20232F");
        if (_headerSettingsButton is not null)
            _headerSettingsButton.Background = _isSettingsActive ? Brush("#6046C8") : Brushes.Transparent;
        if (_drawerThemeButton is not null)
        {
            _drawerThemeButton.Content = _isDarkTheme ? "☀" : "☾";
            _drawerThemeButton.Background = Brush("#20232F");
            _drawerThemeButton.Foreground = Brushes.White;
        }
        if (_headerThemeButton is not null)
            _headerThemeButton.Content = _isDarkTheme ? "☀" : "☾";
    }

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _swipeStart = e.GetPosition(_root);
    }

    private void SetPullRefreshIndicatorPosition(double distance, bool visible = false)
    {
        _pullRefreshDistance = Math.Clamp(distance, 0, 76);
        if (_pullRefreshIndicator?.RenderTransform is TranslateTransform translate)
            translate.Y = _pullRefreshDistance;
        if (_pullRefreshIndicator is { } indicator)
            indicator.Opacity = visible ? 1 : Math.Clamp(_pullRefreshDistance / 18d, 0, 1);
    }

    private void Root_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mediaCardTapCandidate is { } candidate && ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            var delta = e.GetPosition(_root) - candidate.Start;
            if (delta.X * delta.X + delta.Y * delta.Y > 16 * 16) candidate.HasMoved = true;
        }
    }

    private void Root_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var end = e.GetPosition(_root);
        if (_mediaCardTapCandidate is { } candidate && ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            _mediaCardTapCandidate = null;
            var delta = end - candidate.Start;
            if (!candidate.HasMoved && delta.X * delta.X + delta.Y * delta.Y <= 16 * 16) _ = OpenAddressAsync(candidate.Url);
        }

        if (_swipeStart is not { } start) return;
        _swipeStart = null;
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
