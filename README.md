# DEEPAi Service Directory

Milestone XProtect Management Server와 함께 설치되어 자사 서버 서비스의 주소, 등록 상태와 사이트 인증서 신뢰를 관리하는 서비스 디렉토리 저장소다.

## 애플리케이션과 개발 단위

단일 배포 패키지 이름은 `service-directory`이며 최종 Inno Setup 설치 EXE 하나로 배포한다.

| 개발 단위 | 위치 | 책임 |
|---|---|---|
| 메인 서비스 | `src/DEEPAi.ServiceDirectory.Service/` | Windows Service, Directory API와 상태 소유 |
| 설정 UI | `src/DEEPAi.ServiceDirectory.Tray/` | WPF 설정 화면과 로컬 관리 명령 |
| 와치독 | `src/DEEPAi.ServiceDirectory.Watchdog/` | 상태 감시와 제한된 서비스 제어 |
| 공유 계층 | `src/DEEPAi.ServiceDirectory.Domain/`, `Application/`, `Infrastructure/`, `ExternalProtocol/`, `InternalProtocol/` | 도메인·응용·저장·프로토콜 구현 |
| 테스트 | `tests/DEEPAi.ServiceDirectory.Tests/` | x64 MSTest 계약·단위 테스트 |

코드는 각 배포 구성요소 또는 공유 assembly별 `src/<구성요소명>/`에 분리한다. 빌드·인스톨러 공용 외부 패키지를 저장소에 둘 필요가 생기면 `common/`만 사용하고, 생성 파일은 `artifacts/`, 최종 설치 EXE는 `installer/` 바로 아래에 둔다.

## 계획 문서

모든 계획 문서는 `docs/plan/` 바로 아래에 두며 파일명으로 책임을 구분한다.

| 문서 | 용도 |
|---|---|
| [`00-overview.md`](docs/plan/00-overview.md) | 전체 목표, 읽기 순서, phase와 현재 상태 |
| [`01-hardening.md`](docs/plan/01-hardening.md) | Directory 구조 제품 전용 하드닝 기준 |
| [`02-certificate-transition.md`](docs/plan/02-certificate-transition.md) | 인증서 전환 차이와 구현 단계 |
| [`03-development.md`](docs/plan/03-development.md) | 제품 구성, 저장·복구·동기화 불변식과 개발 계획 |
| [`03-development-01-storage-schema.md`](docs/plan/03-development-01-storage-schema.md) | 최초 정식 저장 schema v1, 파일 형식과 복구 transaction |
| [`04-api.md`](docs/plan/04-api.md) | API 신뢰 경계와 상세 명세 색인 |
| [`04-api-01-external-application.md`](docs/plan/04-api-01-external-application.md) | 외부 애플리케이션 인증·등록·조회 계약 |
| [`04-api-02-internal.md`](docs/plan/04-api-02-internal.md) | Admin·와치독·Peer 내부 계약 |
| [`05-next-development.md`](docs/plan/05-next-development.md) | 인증서 전환의 다음 구현 순서, 변경 위치와 단계별 완료 조건 |
| [`06-release-validation.md`](docs/plan/06-release-validation.md) | 설치 상태 증거 수집 도구와 실제 OS·Milestone·두 장비 검증 순서 |

계획용 이미지 원본과 결과물은 `docs/plan/03-development/`, 규범 XSD는 `docs/plan/04-api/`에 둔다. 구현은 가장 큰 순번의 할 일 파일을 기준으로 진행하며, 현재 목록은 [`docs/plan/todo-01.md`](docs/plan/todo-01.md)다.

## 작업 지침

공통 개발·문서·검증·버전 규칙은 [`AGENTS.md`](AGENTS.md)가 단일 원본이며 `CLAUDE.md`는 이 파일만 참조한다.
