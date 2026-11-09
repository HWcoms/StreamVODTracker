using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace VODTracker.Android;

[Activity(
    Label = "Stream VOD Tracker",
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    public override void OnBackPressed()
    {
        if (AndroidApp.MainView?.HandleSystemBack() == true) return;
        base.OnBackPressed();
    }
}
