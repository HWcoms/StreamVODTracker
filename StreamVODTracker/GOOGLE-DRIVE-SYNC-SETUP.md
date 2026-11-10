# Google Drive sync setup

The app creates one `StreamVODTracker.sync.json` metadata file in the signed-in user's own Google Drive. The file contains channel and VOD metadata only. Thumbnail URLs, thumbnail image paths, profile-image URLs, and profile-image paths are removed before serialization and are never uploaded. The existing local CSV library and locally cached images remain local.

## One-time OAuth setup (app developer)

1. Create a Google Cloud project and enable the Google Drive API.
2. Configure the OAuth consent screen for `openid`, `email`, and `https://www.googleapis.com/auth/drive.file`. Add test users while the consent screen is in Testing; publish it before general distribution.
3. Create a **Desktop app** OAuth client for Windows. Put its Client ID in Windows Settings. If the token endpoint requires it, put the matching client secret there too.
4. Create one **Android** OAuth client in the same Cloud project for application ID `com.hwcoms.streamvodtracker` and the SHA-1 of the key used to sign the app users install. To avoid separate debug and release registrations, install and distribute Release-signed builds for testing too. Do not embed a client secret in the Android app. Android obtains Drive access tokens through Google Play services.
5. Each user connects their own Google account on each device. Windows stores its refresh token in Windows Credential Manager. Android stores only the selected account email in app preferences; Google Play services manages the authorization grant and access-token renewal.

## Android release signing

Create one long-lived release keystore outside the repository and keep a secure backup. Never commit or share the keystore or its passwords. For example, create it under `%USERPROFILE%\.android` with `keytool -genkeypair -v -keystore "%USERPROFILE%\.android\streamvodtracker-release.jks" -alias streamvodtracker -keyalg RSA -keysize 2048 -validity 10000` and set a strong password when prompted.

Set these environment variables in the shell before building; the project reads them only for Release builds:

- `STREAMVODTRACKER_KEYSTORE`: full path to the `.jks` file
- `STREAMVODTRACKER_KEY_ALIAS`: `streamvodtracker` (or the alias chosen above)
- `STREAMVODTRACKER_KEYSTORE_PASSWORD`: keystore password
- `STREAMVODTRACKER_KEY_PASSWORD`: key password

Build a release APK with `dotnet publish android/VODTracker.Android/VODTracker.Android.csproj -c Release -f net10.0-android`. Install that APK on test devices and distribute the same release-signed artifact; all users then share the one registered package/SHA-1 pair. Debug builds remain signed with the Android debug key and will not match this OAuth registration.

If publishing through Google Play, test through Play's internal testing track so Play delivers the same app-signing certificate used for release. Register the Play app-signing SHA-1 shown in Play Console. Play's upload certificate is different and is not the certificate on installed Play builds. Google documents the distinction in [Client authentication](https://developers.google.com/android/guides/client-auth) and [Play App Signing](https://support.google.com/googleplay/android-developer/answer/9842756).

## How sync behaves

The app merges local and Drive records by channel and VOD ID, keeping the record with the newest `UpdatedAt`, then uploads the merged metadata file. Local changes upload after a short debounce. Windows checks Drive's `modifiedTime` at its configured interval (90 seconds by default). Android checks every 90 seconds while active. Both clients sync at startup and when the user requests a sync in Settings. Each user's file is created under the Google account that authorized the app, so Drive storage is charged to that user's Drive. Google Drive API request quotas also apply to the OAuth Cloud project.

Images are intentionally excluded to keep Drive storage small. Deletions made while both devices are connected are reflected on the next upload. An offline deletion has no tombstone yet and may return when the other device syncs.
