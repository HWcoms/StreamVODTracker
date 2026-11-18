# 개인 토큰 생성 가이드 (초안)

> 작성 중인 초안입니다. Google Cloud Console 화면과 앱 배포 방식에 맞춰 세부 화면 이름 및 스크린샷을 보완해 주세요.

## 안내

StreamVODTracker는 Google Drive 동기화를 위해 Google OAuth를 사용합니다. 사용자가 액세스 토큰이나 갱신 토큰을 직접 복사해 앱에 붙여 넣는 방식은 아닙니다. 앱에서 Google 계정을 연결하면 Google이 토큰을 발급하고 앱이 필요한 자격 증명을 기기에 저장합니다.

이 가이드는 개인 Google Cloud 프로젝트에서 사용할 OAuth 클라이언트 정보를 만드는 절차의 초안입니다. OAuth 클라이언트 ID와 클라이언트 보안 비밀은 토큰 그 자체가 아닙니다.

## 준비

- Google 계정
- [Google Cloud Console](https://console.cloud.google.com/) 접근 권한
- 앱에서 사용할 기기 및 설치 버전 정보

## Google Cloud 프로젝트와 Drive API

1. Google Cloud Console에서 프로젝트를 새로 만들거나 사용할 프로젝트를 선택합니다.
2. API 라이브러리에서 Google Drive API를 찾아 사용 설정합니다.
3. OAuth 동의 화면을 구성합니다. 앱에서 요청하는 권한 범위는 `openid`, `email`, `https://www.googleapis.com/auth/drive.file`입니다.
4. 동의 화면이 테스트 상태라면 사용할 Google 계정을 테스트 사용자로 등록합니다. 테스트 모드에서는 갱신 토큰이 만료될 수 있으므로 장기간 사용할 때의 제한을 확인합니다.

## Windows OAuth 클라이언트 만들기

1. Google Cloud Console의 사용자 인증 정보에서 OAuth 클라이언트를 만듭니다.
2. 애플리케이션 유형으로 **데스크톱 앱**을 선택합니다.
3. 발급된 클라이언트 ID를 복사합니다. 클라이언트 보안 비밀이 발급되는 경우 함께 보관합니다.
4. StreamVODTracker의 설정에서 클라이언트 ID와 필요한 경우 보안 비밀을 입력합니다.
5. 앱의 Google 계정 연결 버튼을 눌러 계정 선택 및 권한 승인을 완료합니다. 토큰은 앱이 자동으로 관리합니다.

## Android 앱

Android는 Windows 데스크톱 클라이언트와 별도의 Android OAuth 클라이언트를 사용합니다. 앱 패키지 ID와 설치된 빌드 서명 인증서의 SHA-1 지문을 등록해야 합니다. Android 앱에 클라이언트 보안 비밀이나 토큰을 직접 넣지 않습니다. 자세한 서명 및 SHA-1 등록 절차는 [Google Drive 동기화 설정](StreamVODTracker/GOOGLE-DRIVE-SYNC-SETUP.md)의 Android 항목을 참고하세요.

## 보안 유의사항

- 액세스 토큰, 갱신 토큰, 클라이언트 보안 비밀을 공개 저장소나 스크린샷, 채팅, 로그에 올리지 않습니다.
- 토큰이 만료되거나 폐기된 경우 앱 설정에서 Google 계정을 다시 연결합니다.
- 클라이언트 보안 비밀을 분실하거나 노출한 경우 Google Cloud Console에서 해당 클라이언트를 점검하고 필요하면 교체합니다.
- OAuth 동의 화면의 테스트/프로덕션 상태와 토큰 만료 정책은 배포 형태에 맞게 확인합니다.

## 확인이 필요한 내용

- 개인 토큰이라는 표현이 Google OAuth 클라이언트 ID/보안 비밀을 의미하는지, 사용자가 직접 발급받는 액세스 토큰을 의미하는지 확정
- 현재 Google Cloud Console의 메뉴 명칭 및 최신 절차 확인
- 화면 캡처와 실제 앱 설정 화면 추가
- Windows와 Android 각각의 사용자 대상 절차 및 주의사항 보완
