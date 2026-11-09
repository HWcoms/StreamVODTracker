# VODTracker 프로젝트 브리프 및 개발 사양

> Rider + Codex에서 프로젝트를 구현할 때 기준 문서로 사용한다. 요구사항을 임의로 축소하거나 아래 데이터 보존 규칙을 변경하지 않는다.

## 1. 제품 개요

VODTracker는 채널 VOD와 기기별 로컬 영상 참조를 관리한다. Windows와 Android는 각자 실행되며 사용자 Google Drive의 공통 메타데이터 JSON으로 동기화한다. 현재 구현은 Windows 데스크톱과 로컬 CSV, Windows OAuth/Drive 동기화다.

### 현재 데스크톱 구현 기준

현재 Windows 저장소는 앱 실행 폴더의 `db` 디렉터리에 있는 채널별 UTF-8 CSV 파일이다. CSV는 앱이 실행 시 읽고 변경 시 다시 기록하는 로컬 원본 데이터다. Android는 별도 호스트 프로젝트에서 같은 Drive JSON을 직접 동기화한다.

- CSV 식별과 파일명은 플랫폼 및 외부 `ChannelId`를 기준으로 한다. 한 파일에 채널 정보와 해당 채널 영상 정보를 저장한다.
- CSV에는 `IsLive`를 저장하지 않는다. 라이브 여부는 실행 중 API에서 확인하는 화면 상태다.
- 채널을 선택하기 전에는 영상 카드를 표시하지 않고 채널 선택/추가 안내를 표시한다. 채널 선택 후 다시보기 카드와 새로고침/추가 도구를 표시한다.
- 채널 이름, 설명, 프로필 이미지 주소/이미지 파일을 채널 ID로 조회해 앱 시작 때 갱신한다. 조회 또는 다운로드가 실패하면 마지막 CSV 값과 캐시된 프로필 이미지를 사용한다.
- 영상 썸네일과 채널 프로필 이미지는 실행 폴더의 `thumbnail/<플랫폼·ChannelId 해시>/` 아래에 캐시하며 CSV에 주소와 로컬 경로를 기록한다. 로그인 쿠키나 인증 토큰은 CSV/썸네일 폴더에 저장하지 않는다.
- 현재 영상 카드는 그리드/리스트 보기와 썸네일 표시 전환을 제공한다. 시청 상태는 `미시청`, `시청중`, `시청완료`로 관리한다.
- 현재 UI는 Avalonia 단독 데스크톱 앱이다. CSV 가져오기/내보내기 화면과 `.xlsx` 입출력은 아직 구현되지 않았다.

### 현재 화면 동작

- 왼쪽 내비게이션에 전체 보관함, 치지직, SOOP, 라이선스 화면과 채널 목록을 둔다. 채널은 채널 추가 창에서 ID 또는 전체 주소로 등록한다.
- 채널을 선택하지 않으면 영상 카드를 표시하지 않고 채널 선택/추가 안내를 표시한다. 선택 채널의 `다시보기 리스트` 제목 오른쪽에는 썸네일 표시, 그리드, 리스트, `＋ 추가`, 새로고침 도구를 둔다.
- 카드 그리드/리스트 보기, 썸네일 표시/숨김, 제목 및 링크 표시, 클릭으로 브라우저/로컬 파일 열기, `미시청`/`시청중`/`시청완료` 상태 변경을 지원한다.
- 플랫폼 강조 색상은 치지직과 SOOP을 구분한다. 선택된 채널/영상 카드 테두리와 선택 작업 버튼은 치지직 `#35B879`, SOOP `#398FE0`을 사용한다. 따라서 두 플랫폼의 테두리/선택 버튼 색은 같지 않다. 라이브 채널 아바타 테두리는 치지직 `#43CDB4`, SOOP `#55B8F2`를 사용한다.
- 플랫폼 공통 기본 버튼은 보라색 `#7156E8`, 마우스 오버는 `#6045D4`를 사용한다. 시청 상태 버튼 색은 미시청 `#D84040`, 시청중 `#D99B18`, 시청완료 `#2E9B57`이며 플랫폼에 따라 달라지지 않는다.
- 직접 추가 항목은 현재 URL 또는 파일 중 하나를 등록한다. URL과 파일을 한 항목에 함께 저장하는 동작은 [TODO.md](TODO.md)에 기록한 후속 작업이다.

### 지원 플랫폼 및 기술 기준

- Windows 데스크톱: C#, Avalonia UI, .NET SDK/runtime 10.0.4. Windows 전용 저장소·자격 증명·파일 실행은 플랫폼 어댑터로 둔다.
- Android: .NET Android 호스트와 Avalonia UI를 사용하고, 공통 카드·Drive 계약은 VODTracker.Core를 공유한다.
- 기기 간 동기화: PC 서버 없이 각 클라이언트가 Google Drive의 동일 JSON 파일을 직접 읽고 쓴다.
- 공통 저장 계약: Windows와 Android가 Drive JSON을 공유한다. SQLite 전환이나 PC API 서버는 Android 동기화의 전제가 아니다.
- Excel 계획: `.xlsx` 입출력이 구현될 때 워크시트 이름은 `치지직`, `soop` 두 개로 제한한다.
- 현재 저장소: 앱 실행 폴더의 `db/<플랫폼>_<ChannelId>_<식별해시>.csv` 파일을 채널별로 사용한다. 이미지 캐시 위치는 `thumbnail/<플랫폼·ChannelId 해시>/`다.

## 2. 범위와 주요 화면

### 화면 구성

1. 메인 화면에 `치지직` 탭, `soop` 탭, `내 영상` 탭을 둔다.
2. 온라인 플랫폼 탭에는 등록 채널과 해당 채널 VOD 목록, 새로고침, 채널 추가·수정·삭제 기능을 둔다.
3. `내 영상` 탭에는 사용자가 추가한 PC 로컬 영상 및 일반 URL 항목을 표시한다.
4. 공통 목록에서 제목/채널 검색, 시청 상태 필터, 정렬, 선택 항목 편집을 지원한다.
5. VOD/영상 상세 또는 편집 패널에서 메모, 상태, 이어보기 위치, 재생 링크/파일 경로를 수정한다.

### 추가 메뉴 및 재생 동작

- 메뉴 또는 추가 버튼에 다음 두 항목을 명확히 제공한다: `온라인 VOD 추가`, `PC 로컬 영상 추가`.
- 온라인 VOD 추가: 플랫폼, 채널(선택), 제목, URL, 게시 일시(선택)를 입력한다. URL은 HTTP/HTTPS만 허용한다. 재생 동작은 기본 브라우저에서 URL을 연다.
- PC 로컬 영상 추가: 파일 선택 대화상자로 파일을 선택하거나 경로를 입력한다. 경로와 표시 제목을 저장한다. 재생 시 구성된 외부 플레이어를 실행하고 저장된 이어보기 위치를 전달한다.
- 로컬 파일이 이동/삭제된 경우 데이터 행을 지우지 않는다. 파일 없음 상태를 표시하고 사용자가 경로를 다시 지정할 수 있게 한다.
- 플레이어 설정은 실행 파일 경로와 인자 템플릿을 포함한다. `{file}` 및 `{positionSeconds}` 같은 명시적 치환 변수만 지원한다. `cmd.exe /c`에 조합 문자열을 넘기지 않는다. 실행 파일과 각 인자를 `ProcessStartInfo`의 `FileName`, `ArgumentList`로 구성하고 `UseShellExecute=false`로 실행한다. 플레이어가 위치 인자를 지원하지 않으면 파일만 실행하고 앱 내 이어보기 위치는 계속 보존한다.
- 파일 경로/URL/프로세스 인자 로그에는 민감 경로·토큰을 불필요하게 남기지 않는다.

## 3. 핵심 데이터 보존 및 새로고침 규칙

- 각 채널 새로고침은 현재 연결된 플랫폼 API를 통해 조회 가능한 VOD를 가져온다. 현재 데스크톱 구현은 치지직/SOOP 비공식 API를 사용하며 응답이 실패해도 기존 CSV와 영상 목록을 삭제하지 않는다. provider 어댑터 분리는 후속 구조 개선 항목이다.
- 새로 발견한 VOD만 데이터베이스에 삽입(merge/upsert)한다. 플랫폼 응답에 없다는 이유로 기존 VOD를 삭제하거나 숨기거나 사용자 데이터를 초기화하지 않는다. 만료·비공개·삭제 가능성은 `availability` 및 `lastSeenAtUtc` 같은 플랫폼 관찰값으로만 기록한다.
- 안정적인 외부 VOD ID와 플랫폼을 복합 유일 키로 사용한다. 같은 VOD를 다시 발견해도 시청 상태, 메모, 이어보기 위치, 사용자 제목 수정값은 그대로 유지한다.
- 제공자 메타데이터 갱신은 플랫폼 제목/게시 일시 등 시스템 관리 필드에 한정한다. 사용자 편집 필드와 시청 데이터는 분리한다.
- 새로고침 실패/부분 실패 시 기존 목록은 그대로 표시하며 마지막 오류와 마지막 성공 갱신 시각을 사용자에게 알려준다. 실패 응답을 빈 목록으로 처리해선 안 된다.
- 치지직과 SOOP 외 플랫폼(예: YouTube)은 MVP에서 구현하지 않는다. 이후 추가를 고려해 provider 인터페이스를 둔다.
- 제공자 API 접근은 각 플랫폼의 현행 약관, 인증 방식, 호출 제한을 준수한다. 공식 API를 우선 사용하되, 사용자가 요청하거나 승인한 경우 출처가 확인되는 커뮤니티 비공식 API를 사용할 수 있다. 비공식 연동은 어댑터로 격리하고 변경 가능성 및 실패 시 수동 등록 경로를 유지한다. HTML 페이지 스크래핑은 사용하지 않는다. 로그인 쿠키 등 계정 인증정보는 명시적 설정 없이 수집하거나 전송하지 않는다.
- 채널 프로필 메타데이터(이름, 설명, 공개 이미지)는 앱 시작 때 `ChannelId`로 다시 조회한다. 성공한 필드만 CSV/cache에 갱신하고 조회 실패 시 기존 값을 유지한다. `IsLive`는 CSV에 보관하지 않는다.

## 4. 데이터 모델 및 SQLite 스키마 초안

> 아래 SQLite 스키마는 향후 로컬 데이터 저장소 전환을 검토할 때 사용할 설계 초안이다. Android 기기 간 동기화는 PC 서버나 서버 SQLite를 사용하지 않는다. 현재 데스크톱 앱은 로컬 CSV를 사용하며 이 스키마를 실행하지 않는다. 현재 CSV에서 `IsLive`를 저장하지 않는다.

시간은 UTC ISO-8601로 저장하고 화면에서 로컬 시간대로 표시한다. SQLite 외래 키를 활성화하고 스키마 변경은 버전이 있는 migration으로 관리한다.

### `Channels`

- `Id` TEXT PK (UUID)
- `Platform` TEXT NOT NULL (`chzzk` 또는 `soop`)
- `ExternalChannelId` TEXT NULL
- `DisplayName` TEXT NOT NULL
- `ChannelUrl` TEXT NULL
- `IsEnabled` INTEGER NOT NULL DEFAULT 1
- `CreatedAtUtc`, `UpdatedAtUtc` TEXT NOT NULL
- 인덱스/제약: `(Platform, ExternalChannelId)` 유일(외부 ID가 NULL이 아닐 때)

### `MediaItems`

- `Id` TEXT PK (UUID)
- `Kind` TEXT NOT NULL (`OnlineVod`, `LocalVideo`, `DirectUrl`)
- `Platform` TEXT NULL (`chzzk`, `soop`; 플랫폼 외 항목은 NULL)
- `ExternalVodId` TEXT NULL
- `ChannelId` TEXT NULL FK → `Channels.Id`, 삭제 시 NULL 처리
- `Title` TEXT NOT NULL
- `SourceUrl` TEXT NULL
- `LocalPath` TEXT NULL
- `PublishedAtUtc` TEXT NULL
- `DurationSeconds` INTEGER NULL
- `Availability` TEXT NOT NULL DEFAULT `Unknown` (`Available`, `Missing`, `Unknown`, `Unavailable`)
- `FirstSeenAtUtc`, `LastSeenAtUtc` TEXT NULL
- `CreatedAtUtc`, `UpdatedAtUtc` TEXT NOT NULL
- 유일 제약: `(Platform, ExternalVodId)` (외부 ID가 있는 플랫폼 VOD에 한함)
- 검증: `OnlineVod`는 유효한 HTTP/HTTPS URL 또는 플랫폼 ID를 가지며, `LocalVideo`는 `LocalPath`를 가진다.

### `WatchRecords`

- `MediaItemId` TEXT PK/FK → `MediaItems.Id` (항목당 현재 상태 1개)
- `WatchStatus` TEXT NOT NULL DEFAULT `Unwatched` (`Unwatched`, `Watching`, `Watched`)
- `PositionSeconds` REAL NOT NULL DEFAULT 0
- `CompletedAtUtc` TEXT NULL
- `UpdatedAtUtc` TEXT NOT NULL
- `RowVersion` INTEGER NOT NULL DEFAULT 1 (Android 동시 수정 충돌 탐지)

### `Notes`

- `Id` TEXT PK (UUID)
- `MediaItemId` TEXT NOT NULL FK → `MediaItems.Id` ON DELETE CASCADE
- `Body` TEXT NOT NULL
- `CreatedAtUtc`, `UpdatedAtUtc` TEXT NOT NULL
- MVP는 항목당 메모 1개 UI를 제공해도 스키마는 복수 메모 확장이 가능하게 한다.

### `RefreshRuns`

- `Id` TEXT PK (UUID)
- `ChannelId` TEXT NOT NULL FK → `Channels.Id`
- `StartedAtUtc`, `FinishedAtUtc` TEXT NULL
- `Status` TEXT NOT NULL (`Running`, `Succeeded`, `Failed`, `Partial`)
- `NewItemsCount` INTEGER NOT NULL DEFAULT 0
- `ErrorSummary` TEXT NULL (토큰/개인정보 제외)

### `AppSettings`

- `Key` TEXT PK
- `Value` TEXT NULL
- 설정 예: API bind 주소/포트, 플레이어 실행 파일, 인자 템플릿, Android 접근 허용 여부.
- 자격 증명/토큰이 필요하면 평문 SQLite나 로그에 저장하지 말고 Windows 보호 저장소를 사용한다.

## 5. Excel 및 CSV 파일 규칙

### Excel

- 내보내는 `.xlsx`에 워크시트 이름을 정확히 `치지직`, `soop` 두 개만 만든다.
- 각 시트에는 해당 플랫폼 VOD 행을 출력한다. 플랫폼에서 사라진 VOD도 기존 SQLite 이력에서 계속 출력한다.
- 권장 열: `항목ID`, `VODID`, `채널`, `제목`, `URL`, `게시일시(UTC)`, `시청상태`, `이어보기(초)`, `메모`, `가용성`, `처음확인(UTC)`, `마지막확인(UTC)`.
- 로컬 영상/직접 URL은 이 두 플랫폼 시트에 섞지 않는다. 일반 CSV 내보내기에서는 포함 여부를 선택할 수 있다.
- 가져오기는 `항목ID` 우선, 없으면 `(플랫폼, VODID)`로 대상을 찾는다. 허용된 사용자 필드(시청 상태, 진행 위치, 메모)를 갱신하고, 외부 ID가 없는 새 행은 미리보기 후 추가한다. 빈 셀이나 행 누락만으로 기존 값/레코드를 삭제하지 않는다.
- 가져오기 전 검증 및 변경 요약 미리보기를 표시한다. 잘못된 행은 행 번호와 오류를 보고하며 정상 행과 구분한다.

### CSV

- UTF-8(BOM 포함 권장), 헤더 포함, RFC 4180 호환 quoting을 사용한다. 쉼표/따옴표/개행이 포함된 제목과 메모를 올바르게 처리한다.
- 현재 데스크톱 앱은 `db` 폴더의 채널별 CSV를 자동으로 읽고 갱신한다. 각 파일은 채널 정보 행과 영상 행을 포함하며 외부 채널 ID로 식별한다. 현재 헤더는 `RecordType`, `Platform`, `ChannelId`, `ChannelName`, `ChannelDescription`, `ChannelUrl`, `ChannelProfileImageUrl`, `ChannelProfileImagePath`, `Title`, `SourceType`, `SourceLabel`, `SourceValue`, `PublishedAtUtc`, `WatchStatus`, `ThumbnailUrl`, `ThumbnailPath`, `Availability`다. `IsLive`는 포함하지 않는다.
- `thumbnail` 폴더에는 채널 프로필 이미지 및 영상 썸네일 캐시를 저장한다. CSV는 원본 이미지 URL과 캐시 경로를 보관한다. API 조회 실패 시 캐시된 CSV/이미지를 우선 사용한다.
- 앱 안의 CSV 가져오기/내보내기 및 `.xlsx` 기능은 아직 구현되지 않았다. 아래 Import/Export 요구사항은 후속 기능 기준이다.
- (후속 요구사항) Import/Export 메뉴를 제공하고, 플랫폼별 또는 전체 선택 내보내기를 지원한다.
- (후속 요구사항) 가져오기 역시 Excel과 같은 키 우선순위 및 사용자 필드 보호 규칙을 적용한다.
- (후속 요구사항) 가져오기는 트랜잭션으로 수행하고 dry-run 미리보기 후 사용자가 적용한다. 입력값을 수식으로 실행하지 않는다(스프레드시트 수식 주입 방지).

## 6. 솔루션 구조와 플랫폼 경계

```text
StreamVODTracker.sln
StreamVODTracker/                 # Windows 데스크톱 앱 (Avalonia)
src/
  VODTracker.Core/                # 공통 Drive 모델, JSON 계약, 병합 규칙
  VODTracker.Domain/              # 도메인 모델
  VODTracker.Application/          # 유스케이스와 포트
  VODTracker.Infrastructure/      # 저장소 구현
  VODTracker.Contracts/            # API 계약(PC/Android 동기화 경로에는 사용하지 않음)
  VODTracker.Api/                  # 별도 실험/확장용이며 Android가 의존하지 않음
android/VODTracker.Android/       # .NET Android + Avalonia 호스트 프로젝트
```

- Windows 및 Android 플랫폼 앱은 공통 Core를 참조한다. Core는 OS 전용 UI, Credential Manager, 파일 경로, Android URI, OAuth UI를 참조하지 않는다.
- Desktop/Android 간 통신은 없다. Drive JSON이 동기화 경로다. `VODTracker.Api`가 존재하더라도 Android가 PC API에 연결하거나 PC 실행을 요구하지 않는다.
- 플랫폼별 OAuth와 파일 접근은 플랫폼 호스트 서비스로 구현한다. Android용 OAuth Client ID와 Windows Desktop OAuth Client ID는 분리하고 Android 앱에 client secret을 넣지 않는다.
## 7. Android 앱 구조

Android는 .NET Android 호스트와 Avalonia UI로 구현한다. Windows와 Android가 공유할 플랫폼 중립 카드 모델·Drive DTO·병합 규칙은 VODTracker.Core에 둔다. 두 앱은 서로 직접 연결하지 않으며, 각 앱이 사용자의 Google Drive에 있는 동일한 동기화 JSON 파일을 직접 읽고 쓴다. PC가 꺼져 있어도 Android의 Drive 동기화와 카드 조회·수정은 가능해야 한다.

```text
android/VODTracker.Android/
  VODTracker.Android.csproj  # net10.0-android host, references VODTracker.Core
  MainActivity.cs            # Android launch activity
  MainApplication.cs         # Avalonia Android application
  AndroidApp.axaml           # Android app lifetime and theme
  AndroidHomeView.cs         # initial mobile host screen
src/VODTracker.Core/         # shared Drive payload, codec, and merge rules
```

- Drive 파일은 `StreamVODTracker/StreamVODTracker.sync.json`을 공유한다. Android OAuth는 Android 전용 Client ID를 사용하고 클라이언트 시크릿을 APK에 포함하지 않는다. OAuth/파일 선택·앱 데이터 경로 같은 OS 전용 구현은 공통 Core 밖의 플랫폼 서비스로 둔다.
- 현재 Desktop은 Drive `drive.file` 범위로 앱 폴더와 JSON 파일을 검색한다. Android도 동일 파일을 발견하고 편집할 수 있는지 OAuth 클라이언트를 준비해 실제 계정으로 확인한다. 필요한 경우 사용자에게 기존 파일 접근 권한을 명시적으로 부여하는 파일 선택 흐름을 사용한다. 파일을 못 찾았다는 이유로 별도 동기화 파일을 자동 생성하면 안 된다.
- Android는 온라인 URL을 Android 브라우저 또는 플랫폼 앱에서 연다. 로컬 파일은 Android에서 선택한 파일 URI로 열며, Android는 Windows 파일 경로를 열거나 수정하지 않는다.
- Drive 파일은 오프라인/백그라운드 동기화를 보장하지 않는다. 앱 실행·화면 활성화·사용자 동기화 및 OS가 허용하는 백그라운드 작업에서 원격 변경을 확인한다.

## 8. Google Drive 동기화 계약

Windows와 Android는 Drive API를 통해 동일한 JSON 파일을 교환한다. 별도 PC API 서버, PC 주소 입력, PC 전원 상태를 동기화 전제로 삼지 않는다. 현재 파일은 `version: 1`, `updatedAt`, `channels[]` 구조이며 각 채널에는 `channel`과 `media[]`가 들어간다. 플랫폼 계약을 바꾸기 전에 기존 Windows 파일과의 역호환을 확인한다.

- 동기화 키는 `Platform + ChannelId` 및 `Platform + VodId`를 사용한다.
- Android가 카드 상태나 메모를 저장할 때 Windows 로컬 영상 경로와 썸네일 캐시 경로를 삭제하거나 덮어쓰지 않는다.
- 현재 `LocalFilePath`는 Windows 경로다. Android 영상 참조를 추가할 때 `DesktopLocalFilePath`와 Android 기기별 `AndroidLocalFileUri` 같은 분리 필드를 도입하고, 기존 파일을 안전하게 마이그레이션한다. 영상 파일 자체는 Drive 동기화 JSON에 올리지 않는다.
- 현재 병합은 카드 레코드 전체에 대해 `UpdatedAt`이 최신인 쪽을 고른다. Android 추가 전에 사용자 필드와 기기별 파일 참조를 독립적으로 병합하고, 삭제 tombstone 및 동시 수정 충돌 정책을 정한다.
- 하나의 JSON 파일을 여러 기기가 동시에 덮어쓰면 변경이 유실될 수 있다. 업로드 전 `modifiedTime` 재확인, 충돌 재병합·재시도 등 파일 갱신 충돌을 다룬다.
- Android 백그라운드 동기화는 WorkManager 등 OS 제약을 고려하며 실시간 동기화를 약속하지 않는다.

## 9. Google OAuth 및 Drive 권한

- Android OAuth Client ID는 Windows Desktop Client ID와 별도로 만든다. 두 클라이언트는 같은 Google Cloud 프로젝트와 OAuth 동의 화면을 사용한다. Android 앱에 Client Secret, 서비스 계정 키, refresh token을 포함하지 않는다.
- 가능한 최소 Drive 권한을 사용하고, `drive.file`에서 기존 Windows 생성 파일을 Android 클라이언트가 읽고 수정할 수 있는지 실제 OAuth 계정으로 검증한다. 현재 Desktop과 Android가 파일을 공유하려면 앱 생성·파일 선택에 따른 파일별 권한 동작을 확인해야 한다.
- Drive refresh token 및 계정 연결 해제·재연결은 Android의 안전한 자격 증명 저장소와 Google Authorization API 흐름에 맞춘다.
- Google Cloud 프로젝트 준비, Drive API 활성화, Android 패키지명·서명 인증서 등록, OAuth 동의 화면 및 공개 배포 검증 절차를 문서화한다. 앱은 사용자가 별도 Cloud 프로젝트를 만들도록 요구하지 않는다.

## 10. 단계별 멀티플랫폼 계획

### 0단계 — 공통 데이터와 저장 규약

- Desktop Drive JSON 구조, 필드별 병합 규칙, 버전 변경과 구버전 호환을 명확히 한다.
- 기기별 로컬 파일 참조와 동기화 충돌 정책을 정하고 Windows 동기화 구현이 모르는 필드를 보존하도록 준비한다.

### 1단계 — Windows 단독 사용

- Avalonia 데스크톱 앱은 로컬 CSV와 Windows Credential Manager를 사용한다.
- Desktop은 사용자 Google Drive에 메타데이터 JSON을 직접 동기화한다.

### 2단계 — Android와 Drive 직접 동기화

- .NET Android 호스트와 Android OAuth Client, Google Drive 인증·파일 읽기/쓰기 및 Core 공통 JSON 직렬화 연결.
- Drive 연결, 데이터 읽기, 사용자 상태·메모 수정, 병합 후 업로드를 검증한다. Windows 앱이나 PC 서버가 꺼진 상태에서도 동작해야 한다.
- 플랫폼 화면, 검색, 온라인 URL 열기, Android 로컬 파일 URI 저장을 구현한다.
- 백그라운드 동기화 제한, 토큰 보안, 동일 파일의 동시 갱신을 처리한다.

## 11. 검증 시나리오

1. Windows가 Drive에 만든 `StreamVODTracker.sync.json`을 Android에서 같은 계정으로 찾아 읽는다. 접근할 수 없으면 파일별 OAuth 권한/선택 흐름을 확인하며 중복 파일을 만들지 않는다.
2. Android에서 시청 상태나 메모를 바꾸고 업로드한 후 Windows가 다운로드했을 때 변경값이 보존된다.
3. Windows가 꺼져 있어도 Android가 Drive 데이터의 읽기·수정·업로드를 완료한다.
4. Android 변경은 Windows의 로컬 파일 경로 및 Windows 전용 캐시 경로를 보존한다. Android URI도 다른 기기의 Android 참조를 덮어쓰지 않는다.
5. 두 기기가 오프라인에서 같은 카드의 서로 다른 필드를 바꾼 뒤 온라인으로 돌아왔을 때 필드별 병합이 보존되고, 같은 필드 충돌은 정책에 따라 감지·해결된다.
6. 동기화 중 네트워크 오류나 앱 종료가 발생해도 마지막 정상 Drive 파일과 로컬 데이터가 손상되지 않는다.
7. 앱 비활성 또는 OS 백그라운드 제한 중 동기화가 지연될 수 있으며, 재실행/활성화 시 원격 변경을 다시 확인한다.
## 12. Rider + Codex 작업 지침

- 이 문서를 기준 사양으로 삼고, 구현 전 현재 솔루션 구조와 변경 사항을 먼저 살핀다.
- 작업을 작고 검토 가능한 단계로 나누고, 한 번에 전체 앱을 생성하거나 요구사항을 임의 생략하지 않는다.
- 새 라이브러리/버전을 추가하기 전 .NET 10.0.4 및 .NET Android 호환성을 확인하고 중앙 버전 관리를 유지한다.
- 사용자 데이터 보존 규칙을 최우선으로 한다. VOD 목록 refresh는 삭제 동기화가 아니며 기존 watch record/note/progress를 절대 초기화하지 않는다.
- 플랫폼별 API 규칙이 확인되지 않으면 임의 엔드포인트를 추측해 구현하지 말고 수동 등록 경로를 남긴다.
- 프로세스 실행 시 문자열 명령 조합이나 `cmd.exe`를 사용하지 않는다. URL, 파일 경로, CSV 입력값을 검증한다.
- API/DB 계약을 바꾸면 migration, DTO, UI, Android 소비 부분을 함께 확인하고 관련 문서를 갱신한다.
- 변경 후 사용자가 요청한 검증 범위에 따라 빌드/테스트 또는 핵심 수동 시나리오를 실행하고 결과와 제한 사항을 보고한다.
- 커밋 전 변경 파일과 데이터 삭제 가능성이 있는 동작을 검토한다. 사용자의 요청 없이 외부 게시, 배포, 데이터 삭제를 수행하지 않는다.

## 13. 초기 Codex 프롬프트 예시

```text
PROJECT-BRIEF.md를 읽고 사양을 기준으로 작업해 주세요. 우선 저장소 상태와 현재 솔루션 구성을 확인한 뒤, 0단계 기반 작업만 계획하고 구현하세요. .NET SDK 10.0.4, C# Avalonia UI, ASP.NET Core API, SQLite 구성을 사용하고 WPF는 사용하지 마세요. 변경 전 확인할 점이 있으면 사양의 사용자 데이터 보존 규칙을 우선하세요. 완료 후 수정 파일, 설계 결정, 실행한 검증과 남은 항목을 요약해 주세요.
```
