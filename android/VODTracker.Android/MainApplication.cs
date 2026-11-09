using Android.App;
using Avalonia;
using Avalonia.Android;

namespace VODTracker.Android;

[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<AndroidApp>
{
    public MainApplication(nint javaReference, global::Android.Runtime.JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
