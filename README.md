# StreamVODTracker

A Windows desktop application for managing online VODs and offline Video Files from selected channels on streaming platforms such as CHZZK and SOOP, with watch history tracking (Manual).

## Preview
<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/7508a3bb-d927-4bb6-92a6-53386093f875" />

## Features

- Add channels and refresh their VOD lists
- Browse videos in grid or list view and track watch status
- Add VOD links or local video files
- Store channel and video data in local CSV files

## Run

Requires the .NET 10 SDK. From the repository root:

```powershell
dotnet run --project StreamVODTracker/StreamVODTracker.csproj
```

Channel integrations use community APIs and may change if those services are updated.

## License

MIT. See [LICENSE](LICENSE) and [third-party notices](StreamVODTracker/THIRD-PARTY-LICENSES.md).
