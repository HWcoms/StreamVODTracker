using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VODTracker.Android;

public sealed partial class AndroidApp : Avalonia.Application
{
    internal static AndroidHomeView? MainView { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = new AndroidSettingsStore();
        RequestedThemeVariant = settings.LoadIsDarkTheme()
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            MainView = new AndroidHomeView(settings);
            singleView.MainView = MainView;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
