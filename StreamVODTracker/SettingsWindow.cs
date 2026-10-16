using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Win32;

namespace StreamVODTracker;

public sealed class SettingsWindow : Window
{
    private readonly TextBlock _titleText;
    private readonly TextBlock _descriptionText;
    private readonly TextBlock _themeTitle;
    private readonly TextBlock _themeDescription;
    private readonly TextBlock _driveTitle;
    private readonly TextBlock _driveDescription;
    private readonly TextBlock _driveStatus;
    private readonly ComboBox _themePicker;
    private readonly TextBox _driveAccount;
    private readonly Button _driveConnectButton;
    private readonly Border _themeCard;
    private readonly Border _driveCard;
    private readonly TextBlock _playerTitle;
    private readonly TextBlock _playerDescription;
    private readonly TextBlock _playerStatus;
    private readonly ComboBox _playerPicker;
    private readonly TextBox _playerPath;
    private readonly ComboBox _argumentPreset;
    private readonly TextBox _playerArguments;
    private readonly Border _playerCard;
    private readonly AppSettingsStore _settingsStore;
    private readonly List<PlayerOption> _playerOptions = [];
    private bool _handlingPlayerSelection;
    private bool _updatingArguments;
    private int _lastPlayerSelectionIndex;
    private string _activeArgumentsPlayerPath = "";

    public SettingsWindow(bool isDarkTheme, Action<bool> setTheme, AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        Title = "설정";
        Width = 560;
        Height = 720;
        MinWidth = 500;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _titleText = new TextBlock { Text = "설정", FontSize = 24, FontWeight = FontWeight.Bold };
        _descriptionText = new TextBlock
        {
            Text = "화면 테마와 향후 추가될 클라우드 동기화 옵션을 관리합니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        _themeTitle = new TextBlock { Text = "화면 테마", FontSize = 16, FontWeight = FontWeight.SemiBold };
        _themeDescription = new TextBlock
        {
            Text = "앱의 라이트 또는 다크 테마를 선택합니다. 선택한 테마는 자동 저장됩니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        _themePicker = new ComboBox { MinHeight = 38, Width = 180 };
        _themePicker.Items.Add("라이트");
        _themePicker.Items.Add("다크");
        _themePicker.SelectedIndex = isDarkTheme ? 1 : 0;

        _themeCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 9,
                Children = { _themeTitle, _themeDescription, _themePicker }
            }
        };

        _playerTitle = new TextBlock { Text = "로컬 영상 플레이어", FontSize = 16, FontWeight = FontWeight.SemiBold };
        _playerDescription = new TextBlock
        {
            Text = "설치된 플레이어를 선택하거나 직접 EXE 파일을 추가할 수 있습니다. 인수의 {file}은 영상 파일 경로로 바뀝니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        _playerPicker = new ComboBox { MinHeight = 38 };
        _playerPath = new TextBox { MinHeight = 36, IsReadOnly = true, PlaceholderText = "Windows 기본 연결 앱" };
        _argumentPreset = new ComboBox { MinHeight = 38 };
        _argumentPreset.Items.Add("기본 파일 경로");
        _argumentPreset.Items.Add("VLC · 이어보기");
        _argumentPreset.Items.Add("PotPlayer · 이어보기");
        _argumentPreset.Items.Add("mpv · 이어보기");
        _argumentPreset.Items.Add("직접 입력");
        _playerArguments = new TextBox { MinHeight = 38, PlaceholderText = "예: --fullscreen \"{file}\"" };
        _playerStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };

        _playerOptions.Add(new PlayerOption("Windows 기본 연결 앱", ""));
        _playerOptions.AddRange(DiscoverInstalledPlayers());
        foreach (var option in _playerOptions) _playerPicker.Items.Add(option);
        _playerPicker.Items.Add(PlayerOption.AddCustom);

        var storedPlayerPath = _settingsStore.LoadVideoPlayerPath();
        _activeArgumentsPlayerPath = storedPlayerPath;
        if (!string.IsNullOrWhiteSpace(storedPlayerPath))
        {
            var storedOption = _playerOptions.FirstOrDefault(option =>
                option.Path.Equals(storedPlayerPath, StringComparison.OrdinalIgnoreCase));
            if (storedOption is null)
            {
                storedOption = new PlayerOption(Path.GetFileName(storedPlayerPath), storedPlayerPath);
                _playerOptions.Add(storedOption);
                _playerPicker.Items.Insert(_playerPicker.Items.Count - 1, storedOption);
            }
            _playerPicker.SelectedItem = storedOption;
        }
        else
        {
            _playerPicker.SelectedIndex = 0;
        }
        _lastPlayerSelectionIndex = _playerPicker.SelectedIndex;
        UpdatePlayerPathDisplay();

        var storedArguments = _settingsStore.TryLoadVideoPlayerArguments(storedPlayerPath, out var savedArguments)
            ? savedArguments
            : GetDefaultArgumentsForPlayer(storedPlayerPath);
        _playerArguments.Text = storedArguments;
        _argumentPreset.SelectedIndex = GetArgumentPresetIndex(storedArguments);
        if (!_settingsStore.TryLoadVideoPlayerArguments(storedPlayerPath, out _))
            _settingsStore.SaveVideoPlayerArguments(storedPlayerPath, storedArguments);

        _playerCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    _playerTitle, _playerDescription,
                    _playerPicker,
                    new TextBlock { Text = "실행 파일", FontSize = 12, FontWeight = FontWeight.SemiBold },
                    _playerPath,
                    new TextBlock { Text = "실행 인수 형식", FontSize = 12, FontWeight = FontWeight.SemiBold },
                    _argumentPreset, _playerArguments,
                    new TextBlock { Text = "플레이어별로 인수를 따로 저장합니다. {file}=영상 경로, {position}=초, {position_hms}=시:분:초 (시청중 카드의 시청 위치)", TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                    _playerStatus
                }
            }
        };

        _playerPicker.SelectionChanged += async (_, _) => await PlayerPicker_SelectionChanged();
        _argumentPreset.SelectionChanged += (_, _) => ApplyArgumentPreset();
        _playerArguments.TextChanged += (_, _) =>
        {
            if (_updatingArguments) return;
            var index = GetArgumentPresetIndex(_playerArguments.Text ?? "");
            _argumentPreset.SelectedIndex = index < 0 ? 4 : index;
        };
        _playerArguments.LostFocus += (_, _) => SaveCurrentPlayerArguments();
        Closed += (_, _) => SaveCurrentPlayerArguments();

        _driveTitle = new TextBlock { Text = "Google Drive 동기화", FontSize = 16, FontWeight = FontWeight.SemiBold };
        _driveDescription = new TextBlock
        {
            Text = "Google Drive를 통한 기기 간 라이브러리 동기화 설정은 추후 추가할 예정입니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        _driveStatus = new TextBlock { Text = "준비 중", FontSize = 12, FontWeight = FontWeight.SemiBold };
        _driveAccount = new TextBox
        {
            PlaceholderText = "Google 계정 연결 (추후 추가)",
            MinHeight = 38,
            IsEnabled = false
        };
        _driveConnectButton = new Button { Content = "Google 계정 연결", MinHeight = 38, IsEnabled = false };
        _driveCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 9,
                Children = { _driveTitle, _driveDescription, _driveStatus, _driveAccount, _driveConnectButton }
            }
        };

        _themePicker.SelectionChanged += (_, _) =>
        {
            var useDarkTheme = _themePicker.SelectedIndex == 1;
            setTheme(useDarkTheme);
            ApplyDialogTheme(useDarkTheme);
        };

        var content = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(26),
            Children = { _titleText, _descriptionText, _themeCard, _playerCard, _driveCard }
        };
        Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        ApplyDialogTheme(isDarkTheme);
    }

    private void ApplyDialogTheme(bool isDark)
    {
        var background = Color.Parse(isDark ? "#171922" : "#F5F6FA");
        var cardBackground = Color.Parse(isDark ? "#242731" : "#FFFFFF");
        var border = Color.Parse(isDark ? "#414550" : "#D2D5DE");
        var primaryText = Color.Parse(isDark ? "#F1F2F7" : "#202230");
        var secondaryText = Color.Parse(isDark ? "#B4B7C4" : "#666A78");
        var inputBackground = Color.Parse(isDark ? "#1D2029" : "#FFFFFF");

        Background = new SolidColorBrush(background);
        _titleText.Foreground = new SolidColorBrush(primaryText);
        _descriptionText.Foreground = new SolidColorBrush(secondaryText);
        _themeTitle.Foreground = new SolidColorBrush(primaryText);
        _themeDescription.Foreground = new SolidColorBrush(secondaryText);
        _driveTitle.Foreground = new SolidColorBrush(primaryText);
        _driveDescription.Foreground = new SolidColorBrush(secondaryText);
        _driveStatus.Foreground = new SolidColorBrush(Color.Parse(isDark ? "#C8BFFF" : "#6045D4"));

        foreach (var card in new[] { _themeCard, _driveCard })
        {
            card.Background = new SolidColorBrush(cardBackground);
            card.BorderBrush = new SolidColorBrush(border);
        }

        _themePicker.Background = new SolidColorBrush(inputBackground);
        _themePicker.Foreground = new SolidColorBrush(primaryText);
        _themePicker.BorderBrush = new SolidColorBrush(border);
        _driveAccount.Background = new SolidColorBrush(inputBackground);
        _driveAccount.Foreground = new SolidColorBrush(secondaryText);
        _driveAccount.BorderBrush = new SolidColorBrush(border);
        _driveConnectButton.Background = new SolidColorBrush(isDark ? Color.Parse("#343744") : Color.Parse("#E6E7EC"));
        _driveConnectButton.Foreground = new SolidColorBrush(secondaryText);
        _playerTitle.Foreground = new SolidColorBrush(primaryText);
        _playerDescription.Foreground = new SolidColorBrush(secondaryText);
        _playerStatus.Foreground = new SolidColorBrush(Color.Parse(isDark ? "#C8BFFF" : "#6045D4"));
        _playerCard.Background = new SolidColorBrush(cardBackground);
        _playerCard.BorderBrush = new SolidColorBrush(border);
        foreach (var input in new TemplatedControl[] { _playerPicker, _playerPath, _argumentPreset, _playerArguments })
        {
            input.Background = new SolidColorBrush(inputBackground);
            input.Foreground = new SolidColorBrush(primaryText);
            input.BorderBrush = new SolidColorBrush(border);
        }
    }

    private async Task PlayerPicker_SelectionChanged()
    {
        if (_handlingPlayerSelection || _playerPicker.SelectedItem is not PlayerOption selected) return;
        SaveCurrentPlayerArguments();
        if (selected.IsAddCustom)
        {
            _handlingPlayerSelection = true;
            _playerPicker.SelectedIndex = _lastPlayerSelectionIndex;
            _handlingPlayerSelection = false;

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "플레이어 실행 파일 선택",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("실행 파일") { Patterns = ["*.exe"] }]
            });
            if (files.Count == 0) return;

            var path = files[0].Path.LocalPath;
            if (!File.Exists(path))
            {
                _playerStatus.Text = "선택한 실행 파일을 찾을 수 없습니다.";
                return;
            }
            var option = _playerOptions.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                option = new PlayerOption(Path.GetFileName(path), path);
                _playerOptions.Add(option);
                _playerPicker.Items.Insert(_playerOptions.Count - 1, option);
            }
            SelectPlayer(option);
            _playerStatus.Text = "플레이어를 선택하고 저장했습니다.";
            return;
        }

        SelectPlayer(selected);
        _playerStatus.Text = selected.Path.Length == 0
            ? "Windows 기본 연결 앱을 사용합니다."
            : "선택한 플레이어를 저장했습니다.";
    }

    private void SelectPlayer(PlayerOption option)
    {
        _handlingPlayerSelection = true;
        _playerPicker.SelectedItem = option;
        _lastPlayerSelectionIndex = _playerPicker.SelectedIndex;
        _handlingPlayerSelection = false;
        _settingsStore.SaveVideoPlayerPath(option.Path);
        _activeArgumentsPlayerPath = option.Path;
        UpdatePlayerPathDisplay();
        LoadCurrentPlayerArguments();
    }

    private void SaveCurrentPlayerArguments()
    {
        _settingsStore.SaveVideoPlayerArguments(_activeArgumentsPlayerPath, _playerArguments.Text ?? "");
    }

    private void LoadCurrentPlayerArguments()
    {
        if (_playerPicker.SelectedItem is not PlayerOption option) return;
        _activeArgumentsPlayerPath = option.Path;
        var arguments = _settingsStore.TryLoadVideoPlayerArguments(option.Path, out var savedArguments)
            ? savedArguments
            : GetDefaultArgumentsForPlayer(option.Path);
        _updatingArguments = true;
        _playerArguments.Text = arguments;
        _argumentPreset.SelectedIndex = GetArgumentPresetIndex(arguments);
        _updatingArguments = false;
        _settingsStore.SaveVideoPlayerArguments(option.Path, arguments);
    }

    private static string GetDefaultArgumentsForPlayer(string playerPath)
    {
        var executable = Path.GetFileName(playerPath);
        if (executable.Equals("vlc.exe", StringComparison.OrdinalIgnoreCase))
            return "--start-time={position} \"{file}\"";
        if (executable.StartsWith("PotPlayerMini", StringComparison.OrdinalIgnoreCase))
            return "\"{file}\" /seek={position_hms}";
        if (executable.Equals("mpv.exe", StringComparison.OrdinalIgnoreCase))
            return "--start={position} -- \"{file}\"";
        return "{file}";
    }

    private void UpdatePlayerPathDisplay()
    {
        _playerPath.Text = _playerPicker.SelectedItem is PlayerOption option && option.Path.Length > 0
            ? option.Path
            : "Windows 기본 연결 앱 사용";
    }

    private void ApplyArgumentPreset()
    {
        if (_argumentPreset.SelectedItem is not string preset || preset == "직접 입력") return;
        var arguments = preset switch
        {
            "VLC · 이어보기" => "--start-time={position} \"{file}\"",
            "PotPlayer · 이어보기" => "\"{file}\" /seek={position_hms}",
            "mpv · 이어보기" => "--start={position} -- \"{file}\"",
            _ => "{file}"
        };
        _updatingArguments = true;
        _playerArguments.Text = arguments;
        _updatingArguments = false;
        SaveCurrentPlayerArguments();
    }

    private static int GetArgumentPresetIndex(string arguments) => arguments switch
    {
        "{file}" => 0,
        "--start-time={position} \"{file}\"" => 1,
        "\"{file}\" /seek={position_hms}" => 2,
        "--start={position} -- \"{file}\"" => 3,
        _ => -1
    };

    private static IReadOnlyList<PlayerOption> DiscoverInstalledPlayers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new Dictionary<string, PlayerOption>(StringComparer.OrdinalIgnoreCase);
        var appPaths = new[]
        {
            ("vlc.exe", "VLC"), ("PotPlayerMini64.exe", "PotPlayer"), ("PotPlayerMini.exe", "PotPlayer"),
            ("mpv.exe", "mpv"), ("MPC-HC64.exe", "MPC-HC"), ("mpc-hc64.exe", "MPC-HC"),
            ("mpc-be64.exe", "MPC-BE"), ("KMPlayer64.exe", "KMPlayer"), ("GOM.exe", "GOM Player")
        };
        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in hives)
        foreach (var view in views)
        foreach (var (exeName, displayName) in appPaths)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                AddPlayer(found, displayName, key?.GetValue(null) as string);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }

        var recognizedNames = new[] { "vlc", "potplayer", "mpc-hc", "mpc be", "mpv", "kmplayer", "gom player" };
        foreach (var hive in hives)
        foreach (var view in views)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var app = uninstall.OpenSubKey(subKeyName);
                        var displayName = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || !recognizedNames.Any(name => displayName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;
                        var icon = app?.GetValue("DisplayIcon") as string;
                        var path = ExtractExecutablePath(icon);
                        if (path is null)
                        {
                            var installDirectory = app?.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(installDirectory))
                            {
                                path = appPaths.Select(candidate => Path.Combine(installDirectory, candidate.Item1)).FirstOrDefault(File.Exists);
                            }
                        }
                        AddPlayer(found, displayName, path);
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        }

        // Include apps Windows itself registered as handlers for MP4, even when they
        // are not among the common player names above.
        try
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using var mp4 = classes.OpenSubKey(".mp4");
            var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaultProgId = mp4?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(defaultProgId)) progIds.Add(defaultProgId);
            using (var userChoice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp4\UserChoice"))
            {
                var userProgId = userChoice?.GetValue("ProgId") as string;
                if (!string.IsNullOrWhiteSpace(userProgId)) progIds.Add(userProgId);
            }
            using (var openWith = classes.OpenSubKey(@".mp4\OpenWithProgids"))
                if (openWith is not null) progIds.UnionWith(openWith.GetValueNames());

            foreach (var progId in progIds)
            {
                using var commandKey = classes.OpenSubKey($@"{progId}\shell\open\command");
                var command = commandKey?.GetValue(null) as string;
                var appName = classes.OpenSubKey(progId)?.GetValue(null) as string;
                AddPlayer(found, string.IsNullOrWhiteSpace(appName) ? progId : appName, command);
            }

            using (var openWithList = classes.OpenSubKey(@".mp4\OpenWithList"))
            {
                if (openWithList is not null)
                foreach (var appExe in openWithList.GetSubKeyNames())
                {
                    using var commandKey = classes.OpenSubKey($@"Applications\{appExe}\shell\open\command");
                    AddPlayer(found, Path.GetFileNameWithoutExtension(appExe), commandKey?.GetValue(null) as string);
                }
            }

            using (var userOpenWithList = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.mp4\OpenWithList"))
            {
                if (userOpenWithList is not null)
                foreach (var valueName in userOpenWithList.GetValueNames().Where(name => !name.Equals("MRUList", StringComparison.OrdinalIgnoreCase)))
                {
                    var appExe = userOpenWithList.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(appExe)) continue;
                    using var commandKey = classes.OpenSubKey($@"Applications\{appExe}\shell\open\command");
                    AddPlayer(found, Path.GetFileNameWithoutExtension(appExe), commandKey?.GetValue(null) as string);
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
        return found.Values.OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static void AddPlayer(IDictionary<string, PlayerOption> found, string name, string? path)
    {
        var executable = ExtractExecutablePath(path);
        if (executable is null || !File.Exists(executable)) return;
        found.TryAdd(executable, new PlayerOption(Path.GetFileName(executable), executable));
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = Environment.ExpandEnvironmentVariables(value.Trim());
        if (candidate.StartsWith('"'))
        {
            var closingQuote = candidate.IndexOf('"', 1);
            if (closingQuote > 1) candidate = candidate[1..closingQuote];
        }
        else
        {
            var comma = candidate.IndexOf(',');
            if (comma >= 0) candidate = candidate[..comma];
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exeEnd = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeEnd >= 0) candidate = candidate[..(exeEnd + 4)];
            }
        }
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private sealed record PlayerOption(string Name, string Path, bool IsAddCustom = false)
    {
        public static PlayerOption AddCustom { get; } = new("직접 추가...", "", true);
        public override string ToString() => Name;
    }
}
