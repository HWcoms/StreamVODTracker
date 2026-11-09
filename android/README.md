# Android app

The Android host project is `VODTracker.Android/VODTracker.Android.csproj`. It is a .NET 10 Android app using Avalonia and references `../src/VODTracker.Core` for the shared Google Drive payload and merge policy.

Windows and Android do not connect to each other. The planned sync path is for each client to read and write the same `StreamVODTracker/StreamVODTracker.sync.json` file in the user's Google Drive. The current Android screen is only a host-project placeholder; Google OAuth and Drive file access are not wired yet.

## Build prerequisites

- .NET 10 SDK with the Android workload
- Android SDK and JDK configured in Rider
- Android emulator or USB debugging enabled on a device

Build and deploy from this directory with `dotnet build` or `dotnet run` after installing those prerequisites. The Android host has not yet been built in this workspace because its Android workload, SDK, and JDK are not installed here.

## Next Android work

1. Add Android-specific Google OAuth without embedding a client secret.
2. Confirm access to the existing Drive sync file under the app's `drive.file` authorization; do not silently create a second sync file when access is missing.
3. Wire the shared payload codec and merge rules to Drive download/upload.
4. Move remaining card and storage logic out of the Windows window code into shared Core services.
5. Add mobile navigation and platform-safe Android file URI handling.
