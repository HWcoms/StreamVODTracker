# 플랫폼 API 설정 가이드

이 문서는 VODTracker의 채널 등록에 필요한 API 설정과 자동 조회 동작을 설명합니다. Client Secret은 채팅이나 저장소에 올리지 마세요. 앱 설정 화면은 Windows 자격 증명 관리자에 보관합니다.

## 로컬 정보 갱신과 오프라인 대체

현재 앱은 서버 DB 대신 실행 폴더의 `db` 디렉터리에 채널별 CSV를 저장합니다. 채널 ID를 기준으로 앱을 시작할 때마다 채널명, 설명, 프로필 사진 URL과 이미지를 다시 조회합니다. 조회가 성공하면 CSV와 `thumbnail/<채널 식별 폴더>/`의 이미지 캐시를 갱신합니다. 네트워크/API/이미지 다운로드가 실패하면 CSV에 저장된 채널 정보와 기존 캐시 이미지를 유지해 표시합니다. CSV에는 `IsLive`를 저장하지 않으며 라이브 여부는 실행 중 확인합니다.

이미지 주소는 공개 프로필 이미지 URL이며 로그인 쿠키나 Client Secret을 사용하지 않습니다. 프로필 주소만 저장된 상태에서 이미지 파일이 없으면 저장된 URL을 재시도합니다.

## 치지직 채널 자동 조회

치지직 조회는 공식 API가 아닌 커뮤니티 비공식 API 라이브러리 [kimcore/chzzk](https://github.com/kimcore/chzzk)의 채널 조회 규격을 참고합니다. 현재 앱은 채널 공개 프로필 엔드포인트를 사용하며 로그인 쿠키를 보내지 않습니다.

1. 채널 추가에서 치지직을 선택합니다.
2. 채널 ID를 입력합니다. 예시는 입력창 아래에 표시됩니다. 실제 채널 주소 형식은 `https://chzzk.naver.com/{채널ID}`입니다.
3. **채널 정보 불러오기**를 누르면 채널명과 설명이 입력됩니다.

조회 주소: `GET https://api.chzzk.naver.com/service/v1/channels/{channelId}`. 응답의 `content.channelName`, `content.channelDescription`, `content.channelImageUrl`, `content.openLive`를 사용합니다. 채널명·설명·프로필 사진은 앱을 시작할 때 다시 조회하며 성공하면 로컬 CSV와 이미지 캐시를 갱신합니다. 라이브 상태는 실행 중 주기적으로 다시 확인합니다. 이 엔드포인트는 비공식이며 변경되거나 제한될 수 있으므로, 조회가 실패하면 등록된 CSV 정보와 기존 프로필 이미지 캐시를 사용합니다.

설정 화면의 Client ID와 Secret은 치지직 공식 API 연동을 위한 보관 항목입니다. 현재 비공식 채널 프로필 조회에는 필요하지 않습니다. 공식 개발자 앱을 등록하려면 [치지직 개발자 센터](https://developers.chzzk.naver.com/)를 이용하세요. 공식 API 인증은 [Client 인증 안내](https://chzzk.gitbook.io/chzzk/chzzk-api/tips), 공식 채널 조회는 [채널 API 문서](https://chzzk.gitbook.io/chzzk/chzzk-api/channel)를 참고하세요.

## SOOP 채널 자동 조회

SOOP 채널 조회는 커뮤니티 비공식 클라이언트 [reindeer002/soop](https://github.com/reindeer002/soop)의 채널 스테이션 API와 공개 사용자 스크립트의 도메인 폴백 동작을 참고합니다. 로그인 정보나 쿠키는 사용하지 않습니다.

1. 채널 추가에서 SOOP을 선택합니다.
2. 스트리머 ID를 입력합니다. 예시는 입력창 아래에 표시됩니다. 등록 URL은 `https://www.sooplive.com/station/{스트리머ID}`입니다.
3. **채널 정보 불러오기**를 누르면 채널명과 프로필 설명을 입력합니다.

조회 순서: `GET https://chapi.sooplive.com/api/{streamerId}/station`, 실패하면 `chapi.sooplive.co.kr`로 한 번 재시도합니다. 응답의 `station.user_nick`(없으면 `station.station_name`), `station.display.profile_text`, 최상위 `profile_image`, `broad.broad_no`를 사용합니다. 프로필 주소가 없거나 다운로드가 실패하면 공개 `profile.img.sooplive.com/LOGO/...` 주소도 시도합니다. 채널명·설명·프로필 사진은 매 앱 실행 시 갱신을 시도하며, 실패하면 CSV와 저장된 캐시를 사용합니다. 라이브 표시는 실행 중 상태로만 관리하고 CSV에는 저장하지 않습니다. 이 규격도 비공식이므로 응답 구조나 도메인이 바뀌면 조회가 실패할 수 있습니다. 그 경우 채널명과 설명을 직접 입력해 등록하세요.

SOOP Client ID/Secret은 공식 API용으로 설정 화면에 보관할 수 있지만, 현재 비공식 프로필 조회에는 필요하지 않습니다. HTML 페이지 스크래핑은 사용하지 않습니다.

## 자격 증명 관리

- Client Secret 입력란은 마스킹됩니다.
- 저장된 Client ID/Secret은 Windows 자격 증명 관리자에 앱 전용 항목으로 보관됩니다.
- 설정 화면에서 자격 증명을 삭제할 수 있습니다.
- 치지직 비공식 채널 조회에는 ID/Secret이나 로그인 쿠키가 필요하지 않습니다.
## 라이선스 및 외부 코드 참조

앱의 **라이선스** 탭 또는 [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md)에서 NuGet 패키지와 참고 프로젝트의 라이선스 현황, 추가 확인이 필요한 항목을 확인하세요.
