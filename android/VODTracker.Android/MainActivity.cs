using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace VODTracker.Android;

[Activity(
    Label = "Stream VOD Tracker",
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
