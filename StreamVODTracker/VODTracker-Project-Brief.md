# VODTracker 프로젝트 브리프 및 개발 사양

> Rider + Codex에서 프로젝트를 구현할 때 기준 문서로 사용한다. 요구사항을 임의로 축소하거나 아래 데이터 보존 규칙을 변경하지 않는다.

## 1. 제품 개요

VODTracker는 사용자가 등록한 채널의 VOD와 개인 PC의 로컬 영상을 함께 관리하는 개인용 앱이다. 장기 목표는 시청 정보와 Android 연동을 PC 서버/SQLite로 제공하는 것이지만, 현재는 Windows 단독 앱과 로컬 CSV 저장소를 구현하고 있다.

### 현재 데스크톱 구현 기준

현재 작업 기준의 저장소는 서버/SQLite가 아니라 앱 실행 폴더의 `db` 디렉터리에 있는 채널별 UTF-8 CSV 파일이다. 서버와 SQLite는 후속 단계이며 지금 동작하는 기능으로 간주하지 않는다. CSV는 앱이 실행 시 읽고 변경 시 다시 기록하는 로컬 원본 데이터다.

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
- 직접 추가 항목은 현재 URL 또는 파일 중 하나를 등록한다. URL과 파일을 한 항목에 함께 저장하는 동작은 [NEXT-TASKS.md](NEXT-TASKS.md)에 기록한 후속 작업이다.

### 지원 플랫폼 및 기술 기준

- Windows 데스크톱: C#, Avalonia UI, .NET SDK/runtime **10.0.4** 기준으로 구성한다. WPF를 사용하지 않는다.
- Android: Kotlin 앱. 우선 네이티브 Android UI 및 HTTP 클라이언트를 사용한다.
- 서버: Windows 앱과 같은 PC에서 실행하는 ASP.NET Core Web API.
- 장기 저장소 계획: 서버 측 SQLite를 후속 단계에서 검토한다. Android 서버 동기화와 오프라인 우선 동기화도 현재 구현 범위가 아니다.
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

> 아래 SQLite 스키마는 서버 저장소를 도입할 때 사용할 장기 설계 초안이다. 현재 데스크톱 앱은 로컬 CSV를 사용하며 이 스키마를 실행하지 않는다. 현재 CSV에서 `IsLive`를 저장하지 않는다.

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

## 6. Windows 프로젝트 구조

```text
VODTracker/
  VODTracker.sln
  global.json
  Directory.Build.props
  src/
    VODTracker.Desktop/          # Avalonia UI, ViewModels, Windows 진입점
    VODTracker.Application/      # 유스케이스, DTO, 검증, 서비스 인터페이스
    VODTracker.Domain/           # 엔티티, 값 타입, 도메인 규칙
    VODTracker.Infrastructure/   # EF Core/SQLite, migrations, provider, 파일/프로세스
    VODTracker.Api/              # ASP.NET Core API, 인증/로컬 네트워크 호스트
    VODTracker.Contracts/        # API 요청/응답 계약
  tests/
    VODTracker.Domain.Tests/
    VODTracker.Application.Tests/
    VODTracker.Api.Tests/
    VODTracker.Infrastructure.Tests/
  android/
    app/                         # Kotlin Android 앱
  docs/
    PROJECT-BRIEF.md             # 이 사양을 프로젝트 루트에도 복사해 유지
```

- 의존 방향은 `Desktop/Api → Application → Domain`, `Infrastructure → Application/Domain`으로 유지한다. Contracts는 Desktop/Android와 API 사이 HTTP 모델만 담는다.
- 버전은 중앙 관리(`Directory.Packages.props`)한다. `global.json`에서 SDK 10.0.4를 고정하고 roll-forward를 명시적으로 결정한다. CI/개발 환경에서 해당 SDK 설치 여부를 확인한다.
- 플랫폼별 VOD 검색/동기화 코드는 `IVodProvider`와 `ChzzkVodProvider`, `SoopVodProvider` 구현체로 나눈다.
- Windows API 서버는 데스크톱 앱과 같은 PC에서 시작/정지할 수 있게 하며, API 유스케이스와 데이터베이스 로직을 UI에 두지 않는다.

## 7. Android 앱 구조

```text
android/app/src/main/java/<package>/
  data/        # Retrofit/Ktor API, DTO, repository
  domain/      # 모델 및 사용 사례
  ui/          # 치지직/soop/내 영상 화면, 상세/편집 화면
  settings/    # PC 서버 주소와 연결 상태
```

- 첫 실행 시 PC 서버 주소와 포트를 입력/저장하고 연결 상태를 점검한다.
- 온라인 항목의 URL은 Android 브라우저/앱 링크로 연다. Android 클라이언트는 PC의 로컬 파일 경로를 직접 재생할 수 없으므로 로컬 항목에서는 메타데이터와 이어보기 정보를 보여주고 `PC에서 재생` 요청을 API로 보낸다. PC가 온라인 상태가 아니면 명확히 안내한다.
- MVP에서는 PC 서버 연결이 필요하다. 오프라인 변경 큐 및 양방향 충돌 해결은 후속 단계다.

## 8. HTTP API 초안

Base URL 예: `http://<PC-LAN-IP>:<port>/api/v1`. JSON, UTF-8을 사용한다.

| Method | Route | 목적 |
|---|---|---|
| GET | `/health` | 서버 상태 및 API 버전 확인 |
| GET | `/channels?platform=chzzk` | 채널 목록 |
| POST | `/channels` | 채널 추가 |
| PATCH | `/channels/{id}` | 채널 이름/주소/활성화 변경 |
| DELETE | `/channels/{id}` | 채널 삭제(연결된 이력은 보존, ChannelId만 NULL 처리) |
| POST | `/channels/{id}/refresh` | 동기식 또는 작업 ID 기반 새로고침 실행 |
| GET | `/refresh-runs/{id}` | 갱신 작업 진행/결과 확인 |
| GET | `/media?platform=&kind=&status=&channelId=&q=&cursor=` | 페이지네이션 목록/검색/필터 |
| POST | `/media` | 온라인 VOD URL, 로컬 영상 메타데이터 수동 추가 |
| GET | `/media/{id}` | 상세 조회 |
| PATCH | `/media/{id}` | 제목/URL/로컬 경로 등 편집 |
| DELETE | `/media/{id}` | 사용자 명시 삭제(연결 데이터 정책을 확인하고 수행) |
| PUT | `/media/{id}/watch` | 상태 및 이어보기 위치 갱신, `RowVersion` 충돌 검사 |
| PUT | `/media/{id}/notes` | 메모 저장 |
| POST | `/media/{id}/play` | PC에서 로컬 영상 재생 요청(데스크톱 API 전용) |
| GET | `/settings/player` | 플레이어 설정 조회(실행 파일 경로 노출 범위 제한) |
| PUT | `/settings/player` | 플레이어 설정 변경(PC에서만 허용) |
| GET | `/export/csv?platform=` | CSV 다운로드 |
| POST | `/import/csv/preview` | 업로드 CSV 검증/미리보기 |
| POST | `/import/csv/apply` | 확인된 가져오기 반영 |

- 상태 코드와 오류 본문을 일관되게 정의한다. 검증 오류는 `400`, 미발견 `404`, 버전 충돌 `409`, 서버 오류 `500`을 사용한다.
- 목록은 제한 크기 페이지네이션을 사용하고, `refresh`는 장시간 요청을 막기 위해 작업 ID 반환 방식을 우선한다.
- DTO에는 DB 엔티티를 직접 노출하지 않는다. `RowVersion` 또는 ETag로 다중 클라이언트 덮어쓰기를 감지한다.
- Excel 생성은 API 서버에서 `.xlsx`를 반환하거나 데스크톱에서 동일 계약으로 생성할 수 있다. 단일 구현을 선택해 동작을 일치시킨다.

## 9. 네트워크 및 보안

- API 기본 바인딩은 루프백으로 시작한다. Android에서 접속을 켤 때만 사용자가 승인한 LAN 인터페이스에 바인딩한다. 라우터 포트 포워딩/인터넷 공개는 기본 비활성화한다.
- LAN 모드에는 페어링/인증 토큰을 적용하고 Windows 방화벽에서 필요한 포트만 제한적으로 허용한다. 토큰은 기기별 취소 가능해야 하며 평문 로그를 금지한다.
- 평문 HTTP는 신뢰 가능한 개인 LAN의 MVP 범위에서만 허용하고 위험을 설정 화면에서 알린다. 외부 네트워크 접속이 필요해지면 TLS/VPN 등 보호된 전송을 별도 설계한다.
- 모든 입력 URL은 `http`/`https` scheme과 호스트를 검증한다. API가 임의 URL을 대신 가져오는 SSRF 형태로 만들지 않는다.
- 파일 재생 API는 서버 호스트의 허용된 경로에 있는 미디어 항목만 실행한다. 클라이언트가 전달한 임의 명령이나 실행 파일 경로를 실행하지 않는다.
- DB 백업/가져오기 전 기존 DB를 덮어쓰지 않고 복구 가능한 백업을 제공한다. 로그에 메모나 전체 경로를 기본 기록하지 않는다.

## 10. 단계별 MVP 계획

### 0단계 — 기반

- 솔루션, SDK 고정, 빌드 설정, 프로젝트 참조, SQLite migration, 설정 저장 구조를 만든다.
- 도메인 모델, API 계약, 기본 오류/로깅 규칙을 정한다.

### 1단계 — Windows 단독 사용

- Avalonia UI: `치지직`, `soop`, `내 영상` 탭, 목록/검색/필터, 채널 및 영상 수동 등록.
- 시청 상태/메모/이어보기 편집과 SQLite 영구 저장.
- 브라우저 URL 열기 및 설정 가능한 외부 플레이어로 로컬 영상 실행.
- CSV import/export와 정확히 두 워크시트(`치지직`, `soop`)인 Excel export/import.

### 2단계 — 채널 VOD 갱신

- 제공자별 어댑터를 연결하고 새 항목만 병합.
- 사라진 VOD 이력 유지, 실패/부분 실패 시 기존 데이터 보존.
- 플랫폼별 API/인증/호출 제한 적합성을 확인한 뒤 지원 가능한 연동부터 활성화.

### 3단계 — PC API 및 Android

- API 목록/상세/상태/메모/새로고침/재생 요청 구현.
- PC 서버 주소 설정, 기기 페어링, Android 목록/검색/상태 편집/온라인 URL 열기.
- Android에서 로컬 영상의 PC 재생 요청과 연결 오류 처리.

### 후속 범위

- 오프라인 동기화 큐 및 충돌 해결, YouTube provider, 메타데이터 자동 보강, 재생 상태 자동 감지.
- 기본 MVP에는 사용자 요청으로 승인된 플랫폼 비공식 API 의존을 허용한다. 비공식 연동은 출처와 변경 가능성을 문서화하고 수동 등록 대안을 유지한다. 영상 스트리밍 프록시와 계정 비밀번호 수집 기능은 넣지 않는다.

## 11. 인수 기준 및 수동 검증 시나리오

1. Windows 앱에서 온라인 VOD URL을 추가하고 재생하면 기본 브라우저가 해당 URL을 연다.
2. 로컬 파일을 추가하고 플레이어를 설정하면 설정된 실행 파일에 파일 경로와 저장된 이어보기 초가 인자로 전달된다. 인자에 공백/한글 경로가 있어도 하나의 파일 인자로 안전하게 처리된다.
3. 시청 상태, 메모, 진행 위치를 저장한 뒤 앱을 재시작해도 값이 보존된다.
4. 플랫폼 VOD를 저장한 뒤 제공자 응답에서 그 VOD를 제외하고 새로고침해도 기존 행, 상태, 메모, 진행 위치가 유지된다.
5. 이미 저장된 VOD를 다시 받아도 중복 행을 만들지 않으며 사용자 필드를 덮어쓰지 않는다.
6. 제공자 연결 실패 또는 빈 오류 응답을 기존 목록 삭제로 처리하지 않고, 마지막 갱신 실패를 표시한다.
7. Excel 내보내기 파일에는 `치지직`, `soop` 시트가 각각 정확히 한 번 존재하고 그 외 워크시트가 없다. 플랫폼별 항목과 사라진 VOD 이력이 포함된다.
8. CSV/Excel 가져오기에서 메모의 쉼표, 따옴표, 줄바꿈, 한글이 유지되고, 적용 전 변경 미리보기가 보인다. 파일에 없는 행은 삭제되지 않는다.
9. Android에서 PC API에 연결해 목록 및 상태/메모를 확인·수정할 수 있다. 온라인 VOD는 Android에서 연다. 로컬 영상은 요청 시 PC 플레이어에서 열리며 PC 미연결 시 안내한다.
10. API는 기본 설정에서 루프백만 열고, LAN 연결을 명시적으로 켜기 전 외부 기기 연결을 허용하지 않는다.

## 12. Rider + Codex 작업 지침

- 이 문서를 기준 사양으로 삼고, 구현 전 현재 솔루션 구조와 변경 사항을 먼저 살핀다.
- 작업을 작고 검토 가능한 단계로 나누고, 한 번에 전체 앱을 생성하거나 요구사항을 임의 생략하지 않는다.
- 새 라이브러리/버전을 추가하기 전 .NET 10.0.4 및 Kotlin/Android 호환성을 확인하고 중앙 버전 관리를 유지한다.
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
