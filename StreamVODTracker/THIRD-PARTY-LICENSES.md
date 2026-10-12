# 라이선스 및 외부 프로젝트 고지

## VODTracker

VODTracker 프로그램 코드는 MIT License로 배포합니다. 전문은 저장소 루트의 [LICENSE](../LICENSE) 파일을 확인하세요.

## 참고한 프로젝트 및 제작자

| 프로젝트 | 제작자 | 라이선스 | 사용 내용 및 고지 |
|---|---|---|---|
| [kimcore/chzzk](https://github.com/kimcore/chzzk) | [kimcore](https://github.com/kimcore) | [MIT](https://github.com/kimcore/chzzk/blob/main/LICENSE) | 치지직 공개 채널 프로필 API 경로와 응답 필드 참고. 프로젝트 코드는 복사하지 않았습니다. API 서비스 이용 조건은 저장소 MIT 라이선스와 별개입니다. |
| [reindeer002/soop](https://github.com/reindeer002/soop) | [reindeer002](https://github.com/reindeer002) | [MIT](https://github.com/reindeer002/soop/blob/develop/LICENSE) | SOOP 공개 스테이션 API 경로와 응답 필드 참고. 프로젝트 코드는 복사하지 않았습니다. API 서비스 이용 조건은 저장소 MIT 라이선스와 별개입니다. |
| [SOOP (숲) - 방송 알리미](https://greasyfork.org/scripts/588909/code) | [Eldirna](https://greasyfork.org/en/users?search=Eldirna) | [MIT (스크립트 메타데이터)](https://greasyfork.org/scripts/588909/code) | SOOP API 호스트 폴백과 공개 프로필 이미지 주소의 조합 규칙을 참고했습니다. 사용자 스크립트 코드는 포함하지 않았습니다. |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) 및 관련 Avalonia 패키지 | [Avalonia Team](https://github.com/AvaloniaUI) | [MIT](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md) | UI 프레임워크, 테마, 플랫폼, 폰트 및 렌더링 패키지. |
| [ANGLE](https://github.com/google/angle) · Avalonia.Angle.Windows.Natives | [Google](https://github.com/google) 및 ANGLE 기여자 | [BSD 3-Clause](https://github.com/google/angle/blob/main/LICENSE) | 네이티브 그래픽 패키지. 전체 저작권·조건·면책 고지는 배포물의 `THIRD-PARTY-NOTICES.txt`에 동봉합니다. |
| [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) | [Microsoft](https://github.com/microsoft) | [MIT](https://github.com/mono/SkiaSharp/blob/main/LICENSE) | 그래픽 렌더링 및 글꼴 셰이핑 패키지. NuGet 메타데이터의 제작자 표기는 Microsoft입니다. |
| [Microsoft.Extensions](https://github.com/dotnet/extensions) | [.NET Foundation](https://github.com/dotnet) | [MIT](https://github.com/dotnet/extensions/blob/main/LICENSE.TXT) | 의존성 주입 및 로깅 추상화 패키지. |
| [Microsoft.IO.RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream) | [Microsoft](https://github.com/microsoft) | [MIT](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream/blob/main/LICENSE) | 스트림 풀링 라이브러리. |
| [MicroCom](https://github.com/kekekeks/MicroCom) · MicroCom.Runtime | [kekekeks](https://github.com/kekekeks) | [MIT](https://github.com/kekekeks/MicroCom/blob/master/LICENSE) | COM 상호 운용 지원 패키지. |
| [Tmds.DBus](https://github.com/tmds/Tmds.DBus) · Tmds.DBus.Protocol | [Tom Deseyn](https://github.com/tmds) | [MIT](https://github.com/tmds/Tmds.DBus/blob/main/LICENSE) | D-Bus 프로토콜 패키지. |

## 배포 참고

- 이 프로그램의 MIT 라이선스는 위 외부 패키지와 프로젝트의 별도 라이선스를 대체하지 않습니다.
- ANGLE의 BSD 3-Clause 전문은 `THIRD-PARTY-NOTICES.txt`에 포함되어 있으며, 이 파일은 빌드 및 게시(publish) 출력 디렉터리로 복사됩니다.
- 라이선스 메타데이터를 확인할 수 없는 `AvaloniaUI.DiagnosticsSupport` 패키지와 Debug 전용 Developer Tools 연결을 프로젝트에서 제거했습니다.
- 비공식 API 참고 프로젝트의 MIT 라이선스는 해당 저장소의 코드에 대한 것입니다. 치지직/SOOP 서비스 API의 접근·사용 조건을 허가하는 것은 아닙니다.
- 현재 사용 중인 NuGet 의존성 버전과 라이선스 메타데이터는 `StreamVODTracker/obj/project.assets.json` 및 복원된 `.nuspec` 파일을 기준으로 기록했습니다. 의존성 변경 시 이 문서도 갱신하세요.
