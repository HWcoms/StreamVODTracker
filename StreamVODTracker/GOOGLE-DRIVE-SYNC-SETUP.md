# Google Drive sync setup

The app creates one `StreamVODTracker.sync.json` metadata file in the signed-in user's own Google Drive. The file contains channel and VOD metadata only. Thumbnail URLs, thumbnail image paths, profile-image URLs, and profile-image paths are removed before serialization and are never uploaded. The existing local CSV library and locally cached images remain local.

## One-time OAuth setup (app developer)

1. Create a Google Cloud project and enable the Google Drive API.
2. Configure the OAuth consent screen as **External**, request only `openid`, `email`, and `https://www.googleapis.com/auth/drive.file`, then publish the consent screen to **In production**. While it is in Testing, Google refresh tokens for Drive access expire after seven days.
3. Create an OAuth client of type **Desktop app** and paste its Client ID into the app's Settings. The same app Client ID is used by all installations; users do not create their own client IDs.
4. Each user connects their own Google account once on each device. The refresh token is saved in Windows Credential Manager, not the password. On later starts the app refreshes the token and syncs automatically.

## How sync behaves

The app merges local and Drive records by channel and VOD ID, keeping the record with the newest `UpdatedAt`, then uploads the merged metadata file. Local changes upload after a short debounce. Remote changes are pulled at startup or when the user clicks **Sync with saved Google account**. Each user's file is created under the Google account that authorized the app, so Drive storage is charged to that user's Drive. Google Drive API request quotas also apply to the OAuth Cloud project; the API has per-project and per-user-per-project limits.

Images are intentionally excluded to keep Drive storage small. Deletions made while both devices are connected are reflected on the next upload. An offline deletion has no tombstone yet and may return when the other device syncs.