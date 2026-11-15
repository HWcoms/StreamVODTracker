# StreamVODTracker

치지직(CHZZK)과 SOOP 채널의 VOD 및 개인 영상 목록을 관리하는 Windows·Android 앱입니다. 시청 상태와 메모를 기록하고, Google Drive를 통해 기기 간 보관함을 동기화할 수 있습니다.

## 화면 미리보기

<img width="1282" height="852" alt="StreamVODTracker 화면 미리보기" src="https://github.com/user-attachments/assets/69a39659-7f29-4e0b-a39d-2d6778a4e97c" />

## 주요 기능

- 치지직 및 SOOP 채널과 채널별 VOD 목록 관리
- VOD 시청 상태, 메모, 즐겨찾기 및 이어보기 위치 기록
- 개인 영상 파일과 VOD 링크 추가
- 채널 및 영상 데이터의 로컬 저장
- Google Drive를 이용한 Windows·Android 보관함 동기화

## 다운로드

[GitHub Releases에서 최신 배포 파일 보기](https://github.com/HWcoms/StreamVODTracker/releases)

- **Windows x64:** `StreamVODTracker-win-x64.zip`을 내려받아 압축을 풀고 `VODTracker.Desktop.exe`를 실행하세요. self-contained 빌드는 .NET 런타임을 별도로 설치하지 않아도 됩니다.
- **Android ARM64 (`arm64-v8a`):** APK를 내려받아 설치하세요. 설치를 위해 휴대폰 설정에서 해당 앱의 알 수 없는 앱 설치를 허용해야 할 수 있습니다.

## 소스에서 실행하기

### Windows

.NET 10 SDK가 필요합니다. 저장소 루트에서 실행하세요.

```powershell
dotnet run --project StreamVODTracker/StreamVODTracker.csproj
```

Windows x64 배포 파일은 다음 명령으로 생성할 수 있습니다.

```powershell
dotnet publish StreamVODTracker/StreamVODTracker.csproj -c Release -r win-x64 --self-contained true -o StreamVODTracker/bin/Release/win-x64
```

### Android

.NET 10 SDK, Android workload, Android SDK 및 JDK가 필요합니다. ARM64 APK는 다음 명령으로 생성할 수 있습니다.

```powershell
dotnet publish android/VODTracker.Android/VODTracker.Android.csproj -c Debug -f net10.0-android -r android-arm64 -p:AndroidPackageFormat=apk
```

채널 VOD 연동은 각 서비스의 API에 의존하므로, 서비스 변경에 따라 동작이 달라질 수 있습니다.

## 라이선스

MIT 라이선스입니다. [LICENSE](LICENSE)와 [서드파티 라이선스 안내](StreamVODTracker/THIRD-PARTY-LICENSES.md)를 확인하세요.
