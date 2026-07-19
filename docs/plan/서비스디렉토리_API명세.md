# 서비스 디렉토리 API 명세 안내

> 문서 상태: 분리 완료, 세부 wire 계약 확정
> 구현 상태: External 세 endpoint·`WDOG` loopback health·Admin 12개 endpoint 경계와 이를 연결하는 공용 `HttpListener` transport host, strict External·Admin·Peer XML codec, Peer 인증·sync primitive, Tray client 및 Watchdog Windows Service 실행체 소스 추가. 현재 작업 트리 빌드·테스트 미검증, 메인 Windows Service 연결·Admin application handler·Peer HTTP 상태/session/DPAPI·wire orchestration 미구현
> 최종 정리일: 2026-07-18

기존의 단일 API 명세를 호출 주체와 신뢰 경계에 따라 두 문서로 분리했다. 이 파일은 호환되는 진입점과 문서 색인으로만 사용하며, 요청·응답의 단일 원본은 아래 두 상세 명세다.

## 운영 기준

- Milestone XProtect `2021 R1` 이상에서 동작하는 `x64` 전용 구성요소
- 지원 OS는 x64 Windows Server `2019` 이상 Standard·Datacenter Desktop Experience, Windows 10 `1809` 이상 및 Windows 11 `24H2` 이상 Pro·Enterprise·IoT Enterprise이며 Server Core는 제외. Enterprise·IoT Enterprise LTSC는 버전 하한·Milestone 지원 교집합·조합 검증을 모두 충족한 release만 포함
- Active Directory 도메인과 Workgroup 환경 모두 지원
- External·Peer 원격 prefix는 설치 시 선택한 단일 unicast IP literal `ListenAddress`에 등록하고 Admin·와치독 loopback prefix는 별도로 등록. IPv6 link-local·multicast·IPv4-mapped와 zone identifier, wildcard를 금지하고 mapped 주소는 원래 IPv4 literal로 입력. Windows 방화벽은 Domain·Private 프로필만 허용하며 Public 프로필에서는 차단하고 원격 CIDR·원격 대역 allowlist는 사용하지 않음
- IP literal prefix를 보안 경계로 취급하지 않고 요청의 실제 local endpoint를 신뢰 경계별 주소와 TCP `21000`에 다시 결합
- 활성 서비스 최대 1,000개, 외부 애플리케이션 호출은 저빈도 전제
- URL, media type, health와 Peer XML에 API 버전 필드를 두지 않고 현재 무버전 경로를 영구 유지. 기존 소비자와 호환되는 추가만 허용
- 고정 XML namespace는 External `urn:deepai:service-directory:external`, Admin `urn:deepai:service-directory:admin`, Peer `urn:deepai:service-directory:peer`이며 버전 suffix와 namespace 없는 요청을 금지. 규범 XSD는 [`xsd/external.xsd`](./xsd/external.xsd), [`xsd/admin.xsd`](./xsd/admin.xsd), [`xsd/peer.xsd`](./xsd/peer.xsd)
- HTTP `200`은 `Code=0` 성공에만 사용하고 `400`·`401`·`403`·`404`·`409`·`413`·`415`·`429`·`500` 매핑과 인증 전 bodyless·인증 후 Peer signed 오류 예외는 상세 명세를 따름
- Peer 병합은 `LogicalVersion`과 canonical `OriginInstanceId`를 사용하고 UTC 시각은 감사·표시 전용. 60초 시계 편차는 Peer 인증 freshness에만 적용
- 인증·인가·endpoint 신뢰 경계 실패는 Windows Application Event Log source `DEEPAi.ServiceDirectory.Security`에 별도 기록하고 비밀값 배제와 반복 실패 flood 억제를 적용
- 상세 요청 크기·필드·항목 수·rate limit·timeout은 아래 내·외부 상세 명세가 단일 원본

## 상세 명세

| 문서 | 대상 | 포함 범위 |
|---|---|---|
| [외부 애플리케이션 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md) | 서비스 디렉토리를 이용하는 다른 제품 | 생존 확인, 제품코드별 서비스 조회, 등록·수정 승인 요청. 별도 결과 API 없이 서비스 재조회로 승인 반영 확인 |
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
- Admin과 Peer 계약은 외부 호환성 약속에 포함하지 않지만, 인증·호환성·오류 계약을 임의 변경하지 않는다.
- 파일명, `directory.xml` 구조, `File.Replace` 같은 저장 구현은 API 계약이 아니다. [개발계획](./서비스디렉토리_개발계획.md)에서 관리한다.
- “External”, “Admin”, “Peer”는 신뢰 경계를 분류하는 이름이다. External은 승인된 예외인 일일 검증값과 폐쇄망 통제를 사용하며 강한 호출자 인증으로 표현하지 않는다. Admin·Peer는 IP 주소, loopback 또는 방화벽만으로 인증을 대신하지 않는다.

## 현재 호환성 상태

두 IP literal prefix, raw request-target, 경로별 인증 선택, 세 완성 어댑터 dispatch, 응답 기록과 endpoint별 deadline을 담당하는 `HttpListener` transport host 소스는 추가됐지만 메인 Windows Service에 연결하지 않았고 실제 Windows listener·Negotiate/NTLM·중지 경합을 실행 검증하지 않았다. Peer wire handler가 없으므로 `/api/sync/*`는 host에서 body 없는 `404`로 닫혀 있다. Tray의 Admin client도 실행 검증하지 않았지만 두 상세 명세의 wire 계약은 확정했다. 경로와 wire에는 API 버전 필드를 두지 않으며 현재 `/api/*`, `/admin/*`, `/api/sync/*` 경로를 영구 유지한다. 공개 뒤에는 기존 소비자가 계속 동작하는 호환 추가만 허용한다.

- External 일일 API 키 헤더는 현재 44자 알고리즘에 영구 결합한다. 불가피한 교체는 기존 wire 변경이 아니라 별도 인증 헤더와 endpoint의 병렬 계약 및 명시적 migration으로만 수행한다.
- HTTP 상태와 envelope 오류 매핑은 각 상세 명세에서 확정했다.
- 상세 명세와 XSD에 열거한 오류 `Code` 집합은 닫힌 계약이다. 목록에 없는 값을 임의로 추가하거나 기존 의미를 재사용하지 않는다.
- 요청은 경계별 고정 namespace와 XSD를 엄격히 적용하고 namespace 없는 XML을 거부한다. 호환 추가는 응답 마지막의 선택적 `Extensions` 안에서만 허용하며 요청의 알 수 없는 요소·속성은 거부한다.

외부 계약을 확정한 뒤에는 경로·메서드·기존 필드 의미를 바꾸거나 새 필수 필드를 추가하지 않는다. 기능 확장은 상세 계약이 이미 허용한 선택적 확장점 또는 별도 endpoint 추가처럼 기존 소비자가 계속 동작하는 방식으로만 수행하며 `/v1` 같은 버전 경로, version query·header와 `ApiVersion`·`ProtocolVersion` 요소를 추가하지 않는다. 암호 primitive의 domain-separation label과 알고리즘 식별자는 API 버전 필드로 취급하지 않는다.
