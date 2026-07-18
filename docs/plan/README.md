# 서비스 디렉토리 계획 문서 안내

> 문서 묶음 상태: 설계 초안
> 구현 상태: 초기 기반 구현 중, Debug|x64 빌드 확인·실행 미검증
> 최종 정리일: 2026-07-18

이 디렉터리는 서비스 디렉토리의 제품 설계, API 계약, 공통 보안 기준을 관리한다. 현재 저장소에는 초기 x64 솔루션, `LogicalVersion`·snapshot `LogicalClock` 도메인 모델과 결정적 revision 비교, 등록 상태 전이·승인 서비스 조회와 mutation coordinator, 저장 계약, External 일일 API 키 코덱·헤더 검증 경계, 원자 파일 교체, 시스템 파일 로그·보존, Windows Application Event Log 보안 진단·flood limiter primitive, bounded XML 입력, Named Pipe wire codec, `ListenAddress` prefix·요청 endpoint guard, Admin Windows identity 인가와 Peer P-256 pairing 암호 primitive가 있다. 2026-07-18 Visual Studio Build Tools 2022에서 Debug|x64 솔루션 빌드를 경고·오류 없이 확인했지만 Release·실행·테스트는 검증하지 않았다. 서비스 호스트, HTTP API, XML serializer·다중 파일 복구와 durable logical clock 저장, 실제 sync 병합, 설정, 보안 진단 host·installer 통합, UI, 와치독 실행체, 설치와 테스트는 구현되지 않았다. 문서에서 “확정”이라고 표시한 내용도 구현 또는 검증 완료를 뜻하지 않는다.

## 확정 운영 기준

- 제품 버전 `1.0.0`, 초기 build `1`. 제품 버전은 사용자 명시 요청 때만 변경하고 이후 새 변경의 commit+push 전달마다 build만 1 증가
- Milestone XProtect `2021 R1` 이상, `x64` 전용
- 지원 OS는 x64 Windows Server `2019` 이상 Standard·Datacenter Desktop Experience, Windows 10 `1809` 이상 및 Windows 11 `24H2` 이상 Pro·Enterprise·IoT Enterprise이며 Server Core는 제외. Enterprise·IoT Enterprise LTSC는 버전 하한·Milestone 지원 교집합·조합 검증을 모두 충족한 release만 포함
- Active Directory 도메인과 Workgroup 환경 모두 지원
- External·Peer 원격 prefix는 설치 시 선택한 단일 unicast IP literal `ListenAddress`에만 등록하고 Admin·와치독 loopback prefix는 별도로 등록. IPv6 link-local·multicast·IPv4-mapped와 zone identifier, wildcard를 금지하고 mapped 주소는 원래 IPv4 literal로 입력. Windows 방화벽은 Domain·Private 프로필만 허용하며 Public 프로필에서는 차단하고 원격 CIDR·원격 대역 allowlist는 사용하지 않음
- IP literal URL prefix만 보안 경계로 신뢰하지 않고, External·Peer 요청의 실제 local endpoint를 `ListenAddress:21000`과 비교하며 Admin·와치독 요청은 local `127.0.0.1:21000`과 loopback remote address를 모두 확인
- `Deleted=false`인 활성 서비스 최대 1,000개, 외부 애플리케이션 호출은 저빈도 전제
- Admin은 `127.0.0.1` loopback `HttpListener`의 Negotiate를 사용하고 AD·Workgroup 모두 NTLM을 허용하며 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹으로 인가. Kerberos는 검증된 hostname·SPN을 별도 구성한 경우에만 선택적으로 사용하고 `UnsafeConnectionNtlmAuthentication=false` 유지
- Peer는 [내부 API 명세](./서비스디렉토리_내부_API명세.md#22-peer)의 ECDH P-256·양쪽 8자리 SAS 확인·DPAPI·HMAC-SHA256 계약 사용
- 동기화 병합은 레코드의 unsigned 64-bit `LogicalVersion`과 canonical `OriginInstanceId`를 순서대로 비교하며 내구적 `LogicalClock`을 사용. UTC 변경 시각은 감사·표시 전용이고 60초 시계 편차 한계는 Peer 인증 freshness에만 적용
- API는 URL·media type·health·Peer wire에 버전 필드를 두지 않고 현재 무버전 경로를 영구 유지하며 호환 추가만 허용
- 시스템 파일 로그 보존은 기본 `30`일, 허용 범위 `1..1095`일. 인증·인가·신뢰 경계 실패는 9개 파일 이벤트와 분리해 Windows Application Event Log source `DEEPAi.ServiceDirectory.Security`에 기록하고 비밀값 배제·flood 억제 적용
- 외부 등록 결과용 별도 API와 거절 이력을 제공하지 않고 외부 앱은 `/api/services` 재조회로 승인 반영 여부만 확인
- 일반 제거는 `%ProgramData%\DEEPAi\ServiceDirectory\`의 운영 데이터·로그·Peer 자격 증명을 보존하고 명시적 전체 삭제 선택 때만 제거

상세 요청 제한과 보안 wire contract는 [외부 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md)와 [내부 API 명세](./서비스디렉토리_내부_API명세.md)를 단일 원본으로 사용한다.

제품·빌드 번호의 기계 판독 단일 원본은 저장소 루트 [VERSION](../../VERSION)이다. 제품 버전은 무버전 API 경로와 wire에 노출하지 않는다. 암호 primitive의 알고리즘 식별자와 domain-separation label은 API 버전 필드가 아니다.

## 권장 읽기 순서

| 순서 | 문서 | 책임 |
|---|---|---|
| 1 | [애플리케이션 하드닝 가이드](./애플리케이션_하드닝_가이드.md) | 모든 제품이 지켜야 하는 최소 보안 기준 |
| 2 | [서비스 디렉토리 개발계획](./서비스디렉토리_개발계획.md) | 제품 범위, 구성요소, 도메인 규칙, 저장·동기화 설계, 개발 순서 |
| 3 | [API 명세 안내](./서비스디렉토리_API명세.md) | API 문서 경계와 엔드포인트 소유권 |
| 4 | [외부 애플리케이션 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md) | 다른 제품이 서비스 조회와 등록 요청에 사용하는 계약 |
| 5 | [내부 API 명세](./서비스디렉토리_내부_API명세.md) | 트레이, 와치독, 서비스 디렉토리 피어가 사용하는 계약 |

## 문서 우선순위

문서가 충돌할 때는 다음 기준을 적용한다.

1. 보안 하드닝 가이드는 공통 기준선이다. 프로젝트별 판정과 승인된 예외는 [개발계획 §8](./서비스디렉토리_개발계획.md#8-프로젝트-하드닝-적용)에 사유, 위험, 보완 통제, 적용 기간과 승인 근거를 기록한다.
2. API 요청·응답과 호출자 계약은 해당 외부 또는 내부 API 명세가 단일 원본이다.
3. 제품 구성, 데이터 소유권, 저장과 동기화 불변식은 개발계획이 단일 원본이다.
4. 요약 문구와 상세 문서가 다르면 상세 문서를 우선하되, 같은 변경에서 모순된 요약도 함께 고친다.

## 상태 표기

| 표기 | 의미 |
|---|---|
| 확정 | 설계 결정을 승인함. 구현 완료라는 뜻은 아님 |
| 초안 | 검토 중이며 호환성을 보장하지 않음 |
| 구현 차단 | 결정 전에는 해당 기능을 운영 품질로 구현하거나 완료 처리할 수 없음 |
| 미구현 | 저장소에서 코드와 실행 검증을 확인할 수 없음 |
| 부분 구현 | 일부 코드가 있으나 전체 계약 또는 실행 검증이 완료되지 않음 |

## API 경계

| 호출자 | 허용 계약 | 금지된 의존 |
|---|---|---|
| 다른 애플리케이션 | `/api/health`, `/api/services`, `/api/registration` | `/admin/*`, `/api/sync/*`, XML 저장 파일 |
| 트레이 앱 | `/admin/*`, 와치독 Named Pipe | 동기화 피어 API 직접 호출, XML 저장 파일 직접 수정 |
| 와치독 | 헬스체크, 제한된 서비스 제어 | 디렉토리 데이터 변경 |
| 상대 서비스 디렉토리 | `/api/sync/*` | `/admin/*`, 승인 대기 큐 |

“외부”는 다른 애플리케이션이 지속적으로 의존할 안정된 공개 계약이라는 뜻이며 현재 문서 상태는 초안이다. 요청은 4바이트 ProductCode와 일일 API 키를 검증하지만, 별도 secret이 없는 프로젝트 예외이므로 이를 강한 호출자 인증으로 표현하지 않는다.

## 구현 전에 해소할 결정

다음 항목은 문서를 정리하면서 발견한 구현 차단 사항이다.

- 테스트 프레임워크와 재현 가능한 test/package 진입점. 초기 솔루션과 Debug|x64 MSBuild 진입점은 실행 확인됨
- XML `SchemaVersion`·마이그레이션과 여러 파일 변경용 트랜잭션 저널
- 내·외부 API HTTP 상태·버전을 포함하지 않는 고정 XML namespace/XSD·알 수 없는 요소 처리 정책과 External 일일 API 키 알고리즘을 불가피하게 바꿀 때 무버전 wire의 호환 전환 정책
- Peer pairing transcript의 필드별 정확한 바이트 인코딩·순서와 decision/commit의 목적별 key label·canonical MAC·재전송 방지 입력
- 코드 서명 없는 오프라인 패치 manifest·체크섬과 운영 디버그 심볼 정책

해결되지 않은 항목을 임시 규칙이나 제한 없는 네트워크 노출로 대체해 완료 처리하지 않는다. 평문 HTTP와 External 일일 API 키는 개발계획 §8.2·§8.3의 승인된 예외 범위에서만 허용하며 Admin·Peer에 확장하지 않는다.
