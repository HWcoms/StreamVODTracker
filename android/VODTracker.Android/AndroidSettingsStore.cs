namespace VODTracker.Android;

/// <summary>Android-local settings. OAuth tokens will use Android Keystore-backed storage separately.</summary>
internal sealed class AndroidSettingsStore
{
    private const string PreferencesName = "stream_vod_tracker_settings";
    private const string OAuthClientIdKey = "google_drive_android_client_id";
    private const string DarkThemeKey = "appearance_dark_theme";

    private readonly global::Android.Content.ISharedPreferences _preferences =
        global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, global::Android.Content.FileCreationMode.Private)!;

    public string LoadGoogleOAuthClientId() => _preferences.GetString(OAuthClientIdKey, string.Empty) ?? string.Empty;

    public bool LoadIsDarkTheme() => _preferences.GetBoolean(DarkThemeKey, false);

    public void SaveIsDarkTheme(bool isDark)
    {
        var editor = _preferences.Edit() ?? throw new InvalidOperationException("Android settings are unavailable.");
        editor.PutBoolean(DarkThemeKey, isDark);
        editor.Apply();
    }

    public void SaveGoogleOAuthClientId(string clientId)
    {
        var editor = _preferences.Edit() ?? throw new InvalidOperationException("Android settings are unavailable.");
        editor.PutString(OAuthClientIdKey, clientId.Trim());
        editor.Apply();
    }
}
