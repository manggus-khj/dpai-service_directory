# 서비스 디렉토리 계획 문서 안내

```text
최초 작성일: 2026-07-17
최종 변경일: 2026-07-21
revision: 36
```

> 문서 묶음 상태: 인증서 기반 목표 설계·외부/내부 API 계약 확정
> 구현 상태: 인증서 기반 External/Admin/Peer 계약, canonical DNS+IPv4 저장 schema, 9개 target 복구 journal, 즉시 등록·재등록·renewal·삭제·폐기, HTTPS 설치/기동 사전 검증, Peer pinned TLS·PKI state cache와 standby 구성·승격 소스를 연결했다. Admin 서비스 응답은 단일 목표 `admin.xsd`의 DNS+IPv4 pair를 사용하고 pending DTO·codec·legacy schema를 제거했다. Directory IPv4·hostname 두 exact HTTPS prefix 보완까지 포함한 2026-07-21 최신 작업 트리의 `Debug|x64`·`Release|x64` locked restore·빌드와 자동 테스트 663개가 각 구성에서 모두 통과했고 installer ACL round-trip·HTTPS binding rollback·installed-state·live endpoint PowerShell 회귀도 통과했다. 실제 Windows Server 재설치·DPAPI·HTTP.sys TLS·두 장비 동기화/승격·Milestone 조합 검증은 남아 있다.

이 디렉터리는 서비스 디렉토리의 제품 설계, 인증서 전환 계획, 외부·내부 API와 Directory 구조 제품 전용 보안 기준을 관리한다. 사내 `Directory서비스_애플리케이션_하드닝_가이드` 개정에 따라 원격 평문 HTTP와 외부 승인 대기 계약을 사이트 CA·HTTPS·TOFU pin·1시간 1건 등록 모드·CSR 즉시 발급 계약으로 전환한다.

문서에서 “확정”은 목표 설계 결정이며 구현 완료가 아니다. Domain·저장 directory·Admin·Peer exchange의 service identity와 remote listener·External 공개 PKI·즉시 registration·renewal route는 목표 HTTPS 구조로 전환됐고 로컬 자동 검증은 통과했다. 실제 Windows 서비스·TLS·standby 전환·Milestone 통합을 수행하지 않았으므로 인증서 전환 전체를 현장 검증 완료로 표시하지 않는다.

## 목표 운영 기준

- 현재 저장소 버전 값은 `v1.0.0 build 14`이다. 이후 버전과 build 번호 변경은 루트 `AGENTS.md` §12를 따른다.
- Milestone XProtect `2021 R1` 이상, .NET Framework 4.8, x64 전용
- 지원 OS는 x64 Windows Server 2016+ Standard·Datacenter Desktop Experience, Windows 10 1809+와 Windows 11 24H2+ Pro·Enterprise·IoT Enterprise다. Windows Server 2016은 build 14393 이상과 별도 설치한 .NET Framework 4.8을 요구하며 Server Core는 제외한다
- Milestone Management Server 주소는 같은 서버에 설치된 Directory의 위치다. 외부 앱은 성공한 Milestone session에서 `DirectoryHostName`·`DirectoryIpv4Address`를 얻고 `https://{DirectoryHostName}:21000` 또는 `https://{DirectoryIpv4Address}:21000`으로 Directory에 접속한다. 이 값은 등록할 서비스 주소가 아님
- 연결정보 파일, Directory 주소·ProductCode·CA·pin·PFX의 설치 입력 없음
- 최초 Directory 연결은 SAN·chain·CA 제약을 검증한 제한적 TOFU, 이후 site CA와 SHA-256 SPKI pin 강제
- remote External·Peer는 TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS 전용. protocol·cipher suite를 앱 코드에 고정하지 않으며 Admin·와치독 loopback은 로컬 IPC 경계로 분리
- 기본 Directory site CA, Directory HTTPS leaf, 서버 앱 CSR 발급, serial ledger와 표준 X.509 CRL 사용. CA certificate 자체에는 endpoint SAN을 넣지 않고 Directory leaf에는 Directory DNS·IPv4, 등록 서비스 leaf에는 해당 서비스 DNS·IPv4 SAN을 각각 모두 포함. 모든 leaf의 CRL Distribution Point는 Directory DNS·IPv4 각각의 absolute HTTPS URI 두 개이며 상대 URI를 넣지 않음
- 등록 서버 앱은 자기 서비스의 `ServiceHostName`·`ServiceIpv4Address`를 사용자가 선택하게 하고 제한 ACL 설정에 한 쌍으로 영속화한다. 등록 record·CSR·발급 leaf SAN은 이 두 값만 사용하며 Milestone/Directory 주소와 요청 source IP를 사용하지 않음
- Directory listener, Peer와 등록 서비스 주소는 IPv4만 지원하고 IPv6를 거부
- External 일일 API 키 알고리즘은 유지하되 strong caller identity나 request signature로 표현하지 않음
- 설정 UI의 등록 서비스 화면에서 ProductCode 입력 없는 전역 등록 모드를 열고 1시간 안의 첫 유효 요청 한 건을 즉시 등록·발급한 뒤 닫음
- 승인 대기 메뉴·상태·API 제거. 설치하는 사람은 ProductCode를 입력하지 않음
- 서비스 삭제·재등록은 인증서 폐기와 CRL publish까지 포함
- 조회 클라이언트는 Directory에서 받은 `ServiceHostName`·`ServiceIpv4Address`를 보존하고 선택한 target의 서버 인증서를 같은 site CA·pin·두 SAN·CRL로 검증
- API는 URL·media type·XML에 version 필드를 두지 않고 고정 namespace와 strict XSD 사용
- Active Directory와 Workgroup 환경 모두 지원, 원격 listener는 Domain·Private 프로필만 허용하고 Public 차단
- Admin은 loopback Negotiate와 정확한 로컬 `DEEPAi-ServiceDirectory-Operators` SID로 인가
- Peer는 HTTPS 위에서 기존 ECDH P-256·양쪽 8자리 SAS·DPAPI pair root·HMAC-SHA256 계약 유지
- 활성 서비스 최대 1,000개, 외부 호출 저빈도 전제
- 설정 UI는 라이트 테마, 일반 10pt, 기본 `800x700`·최대 `800x720`
- 일반 제거는 운영 데이터·CA·ledger·CRL·backup을 기본 보존하고 명시적 전체 삭제만 파기

## 권장 읽기 순서

| 순서 | 문서 | 책임 |
|---:|---|---|
| 1 | [Directory Service 사용 애플리케이션 하드닝 가이드](./01-hardening.md) | Directory 구조 제품 전용 추가 보안 기준 |
| 2 | [인증서 전환 변경계획](./02-certificate-transition.md) | 새 Directory 전용 가이드에 따른 차이, 목표 상태와 구현 단계 |
| 3 | [서비스 디렉토리 개발계획](./03-development.md) | 제품 구성, 데이터·복구·동기화 불변식과 전체 개발 순서 |
| 4 | [최초 정식 저장 schema v1](./03-development-01-storage-schema.md) | canonical 저장 XML·binary·교차 파일 불변식과 recovery transaction |
| 5 | [API 명세 안내](./04-api.md) | 신뢰 경계와 endpoint 소유권 |
| 6 | [외부 애플리케이션 API 명세](./04-api-01-external-application.md) | 주소 구성, TOFU·pin, 일일 키, CSR 발급·갱신, CRL과 대상 서비스 인증서 검증 |
| 7 | [내부 API 명세](./04-api-02-internal.md) | 설정 UI 등록 모드, 와치독, CA 운영과 Peer 동기화 계약 |
| 8 | [다음 개발 실행계획](./05-next-development.md) | 확정 계약을 실제 구현 단위로 나눈 선후관계·변경 위치·완료 조건 |
| 9 | [현장 검증 실행계획](./06-release-validation.md) | 설치 상태 비파괴 증거 수집 도구와 실제 OS·Milestone·두 장비 검증 순서 |

## 문서 우선순위

1. 루트 `AGENTS.md`의 공통 보안 지침과 사내 Directory 전용 하드닝 가이드가 보안 기준선이다.
2. 프로젝트별 예외와 목표 전환 결정은 인증서 전환 계획과 개발계획 §8에 기록한다.
3. 요청·응답과 호출자 절차는 해당 외부·내부 API 명세가 단일 원본이다.
4. 저장·복구·제품 구성 불변식은 개발계획이 단일 원본이다.
5. 요약과 상세가 다르면 상세 문서를 우선하되 같은 변경에서 모순을 제거한다.

## 상태 표기

| 표기 | 의미 |
|---|---|
| 목표 확정 | 후속 구현이 따라야 할 승인된 설계. 현재 코드 완료 의미가 아님 |
| 구현 기준선 | 현재 저장소에서 확인한 기존 동작 |
| 미구현 | 목표 문서는 있으나 코드·실행 검증이 없음 |
| 부분 구현 | 일부 소스가 추가됐으나 상위 구성요소 연결·실행 검증 또는 종료 조건이 남음 |
| 구현 차단 | 보안·호환성 결정 전에는 구현하면 안 됨 |
| 검증 완료 | 명시한 명령·환경에서 실제 실행 결과를 확인함 |

## 개발 phase

| Phase | 범위 | 현재 상태 |
|---:|---|---|
| 0 | 인증서 전환 계약과 외부·내부 API 확정 | 완료 |
| 1 | CA·leaf·CSR·serial·ledger·CRL PKI core | 진행 중 — 2026-07-21 `Debug|x64`·`Release|x64` 빌드와 663개 테스트 성공, 실제 DPAPI·서비스 계정 ACL 검증 미완료 |
| 2 | 최초 정식 schema와 다중 파일 저장·복구 | 진행 중 — strict codec·role별 PKI·9개 target·저장 pending 제거·exact replay/용량 preflight·복합 journal과 registration claim·serial·서명 orchestration 및 발급/journal 장애 경계 자동 테스트 통과, 실제 장애·복구 실행 대기 |
| 3 | HTTPS listener와 설치·repair·upgrade | 부분 완료 — Directory leaf·private-key ACL, exact HTTP.sys binding, IPv4·hostname 두 remote URL ACL·방화벽 rollback, 두 exact remote HTTPS prefix listener와 기동 전 identity/leaf/binding 검증 소스 완료. 실제 Windows 설치·TLS 실행 검증 대기 |
| 4 | External TOFU·등록 모드·즉시 발급·갱신 | 진행 중 — 목표 XSD·공개 DTO·strict codec, 공개 PKI·조회·즉시 등록·재등록·renewal 발급·exact replay와 overlap 폐기 runtime·테스트 소스 연결 완료. 외부 앱 TOFU/pin 상호운용 검증 대기 |
| 5 | Admin·설정 UI의 pending 제거와 등록 모드 | 완료 — 단일 목표 `admin.xsd`, 서비스 DNS+IPv4 pair·registration-mode DTO·strict codec, 1시간 first-wins owner와 `READY` active issuer 조건의 실제 route, pending route·DTO·codec·legacy schema·application handler·UI 제거와 등록 서비스 화면의 countdown·제어·마지막 결과 binding 완료; 양 구성 자동 테스트 통과 |
| 6 | 삭제·재등록 원자 폐기와 Peer HTTPS·동일 CA | 부분 완료 — 삭제·재등록 원자 commit, Peer pinned TLS·PKI state cache, 인증 backup 기반 standby 구성과 관찰 high-water 이상 backup만 허용하는 명시적 승격 소스를 연결했다. 실제 이중화 설치·동기화·승격 실행 검증은 남음 |
| 7 | 지원 OS·Milestone 조합 릴리스 검증 | 진행 중 — Windows Server 2016 build 11 최초 설치의 PowerShell 5.1 generic list 반환 실패를 재현·수정하고 build 12 생성. 최신 installer에는 읽기 전용 설치 상태 및 실제 IPv4·hostname TLS/공개 PKI/health JSON 검증 도구와 parser 회귀 소스를 연결했으며 실제 build 12 재설치·최신 package 현장 검증 대기 |

상세 종료 조건은 [인증서 전환 변경계획 §8](./02-certificate-transition.md#8-구현-단계와-종료-조건)을 따른다.

다음 단계는 [다음 개발 실행계획](./05-next-development.md)과 [현장 검증 실행계획](./06-release-validation.md)에 따라 build 12 Windows Server 2016 설치 기준선을 확인한 뒤, 완료된 계약·도메인·저장·복합 journal·HTTPS·External/Admin/Peer·standby 역할 전환 소스를 실제 지원 환경에서 통합 검증하는 것이다. 제품은 아직 배포되지 않았으므로 기존 단일 `ServerAddress`·`pending.xml` 형식은 운영 migration 대상이 아닌 개발 기준선으로 확정했다. 기존 개발·테스트 데이터 루트는 명시적으로 초기화하며 자동 추론·운영자 매핑·호환 migration 코드는 만들지 않는다. CA key rotation·dual-pin은 이번 실행계획에서 제외한다.

## API 경계

| 호출자 | 허용 계약 | 금지된 의존 |
|---|---|---|
| 외부 조회 앱 | `/pki/ca`, `/pki/crl`, `/api/health`, `/api/services` | `/admin/*`, `/api/sync/*`, 저장 파일 |
| 등록 서버 앱 | 조회 계약, `/api/registration`, `/api/certificates/renew` | 등록 모드 원격 제어, CA private key·ledger 파일 |
| 설정 UI | `/admin/*`, 와치독 Named Pipe | XML·CA 파일 직접 수정, Peer endpoint 직접 호출 |
| 와치독 | loopback health, 제한 서비스 제어 | Directory 데이터·등록 모드·CA 변경 |
| 상대 Directory | HTTPS `/api/sync/*` | `/admin/*`, 등록 모드 |

## 현재 구현과 목표 차이

| 영역 | 현재 구현 | 목표 |
|---|---|---|
| remote transport | External·Peer remote `HttpListener`는 Directory IPv4·hostname의 두 exact HTTPS prefix, Admin·WDOG는 exact loopback HTTP. 서비스 시작 전 Directory leaf·private key·binding 검증을 수행하나 실제 Windows TLS 실행은 미검증 | OS 보안 기본값의 TLS 1.2+ HTTPS, Directory 전용 DNS+IPv4 SAN과 Peer 전용 CA/pin/CRL 검증 |
| service identity | Domain·`directory.xml`·Peer exchange·External 등록은 `ServiceHostName`+`ServiceIpv4Address` pair를 사용하고 발급 leaf에 exact 두 SAN을 넣음 | 외부 앱의 pair 저장·제출과 조회 클라이언트 검증 상호운용 확인 |
| external certificate | 일일 키·ProductCode·CSR/SAN·용량 검증, 전역 1시간·1건 claim의 등록·재등록과 현재 leaf proof renewal·exact replay·overlap 폐기 runtime 연결 | 실제 외부 앱 TOFU/pin·발급·갱신 상호운용 검증 |
| UI | 승인 대기 제거, 등록 서비스 화면의 등록 모드·countdown·마지막 성공/실패 결과 연결 | 실제 서비스 실행 UI 통합 검증 |
| storage | pending 없는 `directory.xml`, active issuer full ledger·CA·CRL·idempotency와 standby 공개 Peer PKI cache 분리, operation별 복합 commit과 전체 발급/journal 장애 경계 테스트 소스 연결 | 실제 crash recovery·Peer state 통합 실행 검증 |
| installer | Site CA 초기 encrypted backup, Directory leaf/private-key ACL, IPv4·hostname 두 HTTPS URL ACL·HTTP.sys binding·방화벽과 exact rollback 소스 완료. 실제 Windows 설치 미검증 | site CA·leaf·encrypted backup·두 URL identity·HTTPS binding과 rollback의 실제 설치 검증 |
| Peer | HTTPS IPv4 endpoint + 기존 HMAC, 동일 site CA·pin·SAN·CRL 검증과 active current mapping/standby 공개 cache 원자 교환 연결 | 실제 두 장비 TLS·PKI sync·명시적 승격 실행 검증 |
| XSD/tests | External·Admin registration-mode·Peer DNS+IPv4/PKI high-water XSD·DTO·codec, 공개 PKI·registration·renewal·Peer PKI runtime과 발급/journal 장애 경계 테스트 소스 연결. 구형 External legacy 모델 제거 | 전체 테스트 실행과 지원 환경 통합 검증 |
| PKI core | CA·CSR·leaf·CRL primitive, DPAPI CA key·metadata·ledger·CRL 저장, 암호화 backup, 상태·원장·serial 폐기 Admin/UI, repair 복원·standby 구성/승격과 등록·재등록·renewal·삭제 원자 폐기 연결 | 실제 DPAPI/ACL/TLS·backup/repair·역할 전환 실행 검증. CA rotation은 후속 범위 |

구체적인 후속 변경 대상과 장애 검증은 [인증서 전환 변경계획](./02-certificate-transition.md)을 따른다.
