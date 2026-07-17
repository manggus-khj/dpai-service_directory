# 서비스 디렉토리 API 명세 안내

> 문서 상태: 분리 완료, 세부 계약은 초안
> 구현 상태: 미구현
> API 초안 버전: 1.0
> 최종 정리일: 2026-07-17

기존의 단일 API 명세를 호출 주체와 신뢰 경계에 따라 두 문서로 분리했다. 이 파일은 호환되는 진입점과 문서 색인으로만 사용하며, 요청·응답의 단일 원본은 아래 두 상세 명세다.

## 운영 기준

- Milestone XProtect `2021 R1` 이상에서 동작하는 `x64` 전용 구성요소
- Active Directory 도메인과 Workgroup 환경 모두 지원
- 설치 시 선택한 단일 IP literal `ListenAddress`의 폐쇄망 인터페이스 바인딩과 wildcard 금지. Windows 방화벽은 Domain·Private 프로필만 허용하고 Public 프로필에서는 차단하며 원격 CIDR·원격 대역 allowlist는 사용하지 않음
- 활성 서비스 최대 1,000개, 외부 애플리케이션 호출은 저빈도 전제
- 상세 요청 크기·필드·항목 수·rate limit·timeout은 아래 내·외부 상세 명세가 단일 원본

## 상세 명세

| 문서 | 대상 | 포함 범위 |
|---|---|---|
| [외부 애플리케이션 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md) | 서비스 디렉토리를 이용하는 다른 제품 | 생존 확인, 제품코드별 서비스 조회, 등록·수정 승인 요청 |
| [내부 API 명세](./서비스디렉토리_내부_API명세.md) | 트레이 앱, 와치독, 상대 서비스 디렉토리 | 관리, 승인·거절·삭제, 동기화 설정·교환·해제, 로컬 서비스 제어 |

## 엔드포인트 소유권

| 등급 | 호출 주체 | 엔드포인트 | 상세 문서 |
|---|---|---|---|
| External | 4바이트 ProductCode·일일 API 키 검증을 통과한 다른 애플리케이션 | `GET /api/health` | 외부 명세 |
| External | 4바이트 ProductCode·일일 API 키 검증을 통과한 다른 애플리케이션 | `GET /api/services?productCode={code}` | 외부 명세 |
| External | 4바이트 ProductCode·일일 API 키 검증을 통과한 다른 애플리케이션 | `POST /api/registration` | 외부 명세 |
| Admin | loopback Negotiate 인증과 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹 인가를 통과한 운영자 UI | `/admin/*` | 내부 명세 |
| Peer | ECDH P-256·양쪽 8자리 SAS로 페어링되고 DPAPI·HMAC-SHA256 계약을 통과한 상대 서비스 디렉토리 | `/api/sync/*` | 내부 명세 |
| Local IPC | 로컬의 인가된 트레이 앱 | `\\.\pipe\SvcDirWatchdog` | 내부 명세 |

와치독도 health 전용 구성요소 코드 `WDOG`로 일일 API 키를 생성해 `GET /api/health`를 재사용한다. 계약의 소유권은 외부 명세에 두고 내부 명세는 링크만 한다.

## 분리 원칙

- 다른 애플리케이션은 외부 명세만으로 연동할 수 있어야 한다.
- 외부 DTO에는 톰스톤, 피어 `InstanceId`, 승인 대기 내부 모델 같은 구현 정보를 노출하지 않는다.
- Admin과 Peer 계약은 외부 호환성 약속에 포함하지 않지만, 인증·버전·오류 계약 없이 임의 변경하지 않는다.
- 파일명, `directory.xml` 구조, `File.Replace` 같은 저장 구현은 API 계약이 아니다. [개발계획](./서비스디렉토리_개발계획.md)에서 관리한다.
- “External”, “Admin”, “Peer”는 신뢰 경계를 분류하는 이름이다. External은 승인된 예외인 일일 검증값과 폐쇄망 통제를 사용하며 강한 호출자 인증으로 표현하지 않는다. Admin·Peer는 IP 주소, loopback 또는 방화벽만으로 인증을 대신하지 않는다.

## 현재 호환성 상태

아직 구현과 실제 소비자가 없으므로 두 명세는 초안이다. 기존 경로를 유지해 문서를 분리했지만 다음 항목이 확정되기 전에는 1.0 고정 계약으로 선언하지 않는다.

- URL 또는 미디어 타입 기반 버전 정책
- External 일일 API 키 알고리즘 버전 전환 정책
- 표준 HTTP 상태 사용 여부와 envelope 오류 코드의 최종 매핑
- 등록 요청의 승인·거절 상태 조회 방식
- XML namespace 또는 XSD 사용 여부

외부 계약을 확정한 뒤에는 호환되지 않는 변경을 같은 경로에 덮어쓰지 않는다. 새 버전을 추가하고 이전 계약의 지원·폐기 일정을 기록한다.
