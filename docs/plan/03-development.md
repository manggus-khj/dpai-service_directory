# 서비스 디렉토리 개발계획

```text
최초 작성일: 2026-07-17
최종 변경일: 2026-07-22
revision: 34
```

> 설계 상태: 사이트 CA·HTTPS·TOFU pin·1시간 1건 등록 모드와 CA key rotation·dual-pin 최초 릴리스 필수 범위 확정
> 구현 상태: 기존 PKI core·등록·갱신·삭제·HTTPS·Peer·standby 기준선에 최초 정식 dual-slot state와 13개 journal target, issuer-aware ledger, B-slot Prepare/Cancel, issuer별 CRL·trust bundle, dual-key backup/active repair와 Admin/UI를 반영했다. `ACTIVATED`·Complete maintenance, retired archive, dual-CA Peer·standby와 실제 Windows 통합 검증은 후속 구현 중이다. 2026-07-21 `Debug|x64`·`Release|x64` 빌드와 663개 테스트 결과는 이번 rotation 변경 전 기준선이며 현재 작업 트리의 검증 결과가 아니다.

Milestone XProtect용 자사 소프트웨어의 접속 경로와 사이트 인증서 신뢰를 관리하는 서비스 디렉토리의 제품 범위와 구현 계획을 정의한다. 인증서 전환의 차이·구현 순서는 [인증서 전환 변경계획](./02-certificate-transition.md), 최초 정식 파일·canonical XML·journal target은 [저장 schema v1](./03-development-01-storage-schema.md), 요청·응답의 상세 계약은 [API 명세 안내](./04-api.md)와 분리된 내·외부 명세를 따른다.

## 1. 목표와 범위

- Milestone XProtect 2021 R1 이상 Management Server와 함께 설치되는 x64 Windows 제품
- Windows Server 2016 이상 Standard·Datacenter Desktop Experience, Windows 10 1809 이상 및 Windows 11 24H2 이상 Pro·Enterprise·IoT Enterprise 지원. Windows Server 2016은 build 14393 이상과 .NET Framework 4.8 사전 설치가 필요하며 Server Core는 제외
- TCP `21000` 포트에서 서비스 디렉토리 API 제공
- 별도 DB 없이 XML 파일로 상태 영속화
- Windows Service로 동작하고 설치 파일 형태로 배포
- 서비스 등록·재등록은 운영자가 설정 UI에서 ProductCode 입력 없이 연 1시간·1건 전역 등록 모드의 첫 유효 요청으로만 반영
- 등록 모드를 연 행위를 사전 승인으로 보고 승인 대기·승인·거절 queue를 제공하지 않음
- 외부 앱이 선택·영속화한 DNS hostname/FQDN 한 개와 서비스용 IPv4 한 개를 CSR에 함께 결합해 사이트 CA 서버 인증서를 발급하고 조회 클라이언트가 같은 CA·pin·두 SAN·CRL로 대상 서비스를 검증
- IPv6는 Directory listener, Peer와 등록 서비스 주소 전체에서 지원하지 않음

핵심 기능:

1. 성공한 Milestone Management Server session의 DNS hostname/FQDN·remote IPv4 pair 기반 Directory 주소 구성과 사이트 CA TOFU trust bootstrap
2. 등록 모드 중 첫 유효 CSR의 즉시 서비스 등록·인증서 발급
3. 제품코드별 활성 서비스 정보와 CA·CRL 제공
4. 인증서 자동 갱신·폐기·CA backup/restore와 계획된 CA key rotation·dual-pin 무중단 신뢰 전환
5. 두 서비스 디렉토리 인스턴스 사이의 데이터 동기화
6. 서비스 상태 감시와 제한된 로컬 서비스 제어

현재 저장소에는 x64 솔루션과 최초 정식 v1 state/config·role별 PKI store·복구 journal, HTTPS External·Peer와 loopback Admin·WDOG, 즉시 발급·갱신·폐기, Peer PKI cache 및 standby 구성·승격 소스가 있다. pending 저장·Admin·UI와 External legacy 경계는 제거했다. 2026-07-21 양 구성 자동 테스트 663개와 installer ACL·HTTPS binding 회귀는 통과했으며 외부 앱 TOFU·pin과 실제 서비스·설치·TLS·standby·Milestone 환경 검증은 후속 대상이다.

## 2. 결정 상태

### 2.1 확정된 제품 결정

| 항목 | 결정 | 근거·주의 |
|---|---|---|
| 제품·빌드 버전 | 현재 `v1.0.0 build 14` | 단일 원본은 루트 `VERSION`. 이후 버전과 build 번호 변경은 루트 `AGENTS.md` §12를 따름 |
| 언어·런타임 | C# + .NET Framework 4.8, x64 전용 | 대상 Windows 제품군과의 일관성. x86·AnyCPU 산출물은 지원하지 않으며 런타임 제공 전제는 실제 환경 검증 필요 |
| 지원 Milestone | XProtect 2021 R1 이상 | 2021 R1을 지원 하한으로 고정하고 이후 버전은 실제 설치·회귀 검증 대상으로 관리 |
| 지원 OS | Windows Server 2016+ Standard·Datacenter Desktop Experience, Windows 10 1809+ 및 Windows 11 24H2+ Pro·Enterprise·IoT Enterprise | 모두 x64 전용이며 Server Core는 제외. 서버 설치는 build 14393 이상, 클라이언트 설치는 build 17763 이상을 요구한다. Enterprise·IoT Enterprise LTSC는 버전 하한·Milestone 지원 교집합·조합 검증을 모두 충족한 release만 포함 |
| Windows 운영 환경 | AD 도메인과 Workgroup 모두 | 도메인 전용 계정·Kerberos·그룹 정책을 필수 전제로 두지 않음 |
| 메인 서비스 | Windows Service(`ServiceBase`) + `HttpListener` | 별도 웹 프레임워크 없이 Windows 서비스로 호스팅. 경로별 인증 계약은 내·외부 API 명세를 따름 |
| 트레이 UI | WPF + `H.NotifyIcon.Wpf` 2.4.1, 라이트 테마 | 일반 본문·표·메뉴는 10pt(96 DPI에서 WPF 13.333 DIP), 기본 창 `800x700`·최대 `800x720`, 제목·요약 숫자만 제한적으로 확대. 상태 아이콘은 `tray_running.png`·`tray_stopped.png` 사용 |
| 와치독 | 별도 경량 Windows Service | 메인 프로세스·리스너 장애를 외부에서 감시 |
| 저장소 | XML + `XmlSerializer` | DB 없이 배포. 원자 교체뿐 아니라 다중 파일 복구 설계 필요 |
| 설치 | Inno Setup, 저장소 루트 `installer\` 출력 | 서비스·방화벽·자동 시작을 하나의 설치 흐름으로 관리. 설치 EXE만 출력하고 코드 서명·설치용 체크섬·manifest와 PDB는 포함하지 않음 |
| API 포트 | TCP `21000` | 제품 간 고정 접속 계약 |
| API 호환성 | URL·payload·협상에 API 버전을 두지 않고 목표 무버전 경로를 유지 | 현재 외부 소비자가 없으므로 미출시 HTTP·pending 계약은 이번 전환에서 대체한다. 목표 계약 최초 공개 뒤에는 기존 필드·의미를 바꾸지 않고 선택적 확장점 또는 별도 endpoint로만 확장 |
| API XML·HTTP | External·Admin·Peer별 고정 무버전 namespace와 XSD, 성공만 HTTP 200 | strict 요청 검증과 표준 HTTP 상태 매핑은 내·외부 API 상세 명세가 단일 원본 |
| 전송 프로토콜 | remote External·Peer는 TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS | protocol·cipher suite를 앱 코드에 고정하지 않고 지원 OS에서 구버전 비활성화를 검증. Directory site CA와 strict 검증을 사용하며 remote HTTP·redirect fallback 금지 |
| ProductCode | `[A-Z0-9]{4}` 형식의 4바이트 ASCII | trim·대문자 정규화 후 `OrdinalIgnoreCase` 유일 키로 사용 |
| External 일일 API 키 | ProductCode+시스템 로컬 `yyyyMMdd`를 날짜 파생 AES key로 암호화한 44자 Base64 | 알고리즘은 유지하되 strong identity로 표현하지 않음. HTTPS와 1시간·1건 등록 모드의 보완 통제로 초기 발급 admission에 사용하는 §8.3 예외 |
| Admin 인증·인가 | `127.0.0.1` loopback `HttpListener`의 Negotiate와 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹 | IP literal에서는 Kerberos를 전제로 하지 않고 AD·Workgroup 모두 NTLM을 허용. 검증된 hostname·SPN 환경만 Kerberos 선택 가능, 연결 단위 NTLM 자격 증명 재사용 금지 |
| Peer 페어링·인증 | HTTPS 위 ECDH P-256 일회성 키 교환, 양쪽 8자리 SAS 확인, DPAPI 저장, HMAC-SHA256 요청·응답 서명 | TLS는 전송·서버 identity, 기존 HMAC은 peer identity·message integrity·replay 경계로 함께 유지 |
| 네트워크 노출 | 고정 원격 IP·CIDR allowlist 미사용 | 승인된 폐쇄망 인터페이스에만 바인딩하고 방화벽은 Domain·Private 프로필만 허용하며 Public 프로필은 차단 |
| 원격 Directory identity | 성공한 Milestone Management Server session의 DNS hostname/FQDN 한 개 + 실제 remote IPv4 한 개 + TCP `21000` | 두 값을 같은 identity로 저장하고 Directory leaf SAN에 둘 다 필수 포함. 외부 앱은 DNS 또는 IPv4 base를 선택하되 인증서 실패 fallback 금지. 연결정보 파일·Directory 주소 입력 없음 |
| listener 요청 결합 | IP literal prefix와 실제 local endpoint를 함께 검증 | HTTP Server API의 IP-bound weak wildcard prefix를 보안 경계로 신뢰하지 않고 External·Peer는 `ListenAddress:21000`, Admin·와치독은 `127.0.0.1:21000`과 loopback remote address를 매 요청 확인 |
| IP 지원 범위 | IPv4 only | Directory·External service·Peer·installer·firewall·certificate IP SAN 전체에서 IPv6를 거부. 미배포 구현의 IPv6 입력 허용은 최초 정식 v1에서 제거 |
| 등록 서비스 network identity | 필수 DNS hostname/FQDN 한 개 + 서비스용 IPv4 한 개 | 등록 앱 사용자가 두 값을 선택하고 앱이 제한 ACL 설정에 한 쌍으로 영속화. 요청 source IP·DNS 역조회로 생성·교정하지 않고 leaf SAN에 둘 다 포함 |
| 지원 규모 | 활성 서비스 1,000개, 외부 호출 저빈도 | 외부 앱은 통상 최초 로그인 시 1회 조회. 톰스톤은 활성 수에 포함하지 않고 sync에서 batch 처리 |
| 주기 동기화 | 10분 | 즉시 동기화 실패 보정 |
| 즉시 동기화 | 신규 등록, 재등록, 삭제·인증서 폐기 직후 | 정상 시 두 인스턴스의 지연 최소화 |
| 병합 논리 버전 | unsigned 64-bit `LogicalVersion`과 `LogicalClock` | 병합은 `(LogicalVersion, OriginInstanceId)`로 결정하며 UTC 시각은 표시·감사에만 사용 |
| 시계 편차 한계 | Peer 인증 timestamp가 60초를 초과하면 거부 | HMAC freshness·재전송 방지 조건이며 병합 순서 조건이 아님 |
| 시스템 로그 보존 | 기본 30일, 설정 범위 1~1,095일 | 오늘을 포함한 시스템 로컬 달력 날짜 수. 최대 3년은 일 단위 상수 1,095일로 정의 |
| 보안 진단 로그 | Windows `Application` Event Log의 `DEEPAi.ServiceDirectory.Security` source | 인증·인가와 endpoint 경계 거부만 별도 기록하고 9개 시스템 파일 이벤트와 분리 |
| 등록 모드 | 설정 UI 등록 서비스 화면의 ProductCode 입력 없는 전역 1시간·1건 창 | 첫 유효 요청이 claim하고 즉시 등록·발급 뒤 닫힘. 트레이 menu·installer ProductCode 입력·승인 대기 없음 |
| 제거 기본 정책 | 운영 데이터 기본 보존, 명시적 전체 삭제만 파기 | 일반 제거는 데이터·CA·ledger·CRL·backup과 peer 자격 증명을 유지 |
| 트레이 시작 | 사용자 로그인 시 자동 실행 | 상태·설정 UI 접근성 |
| 톰스톤 정리 | 시간으로 제거하지 않음. 같은 ProductCode의 신규 등록 commit에서만 대체 | 장기 단절 뒤 삭제 항목 부활 방지 |

WinForms, 서비스 내장 웹 UI, Electron/Tauri, Avalonia/MAUI는 현재 범위에서 제외한다. 기술 선택을 바꾸거나 새 런타임·UI 스택을 추가하려면 근거와 영향 범위를 먼저 문서화한다.

### 2.2 외부 환경 전제

Milestone XProtect 2021 R1 이상과 x64를 전제로 Windows Server 2016 이상 Standard·Datacenter의 Desktop Experience, Windows 10 1809 이상과 Windows 11 24H2 이상의 Pro·Enterprise·IoT Enterprise를 지원 범위로 확정했다. Windows Server 2016은 OS build 14393 이상이어야 하며 .NET Framework 4.8을 별도로 설치해야 한다. WPF 트레이를 포함한 제품 구성이므로 Windows Server Core는 지원하지 않는다. Enterprise·IoT Enterprise LTSC는 버전 하한과 해당 Milestone 릴리스의 OS 지원 범위를 모두 충족하고 조합 검증을 통과한 release만 포함한다. 실제 배포 조합은 해당 Milestone 릴리스가 지원하는 OS와의 교집합으로 제한하고, 각 조합에서 최신 보안 업데이트·.NET Framework 4.8·서비스·WPF·설치 동작을 릴리스 환경에서 검증한다. Windows Server 2016의 Microsoft extended support는 2027-01-12 종료 예정이므로 그 전에 제품 지원 지속 여부와 보완 통제를 다시 검토한다. 근거는 [Milestone XProtect Expert 2021 R1 시스템 요구사항](https://www.milestonesys.com/support/help-and-documentation/system-requirements/xprotect-expert-2021-r1/), [Microsoft Windows Server 릴리스 정보](https://learn.microsoft.com/en-us/windows/release-health/windows-server-release-info), [.NET Framework 시스템 요구사항](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements), [Windows Server 2016 수명 주기](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2016)를 따른다.

### 2.3 확정된 구현 기반 결정

| 영역 | 확정 결정 | 현재 구현 상태 |
|---|---|---|
| 프로젝트 기반 | .NET Framework 4.8 x64용 `MSTest.TestFramework`·`MSTest.TestAdapter` exact `4.3.2`, PackageReference lock, `tools/test.ps1`와 `tools/package.ps1` 표준 명령 계약 | 테스트 프로젝트·lock·test/package 진입점과 Inno Setup source 구현. 2026-07-21 locked restore·`Debug|x64`·`Release|x64` build와 663개 test 통과. 최신 package 생성은 별도 요청 전 미수행 |
| 인증서 구현 라이브러리 | `BouncyCastle.Cryptography` exact `2.6.2` | .NET Framework 4.8에 PKCS#10 CSR·X.509 발급·CRL 생성 API가 없어 Infrastructure와 테스트에서 사용. net461 asset, package dependency 없음, MIT 고지·lock 반영. PKI core와 테스트 소스의 `Release|x64` 컴파일 및 자동 테스트 성공 |
| leaf CRL Distribution Point | 한 fullName의 DNS·IPv4 absolute HTTPS issuer별 URI 두 개 | URI형 GeneralName에 상대 path를 넣지 않고 두 Directory identity에 같은 issuer CA serial path를 사용한다. 현재 단일 `/pki/crl` 구현은 최초 릴리스 전 current alias로 한정 |
| 저장 복구 | 미배포 XML v1·journal 기준선을 rotation 포함 최초 정식 v1로 교체 | 현재 role별 PKI strict store, 9개 fixed target, pending 제거와 단일 CA transaction을 연결했다. 최초 릴리스 전 fixed A/B slot·issuer별 CRL·retired archive와 확장 journal로 개정 |
| 운영 | 코드 서명과 설치용 체크섬·manifest를 생성·전달·검증하지 않고 수동 오프라인 설치 EXE를 직접 실행하는 §8.4 승인 예외 | Inno Setup package compile과 단일 EXE 생성 성공. 실제 설치·repair·upgrade·rollback·uninstall 실행 검증 미완료 |

이 표의 결정은 구현 완료가 아니다. remote 평문 HTTP 예외는 폐기하고 사이트 CA·HTTPS로 전환한다. External 일일 API 키 발급 예외와 무검증 오프라인 패치는 각각 [§8.3](#83-external-일일-api-키-발급-예외-기록)·[§8.4](#84-코드-서명체크섬-없는-오프라인-패치-예외-기록)의 승인 범위에서만 사용한다.

### 2.4 현재 구현 기준선

| 프로젝트·파일 | 현재 책임 | 검증 상태 |
|---|---|---|
| `DEEPAi.ServiceDirectory.Domain` | ProductCode·서비스 입력 검증, immutable snapshot·활성 조회, logical clock·결정적 sync, 분리된 Directory/service DNS+IPv4 identity와 certificate ledger 상태 | `ServiceDefinition`·record·snapshot을 DNS+IPv4 pair로 전환하고 Directory identity 혼용·IPv6 거부를 양 구성 자동 테스트로 검증 |
| `DEEPAi.ServiceDirectory.Application` | snapshot load·commit 결과와 저장 계약, 공용 mutation gate, commit 성공 뒤 snapshot 게시와 recovery 차단·재적재, 승인 서비스 조회, 다중 inbound sync batch staging | `Release|x64` 빌드와 자동 테스트 성공. 실제 process 강제 종료 검증 미완료 |
| `DEEPAi.ServiceDirectory.ExternalProtocol` | 44자 일일 API 키, exactly-one header·ProductCode 검증, External DTO·strict XML codec와 PKI·조회·등록·갱신 handler | 공개 `/pki/ca`·`/pki/crl`, 목표 조회와 즉시 등록·재등록·renewal·exact replay를 remote HTTPS transport에 연결. 외부 앱 TOFU/pin과 HTTPS 실행 검증은 미완료 |
| `DEEPAi.ServiceDirectory.Infrastructure` | 저장 XML·config/state/DPAPI peer store·복구 journal, 로그·보안 진단, External·Admin·`WDOG` HTTP handler, Admin application handler, Peer pairing/session·인증 HTTP exchange·scheduler, 공용 native DLL 검색 정책, PKI core | role별 strict PKI storage, Directory leaf machine-store 설치/private-key ACL, native HTTP.sys binding 검증과 HTTPS IPv4 listener, Peer CA pin/SAN/CRL 검증·PKI state cache transaction, standby 구성·승격 repair 소스 완료. 실제 DPAPI/ACL/TLS·role 전환 실행은 미검증 |
| `DEEPAi.ServiceDirectory.InternalProtocol` | embedded `admin.xsd`·`peer.xsd` 기반 Admin·Peer strict DTO/XML codec, canonical Peer endpoint와 Exchange·pairing·handshake·release·revoke wire 모델 | Admin 서비스 DNS+IPv4 pair·registration-mode와 Peer PKI state codec으로 전환하고 pending DTO·codec·`legacy-admin.xsd`를 제거. `Debug|x64`·`Release|x64` 자동 테스트 통과, 실제 network 통합 미검증 |
| `DEEPAi.ServiceDirectory.Service` | x64 Windows Service composition, 설치 identity 검증, 공용 `HttpListener`, endpoint별 인증·deadline·drain, runtime fatal 전파 | CA `READY`, config/NIC/profile/Directory leaf/private key/native binding 검증 뒤 HTTPS remote listener 구성 및 양 구성 빌드 완료. SCM·TLS·Negotiate/NTLM 실제 실행 미검증 |
| `DEEPAi.ServiceDirectory.Tray` | WPF 라이트 테마 상태 모니터, 일반 10pt, 기본 `800x700`·최대 `800x720` 창, 제공 PNG tray icon, loopback Negotiate Admin client, 제한된 와치독 Named Pipe client, 페이지·429 backoff·polling과 관리 명령 UI | 설정 화면에 CA 상태·암호 확인을 거치는 암호화 backup·인증서 원장·serial 폐기를 Admin API로 연결하고 `Release|x64` 빌드·자동 테스트 성공. 실행·DPI·접근성·실제 통합 미검증 |
| `DEEPAi.ServiceDirectory.Watchdog` | 별도 x64 Windows Service, `WDOG` health 10초/전체 3초 deadline, 3회 실패·10분 3회 restart latch, 제한 ServiceController, 보호 Named Pipe server | 소스·계약 테스트와 설치 helper 구성 추가, `Release|x64` 빌드·계약 테스트 성공. 서비스 설치·SCM/pipe·AD/Workgroup 실행 검증 미실행 |
| `Directory.Build.props/targets`, `tools/*.ps1`, `installer`, `tests` | 루트 `VERSION` metadata, Release PDB 정책, x64 build/test/package 진입점, Inno 설치 흐름과 계약 테스트 소스 | 기존 build 12 package 성공 뒤 초기 CA backup·Directory leaf/private-key ACL·HTTPS binding/rollback과 binding 회귀 테스트 소스를 추가. 2026-07-21 양 구성 663개 test와 ACL·HTTPS binding 회귀 통과. 최신 package와 실제 재설치 미검증 |

메인 Windows Service, 실제 `HttpListener` host, Admin handler, Peer 상태/session/DPAPI·sync scheduler, config·PeerSecret 복합 저장과 Inno Setup 소스는 현재 작업 트리에 추가됐다. 2026-07-21 최신 locked restore·`Debug|x64`·`Release|x64` build와 663개 테스트, HTTPS binding·installed-state·live endpoint PowerShell 회귀가 통과했다. 최신 package 생성과 Windows 서비스·실제 설치 검증 전에는 제품 기능 전체를 운영 완료로 세지 않는다.

## 3. 구성요소와 책임

### 3.1 메인 서비스

- 활성 디렉토리, 톰스톤, 등록 모드, 인증서 ledger·CRL·CA 상태와 동기화 설정의 유일한 소유자
- HTTPS External·Peer, loopback Admin·와치독 API 처리
- 기동 시 검증된 XML을 메모리에 적재하고 조회는 메모리에서 처리
- 변경 시 원자적·복구 가능한 방식으로 영속화
- 동기화 스케줄과 병합 수행
- UI 없음

### 3.2 와치독

- `ServiceController`로 메인 서비스 상태 확인
- `GET /api/health`로 리스너 생존 여부 확인
- 정책상 허용된 실패에서 메인 서비스 재시작과 연속 실패·10분 재시작 수·자동 재시작 suppression 진단 상태 갱신
- Named Pipe로 제한된 서비스 제어 명령 수신
- 디렉토리 데이터, 등록 모드·PKI와 동기화 설정은 변경하지 않음
- 메인 서비스와 상호 의존하지 않고 먼저 시작해 나중에 종료

health는 10초 주기로 호출하고 요청 timeout은 3초다. 프로세스 상태와 health 실패가 3회 연속일 때만 재시작을 판단하며, 10분 안에 3회 재시작한 뒤에는 자동 재시작을 중단하고 운영자 경고 상태로 전환한다.

와치독을 `LocalSystem`으로 실행한다고 미리 고정하지 않는다. 필요한 서비스 제어 권한만 가진 계정을 우선 설계하고, 불가능할 때만 승인된 보안 예외를 기록한다.

### 3.3 서비스 컨트롤 트레이 앱

- 일반 사용자 권한으로 로그인 시 자동 실행
- 트레이 메뉴: 시작, 종료, 재시작, 동기화, 상태 모니터
- 상태 모니터:
  - 등록 서비스 목록, 인증서 상태·만료와 확인 후 삭제·폐기
  - 등록 서비스 화면의 전역 등록 모드 상태·남은 시간·시작·종료
  - 피어 설정, 동기화·해제, 마지막 결과와 시계 편차
  - 설정: 시스템 로그 보존기간(일), CA 상태, 암호화 backup, 인증서 원장 조회·serial 폐기
  - 메인 서비스와 와치독 연결 상태
- 화면은 라이트 테마로 고정한다. 일반 본문·표·메뉴의 10pt는 96 DPI 기준 WPF `FontSize=13.333` DIP로 구현하고, 기본 창은 `800x700`, 최대 크기는 `800x720`으로 제한한다. 창 제목·섹션 제목·요약 숫자만 정보 계층에 필요한 범위에서 확대한다.
- 좌측 메뉴는 대시보드, 등록 서비스, 동기화, 설정 순서다. `승인 대기` 메뉴·화면은 제거한다. 등록 모드는 ProductCode를 입력하지 않는 1시간·1건 전역 창이며 `등록 서비스` 화면에서만 조작한다.
- 등록 서비스 화면 상단에는 `닫힘`·`등록 가능`·`등록 처리 중`, `HH:mm:ss` 남은 시간, 시작·종료 버튼, 첫 유효 ProductCode 한 건만 처리한다는 경고와 마지막 등록·인증서 결과를 표시한다.
- 트레이 context menu에는 등록 모드 시작·종료를 추가하지 않는다.
- 트레이 상태 아이콘의 원본 `tray_icons.psd`와 실제 사용 파일 `tray_running.png`·`tray_stopped.png`는 모두 `docs/plan/03-development/`에 둔다. 실제 `RUNNING`에서만 실행 중 아이콘을 사용하고, `STOPPED`·상태 확인 실패에는 중지 아이콘을 사용하면서 tooltip에 정확한 상태 또는 연결 오류를 표시한다.
- 상태 창이 열려 있을 때 sync와 등록 모드는 5초, 서비스 목록은 10초 주기로 갱신하고 창이 숨겨지면 목록 polling을 중단
- 목록 polling은 `pageSize=250`의 현재 표시 페이지와 `TotalCount`를 사용한다. 동시 polling을 중복 실행하지 않고 `429 Retry-After` 동안 자동 polling을 멈춘다.
- XML 파일과 피어 API에 직접 접근하지 않음

권한이 필요한 작업은 와치독 또는 인증된 Admin API에 위임한다. UAC를 피하려고 IPC 인가를 생략하지 않는다.

### 3.4 설치 프로그램

- 실행 파일을 `%ProgramFiles%\DEEPAi\ServiceDirectory\` 아래에 설치
- 상태와 로그를 제한된 ACL의 `%ProgramData%\DEEPAi\ServiceDirectory\` 아래에 저장
- 두 서비스를 독립 자동 시작으로 등록
- 최초 설치에서 Directory site CA와 local Management Server DNS hostname/FQDN 한 개·선택 `ListenAddress` IPv4 한 개를 SAN에 모두 가진 HTTPS leaf를 자동 생성하고 HTTP.sys HTTPS binding을 구성. CA certificate 자체에는 host/IP SAN을 넣지 않음
- 설치하는 사람에게 ProductCode, Directory 주소, CA certificate·pin 또는 외부 앱 PFX를 입력받지 않음
- 암호화 CA backup의 저장·repair 복원 절차를 제공하고 평문 private key export를 금지. 최초 설치는 제한된 `BACKUP_REQUIRED`로 끝날 수 있으나 backup 완료 전에는 등록·발급·폐기를 허용하지 않음
- TCP `21000` 인바운드는 Domain·Private 방화벽 프로필에서만 허용하고 Public 프로필에서는 차단. 고정 원격 IP·CIDR allowlist는 구성하지 않음
- interactive 설치에서는 활성 Domain·Private 네트워크 인터페이스의 non-loopback canonical IPv4 하나를 External·Peer `ListenAddress`로 선택하게 하고, unattended 설치에서는 명시적 IPv4 `/ListenAddress=` 값을 필수로 받음. IPv6, APIPA, multicast, loopback, wildcard, broadcast와 선행 0 표기를 거부한다. 유효한 IPv4가 없으면 설치를 중단하고 자동 주소·wildcard로 대체하지 않음. Admin·와치독 loopback prefix는 별도로 구성
- 선택 IPv4와 로컬 Management Server hostname/FQDN의 두 exact HTTPS URL ACL, 선택 IPv4의 certificate binding과 loopback URL ACL, 프로그램·포트 방화벽 규칙과 `config.xml`을 함께 구성. 주소·hostname·SAN 변경은 installer repair에서 서비스를 멈춘 뒤 검증·leaf 재발급·변경·재기동하며 실패 시 이전 설정으로 롤백
- 로컬 운영자 그룹 `DEEPAi-ServiceDirectory-Operators`를 만들고 AD 사용자·그룹 또는 Workgroup 로컬 사용자를 구성 가능하게 함
- 메인과 와치독은 각각의 Windows 가상 서비스 계정과 서비스 SID로 실행하고 필요한 파일·서비스 제어 ACL만 부여
- 트레이 로그인 자동 시작 등록
- 일반 제거는 서비스, 실행 파일, URL ACL·HTTPS binding, 방화벽 규칙과 자동 시작만 제거하고 `%ProgramData%\DEEPAi\ServiceDirectory\`의 데이터·설정·로그·CA·certificate ledger·CRL·backup·`secrets/peer.dat`은 제한 ACL 그대로 보존
- 사용자가 복구 불가능한 전체 삭제를 명시적으로 선택하고 확인한 경우에만 정확한 데이터 루트를 제거하며, unattended 설치는 별도 명시 인수가 없으면 항상 보존

경로의 회사명은 `DEEPAi`로 확정한다. 제품 디렉터리 이름은 `ServiceDirectory`를 사용한다.

## 4. 통신 아키텍처

```text
[트레이 앱: 일반 권한]
   │  Named Pipe: 서비스 제어
   ▼
[와치독: 최소 권한 서비스] ── 상태 확인·헬스체크 ──▶ [메인 서비스: TCP 21000]
                                                        ▲          │
[트레이 앱] ── loopback Admin API ──────────────────────┘          │ Peer API
                                                                   ▼
                                                     [상대 메인 서비스]

[다른 애플리케이션] ── HTTPS External·PKI API ─────────▶ [메인 서비스]
```

| 경로 | 계약의 단일 원본 | 보안 경계 |
|---|---|---|
| 다른 앱 → 메인 | [외부 애플리케이션 API](./04-api-01-external-application.md) | Milestone/Directory hostname·IPv4 쌍 기반 HTTPS + site CA TOFU/pin + 일일 키 + 등록 모드·CSR + Domain·Private 방화벽 프로필 |
| 트레이 → 메인 | [내부 API의 Admin](./04-api-02-internal.md#4-admin-api) | loopback HTTP + Negotiate Windows identity + 로컬 운영자 그룹 인가 |
| 메인 ↔ 피어 | [내부 API의 동기화](./04-api-02-internal.md#5-피어-동기화-데이터) | HTTPS site CA 검증 + ECDH/SAS 페어링 + DPAPI pair root + HMAC-SHA256·freshness·replay 검증 |
| 트레이 → 와치독 | [내부 API의 Named Pipe](./04-api-02-internal.md#6-named-pipe-서비스-제어-계약) | 로컬 운영자 그룹 ACL + 명령 허용 목록 |

## 5. 데이터와 저장

### 5.1 도메인 레코드

| 필드 | 소유권과 의미 |
|---|---|
| `Name` | 애플리케이션이 등록 요청에 제공하는 표시 이름. trim 후 1~128 Unicode scalar, UTF-8 최대 512바이트, 제어문자와 XML 1.0에서 기록할 수 없는 `U+FFFE`·`U+FFFF` 금지 |
| `ProductCode` | trim 후 `ToUpperInvariant()`로 정규화한 `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII. `OrdinalIgnoreCase`로 비교하는 유일 키 |
| `ServiceHostName` | 등록 외부 앱이 자기 서비스용으로 선택·영속화한 DNS hostname/FQDN 한 개. trim 후 소문자 canonical ASCII, 전체 최대 253자, label별 1~63자 영문·숫자·하이픈만 허용한다. 빈 label, label 선후행 하이픈, wildcard, 마지막 점, scheme·path·query·port와 숫자만인 단일 label을 거부한다. Directory/Milestone hostname과 DNS 역조회 값으로 대체하지 않는다. |
| `ServiceIpv4Address` | 등록 외부 앱이 자기 서비스 listener용으로 선택·영속화한 canonical dotted-decimal IPv4 한 개. 정확히 4개의 `0..255` 10진수 octet과 `0` 외 선행 0 금지를 적용하고 loopback, `0.0.0.0`, APIPA, multicast와 limited broadcast `255.255.255.255`를 거부한다. subnet mask 없이는 directed broadcast를 문자열만으로 판정하지 않으며 앱의 실제 NIC·listener 검증에서 차단한다. IPv6와 IPv4-mapped IPv6를 지원하지 않으며 Directory/Milestone IPv4, TCP source IP와 관찰한 route로 대체하지 않는다. |
| `Port` | `1..65535` 포트 |
| `LastModifiedUtc` | 활성 변경이 발생한 UTC 표시·감사 시각. 병합 순서에는 사용하지 않음 |
| `Deleted`, `DeletedUtc` | 삭제 전파를 위한 톰스톤과 삭제 UTC 표시·감사 시각. 병합 순서에는 사용하지 않음 |
| `LogicalVersion` | 해당 활성 레코드 또는 톰스톤의 `1..18446744073709551615` 논리 버전 |
| `OriginInstanceId` | 마지막 변경 출처. 같은 `LogicalVersion`의 결정적 타이브레이크 |

`ServiceHostName`과 `ServiceIpv4Address`는 분리할 수 없는 등록 서비스 network identity다. 둘 다 등록 요청·`directory.xml`·Peer snapshot·외부 조회·서비스 leaf의 DNS/IP SAN에 같은 값으로 유지한다. 이 쌍은 Directory 접속용 `DirectoryHostName`·`DirectoryIpv4Address`와 별개다.

`LogicalVersion`과 `OriginInstanceId`가 revision identity다. 현재 송신자 ID를 마지막 변경 출처로 대신하면 전달 경로에 따라 결과가 달라질 수 있으므로 원래 `OriginInstanceId`를 레코드에 보존한다. 두 필드는 외부 조회 DTO에 노출하지 않는다.

### 5.2 파일

| 파일 | 내용 |
|---|---|
| `directory.xml` | 최초 정식 `SchemaVersion="1"`에서 `ServiceHostName`·`ServiceIpv4Address` 필수 쌍을 가진 활성 레코드와 톰스톤, `LogicalClock` high-water mark. 인증서 serial·상태는 중복 저장하지 않음 |
| `pending.xml` | build 12 이하 개발 기준선의 승인 대기 파일. 제품이 아직 배포되지 않았으므로 최초 정식 저장 형식에는 포함하지 않고 호환 migration 없이 제거 |
| `config.xml` | IPv4 listener, Directory 접속 identity인 `DirectoryHostName`·`DirectoryIpv4Address`, 동기화, `InstanceId`, peer epoch와 로그 보존기간. SiteId·CA role·rotation은 저장하지 않음 |
| `pki/state.xml` | rotation phase·TrustRevision·current/other fixed slot과 issuer별 CRL high-water를 저장하는 최초 정식 v1 |
| `pki/ledger.xml` | active issuer 전용 full ledger. 최초 정식 v1은 각 발급 entry에 issuer CA serial을 넣어 current·retiring issuer를 구분 |
| `pki/peer-cache.xml` | standby 전용 공개 PKI cache. 최초 정식 v1은 dual-pin bundle, issuer별 CRL high-water와 ProductCode별 current leaf issuer를 저장하며 full ledger로 사용하지 않음 |
| signed CRL | site CA별 canonical DER X.509 CRL. 새 leaf CDP는 issuer CA serial 고정 경로를 사용 |
| CA key/certificate | 최초 정식 v1은 fixed A/B slot으로 current+next 또는 current+retiring CA를 최대 두 개 보유한다. DPAPI·서비스 SID ACL의 private key primary는 active issuer만 보유하고 standby는 backup 처리 중에만 메모리 사용. Directory HTTPS leaf/private key는 Windows certificate store·HTTP.sys binding이 소유 |
| `secrets/peer.dat` | ECDH 페어링으로 파생한 pair root와 peer identity·key epoch의 DPAPI LocalMachine 보호값 |

현재 소스의 `directory.xml`, `pending.xml`, `config.xml`과 단일 CA `SchemaVersion="1"` 형식은 배포되지 않은 개발 기준선이다. rotation을 포함한 인증서 형식을 최초 정식 `SchemaVersion="1"`로 다시 확정하고 pending 없이 directory·config·dual-slot role별 PKI state를 생성한다. build 14 이하 개발 데이터와 `DPAICAB1` backup은 자동 추론·운영자 매핑·호환 migration 대상으로 읽지 않고 개발·테스트 환경에서 명시적으로 데이터 루트를 초기화한다. 저장 schema는 API version이 아니다. `secrets/peer.dat`과 각 CA slot private key는 목적·ACL·backup을 분리한다. 정확한 정식 v1 형식은 [저장 schema](./03-development-01-storage-schema.md)를 [rotation 계획 §8](./07-ca-key-rotation.md#8-최초-정식-저장-schema-v1-개정계획)에 따라 먼저 개정해 단일 원본으로 사용한다.

위 상태 파일은 `%ProgramData%\DEEPAi\ServiceDirectory\` 데이터 루트 아래에 둔다. 로그 파일은 §9의 하위 경로를 사용한다.

### 5.3 저장 불변식

- 기동 시 한 번 로드하고 스키마·필수 값·중복 키를 검증한 뒤 메모리에 게시한다.
- 변경이 있을 때만 같은 볼륨의 임시 파일에 작성하고 flush한 뒤 원자적으로 교체하며 백업을 유지한다.
- installer는 진짜 최초 설치에서만 canonical empty directory, 최초 config, site CA·Directory leaf, empty certificate ledger·CRL을 생성한다. ProductCode는 입력·저장하지 않는다. 재설치·repair는 보존 PKI state를 빈 상태로 초기화하지 않는다.
- 파일 핸들을 상시 유지하지 않는다.
- 디렉토리·등록 모드 claim·인증서 ledger·CRL·설정 변경과 sync 최종 병합·게시는 인스턴스당 하나의 state mutation gate로 직렬화한다. TLS·본문 수신·CSR parse는 gate 밖에서 수행하고 gate 안에서 현재 state를 다시 검증해 저널·원자 저장과 snapshot 교체를 완료한다.
- `LogicalClock`은 빈 저장소에서 `0`이고 모든 record `LogicalVersion` 이상이어야 한다. 등록·재등록·삭제는 mutation gate 안에서 `LogicalClock+1`을 발급해 record, certificate ledger와 필요한 CRL 변경을 같은 복구 단위로 영속화한다.
- `LogicalClock=18446744073709551615`에서는 wrap·초기화·재사용하지 않고 `LOGICAL_CLOCK_EXHAUSTED` 운영 오류로 변경 전체를 실패시키며 현재 상태를 유지한다.
- backup·journal·checkpoint 복구에서도 마지막 발급 high-water를 감소시키지 않는다. 마지막 값을 증명할 수 없으면 낮은 값으로 조용히 재개하지 않고 fail-closed 후 인증된 피어 복구 또는 명시적 운영 복구를 요구한다.
- 조회는 현재 immutable snapshot을 사용해 mutation gate를 점유하지 않는다. sync 네트워크 교환 중 생긴 로컬 변경은 최종 병합 시 현재 snapshot에 보존하고 다음 sync 사이클에서 피어로 전파한다.
- 등록·재등록·삭제처럼 directory·ledger·certificate artifact·CRL이 함께 바뀌는 작업은 §5.4 journal transaction으로 중간 종료 뒤 serial 재사용·등록/폐기 분리 없이 복구한다.
- ledger는 ProductCode별 `CURRENT` 인증서를 하나만 허용한다. 같은 SAN renewal overlap의 이전 인증서는 `RETIRING`과 `ScheduledRevocationUtc`로 보존하고 새 인증서를 `CURRENT`로 게시하며, 예정 시각 또는 즉시 폐기 transaction에서 `REVOKED`·`RevokedUtc`·의미 있는 reason으로 단방향 전이한다. 정기 교체는 `Superseded`, 서비스 삭제는 `CessationOfOperation`이며 `Unspecified`, 임시 `CertificateHold`와 `RemoveFromCRL`은 허용하지 않는다. CRL의 `thisUpdate`·`nextUpdate`는 CA 유효기간 안에서 증가 순서를 지키고 미래 `RevokedUtc` entry를 싣지 않는다. `RETIRING`은 Peer `ActiveCertificates` current mapping에는 포함하지 않지만 CRL 반영 전까지 유효 serial이므로 ledger history에서 제거하지 않는다.
- 손상 파일, 백업 복구, 디스크 부족, 권한 실패와 강제 종료를 명시적으로 처리하고 오류를 숨기지 않는다.
- 트레이, 와치독, 다른 애플리케이션은 파일을 직접 읽거나 수정하지 않는다.
- 정상 지원 규모는 활성 서비스 1,000개다. `Deleted=true` 톰스톤은 활성 수에는 포함하지 않으며 임의 삭제하지 않고 안정된 sync snapshot을 batch로 나눈다.
- 등록 모드는 process memory의 전역 한 개이며 ProductCode를 저장하지 않고 1시간·유효 요청 1건으로 제한한다. 재시작 시 항상 닫힌다.

#### 5.3.1 `config.xml` v1 canonical 형식

`config.xml`은 namespace 없는 `<Config SchemaVersion="1">` 루트 아래에 다음 필수 요소를 정확한 순서로 한 번씩 둔다. `ListenAddress`, `InstanceId`, `LastPeerKeyEpoch`, `LogRetentionDays`, `Sync` 밖의 루트 요소·속성은 허용하지 않는다.

| 순서 | 요소 | 저장 규칙 |
|---|---|---|
| 1 | `ListenAddress` | 현재 미배포 v1 구현은 설치가 선택한 canonical non-loopback unicast IPv4·IPv6 literal을 저장한다. 최초 정식 v1 형식은 canonical IPv4만 허용한다. 구형 개발 파일의 IPv6 값을 migration하지 않고 해당 데이터 루트를 명시적으로 초기화한다. 대괄호·port 없이 저장하며 일반 runtime 설정 commit에서는 불변이다. 주소 변경은 installer repair 전용 commit과 서비스 중지·URL ACL·방화벽·재기동·롤백 흐름에서만 수행한다. |
| 2 | `InstanceId` | 빈 값이 아닌 소문자 `D` GUID. 최초 설치 뒤 변경하지 않는다. |
| 3 | `LastPeerKeyEpoch` | 앞자리 0 없는 `0..18446744073709551615` unsigned decimal high-water. peer 폐기 뒤에도 삭제·감소시키지 않는다. |
| 4 | `LogRetentionDays` | 앞자리 0 없는 `1..1095` 정수. 설치 초기값은 `30`이다. |
| 5 | `Sync` | 아래 durable 동기화 상태와 운영 표시 이력을 보존한다. pair root·HMAC key·nonce·SAS를 저장하지 않는다. |

`Sync` 자식은 아래 순서를 사용한다. 표의 조건에 맞지 않는 누락·추가·중복 요소를 거부한다.

| 순서 | 요소 | cardinality와 조건 |
|---|---|---|
| 1 | `State` | 필수 1개. `Unpaired`, `PairedPendingCommit`, `PairedDisabled`, `Enabled` 중 하나 |
| 2~4 | `PeerEndpoint`, `PeerInstanceId`, `KeyEpoch` | 현재 미배포 v1 구현은 canonical `http://{IP literal}:21000`을 저장한다. 최초 정식 v1 형식은 canonical `https://{IPv4}:21000` endpoint와 peer CA identity만 허용하고 IPv6·DNS host endpoint를 거부한다. 구형 peer state를 migration하지 않고 재페어링한다. ID는 소문자 non-empty `D` GUID, epoch는 `1..UInt64.MaxValue`다. |
| 5~8 | `PairingId`, `CommitExpiresUtc`, `LocalCommitConfirmed`, `RemoteCommitConfirmed` | `PairedPendingCommit`에서만 모두 필수. GUID·7자리 소수 UTC `Z` 시각·소문자 `true`/`false`를 사용하고 다른 상태에서는 모두 없음 |
| 9 | `LastResult` | 필수 1개. 최초값 `NOT_RUN`, 성공은 `OK`, 실패는 내부 API §3의 현재 오류 code 이름 중 하나 |
| 10 | `LastSyncUtc` | `LastResult=NOT_RUN`이면 없음. 그 밖에는 마지막 시도 시각으로 필수 |
| 11 | `ClockSkewSeconds` | `NOT_RUN`이면 없음. 그 밖에는 handshake에서 실제 편차를 관찰한 경우에만 canonical signed decimal로 선택 저장 |
| 12~13 | `LastPeerNotificationOperation`, `LastPeerNotificationResult` | 항상 필수. 최초값은 정확히 `NONE`·`NOT_RUN`; 이후 operation은 `RELEASE`·`REVOKE`, result는 `CONFIRMED`·`UNCONFIRMED`·`NOT_REQUIRED` |
| 14 | `LastPeerNotificationUtc` | `NONE`·`NOT_RUN`일 때 없음. 그 밖에는 마지막 로컬 처리 UTC 시각으로 필수 |

`PairingWindowOpen`, `Negotiating`, `SasPending`, `BothConfirmed`와 SAS·5분 monotonic timeout 상태는 재시작 시 폐기하는 메모리 상태이므로 `config.xml`에 쓰지 않는다. `PairedPendingCommit`의 pair root와 peer binding 원문은 `secrets\peer.dat`의 DPAPI 보호 payload가 소유하며 `config.xml`에는 복구 상태·epoch·비밀이 아닌 표시 정보만 둔다. 세 UTC 요소는 `yyyy-MM-ddTHH:mm:ss.fffffffZ`, GUID는 소문자 `D`, boolean은 소문자, 정수는 앞자리 0과 `+`가 없는 canonical 문자열이어야 한다.

위 형식은 build 14 이하의 미배포 구현 기준선이며 정식 v1 호환 입력이 아니다. 최초 정식 v1은 `pending.xml`을 제거하고 config에 `DirectoryHostName`·`DirectoryIpv4Address`를 추가한다. SiteId·CA role·rotation phase·TrustRevision·slot별 PKI/CRL high-water는 `pki/state.xml`, active issuer의 발급·idempotency·폐기 이력과 issuer CA serial은 `pki/ledger.xml`, standby가 관찰한 dual-pin bundle과 공개 current mapping은 `pki/peer-cache.xml`이 소유한다. CA 인증서에는 endpoint SAN을 두지 않고 Directory leaf에만 두 Directory identity 값을 각각 DNS·IP SAN으로 넣는다. registration mode state와 ProductCode는 config에 저장하지 않는다. 구형 개발 데이터는 명시적 초기화 없이 자동 변환하지 않으며, 정식 v1의 필수 current primary나 PKI state가 누락·손상되면 빈 값으로 자동 생성하지 않고 fail closed한다.

### 5.4 Schema migration과 다중 파일 복구 저널

#### 5.4.1 저장 schema

- 제품이 아직 배포되지 않았으므로 build 12 이하의 단일 `ServerAddress`·`pending.xml` v1은 정식 저장 계약이 아니다. 목표 인증서 기반 형식을 최초 정식 v1로 확정하며 구형 개발 파일을 같은 version의 호환 입력으로 추측해 읽거나 자동 변환하지 않는다.
- 최초 정식 v1 저장 XML 루트 속성은 exact `SchemaVersion="1"`이다. 속성 누락, 빈 값, `0`, 음수, 부호·선행 0이 있는 비정규 정수, 알려진 현재 버전보다 큰 값은 거부하고 snapshot과 listener를 게시하지 않는다. 무버전 파일을 v1로 추정하거나 현재 DTO로 먼저 역직렬화해 의미를 추측하지 않는다.
- 최초 정식 저장 형식은 v1이며 v1 안에서 수행할 migration은 없다. 구형 개발·테스트 설치는 서비스 중지와 명시적 확인 뒤 정확한 데이터 루트를 초기화하고 fresh install한다. 첫 정식 배포 뒤의 형식 변경만 지원 입력 버전을 명시한 `N -> N+1` staging 변환, 전체 입력·결과·교차 파일 불변식 검증과 이 절의 journal commit 순서로 수행한다.
- 여러 버전을 건너뛰는 변환, downgrade와 원본 파일의 in-place 부분 수정은 금지한다. 어느 단계든 실패하면 원본을 보존하고 기동을 fail closed한다.
- 복구 journal의 루트도 exact `SchemaVersion="1"`을 사용한다. 미지원 journal version은 `RecoveryFailed`이며 업그레이드·제거가 active journal을 무시하거나 임의 삭제하지 않는다.
- 현재 build 12 이하 개발 XML의 canonical bytes와 `pending.xml` 구조는 구현 전환 때 제거할 기준선일 뿐 최초 정식 v1 정의가 아니다. 정식 v1은 BOM 없는 strict UTF-8, XML namespace 없음, exact `<?xml version="1.0" encoding="utf-8"?>` 선언과 CRLF·2칸 들여쓰기를 유지하되 `directory.xml`의 DNS+IPv4 service identity와 pending 없는 config·PKI 교차 불변식을 새 serializer·strict parser·canonical round-trip 테스트로 확정한다.
- 현재 저장소 구현은 각 저장 XML과 recovery image를 최대 16MiB로 제한한다. 이는 활성 1,000개·대기 1,000개 정상 규모에서 입력·메모리 상한을 두기 위한 운영 한계이며, 톰스톤 증가로 한계를 넘기기 전에 별도 용량 정책을 확정해야 한다.

#### 5.4.2 journal 위치와 wire 형식

아래 `Pending` target을 포함한 allowlist는 build 12 이하 미배포 구현 기준선이다. 현재 단일 CA 소스는 `Pending`을 제거한 9개 fixed target을 구현했지만 최초 정식 v1 journal은 [rotation 계획 §8.3](./07-ca-key-rotation.md#83-recovery-journal)에 따라 fixed A/B slot·retired archive target과 operation별 집합으로 다시 확장한다. 구형 active journal이 있는 개발 데이터는 자동 폐기하지 않고 데이터 루트 초기화 전 서비스가 중지됐는지 확인한 뒤 개발 환경 전체 초기화 절차로만 제거한다.

- transaction은 데이터 루트와 같은 볼륨의 `%ProgramData%\DEEPAi\ServiceDirectory\journal\{TransactionId}.preparing\`에서 image와 `PREPARED` journal을 먼저 완성하고, 같은 journal root의 `{TransactionId}\`로 원자 rename한 뒤에만 active로 공개한다. mutation gate는 미정리 active transaction을 하나만 허용하며 둘 이상 발견하면 `RecoveryFailed`다. `{TransactionId}`는 lowercase `D` 형식 GUID이고 active `journal.xml`의 `TransactionId`와 정확히 같아야 한다. `.preparing`은 실제 대상을 바꾸기 전의 정리 가능한 잔여물이며 active journal로 해석하지 않는다. 디렉터리와 파일에는 데이터 루트 수준의 제한 ACL을 적용하고 reparse point를 거부한다.
- build 12 이하의 `journal.xml`은 최대 16KiB의 BOM 없는 strict UTF-8이며 DTD·외부 entity와 알 수 없는 요소·속성을 거부한다. 루트는 `SchemaVersion`, `TransactionId`, `Phase`를 가지며 `Phase`는 exact `PREPARED` 또는 `COMMITTED`다. 이 구형 기준선은 1~4개 `Entry`, 현재 단일 CA 구현은 1~9개를 허용한다. 최초 정식 v1의 정확한 상한과 순서는 dual-slot target 확정 때 저장 schema 문서에서 고정한다.
- `Entry`는 `Target`, `BeforeExists`, `AfterExists`와 존재하는 각 image의 64자 lowercase `BeforeSha256`·`AfterSha256`만 기록한다. `Target`은 다음 고정 allowlist와 경로·image 이름으로 매핑하고 절대·상대 임의 경로, `..`, 대체 파일명과 알 수 없는 필드·속성을 허용하지 않는다.

| `Target` | 실제 대상 | before image | after image |
|---|---|---|---|
| `Directory` | `directory.xml` | `directory.before.bin` | `directory.after.bin` |
| `Pending` | `pending.xml` | `pending.before.bin` | `pending.after.bin` |
| `Config` | `config.xml` | `config.before.bin` | `config.after.bin` |
| `PeerSecret` | `secrets\peer.dat` | `peer.before.bin` | `peer.after.bin` |

- image는 실제 대상의 exact raw bytes다. `PeerSecret`에는 DPAPI로 보호된 bytes만 저장하고 복호화한 pair root를 journal에 쓰지 않는다. `Exists=false`인 쪽은 image와 hash가 없어야 하며 복구 시 해당 대상의 부재까지 상태로 복원한다. `PeerSecret`은 폐기·교체된 이전 자격 증명을 `peer.dat.bak`에 남기지 않고 active journal의 보호된 before image만 rollback 근거로 사용한다. `PeerSecret` 삭제가 `COMMITTED`가 되기 전에는 `peer.dat`과 `peer.dat.bak`이 모두 없는지 확인하며, PREPARED rollback 뒤에도 backup 자격 증명을 새로 만들지 않는다.
- PREPARED rollback에서 before가 부재인 target과 그 `.bak`은 `File.Delete`로 바로 지우지 않는다. 각각을 같은 active transaction 디렉터리의 target별 고정 `{target}.primary.discard.bin`·`{target}.backup.discard.bin` 이름으로 write-through 이동해 target 경로의 부재를 먼저 내구화한다. cleanup discard는 해당 PREPARED entry의 before 또는 after가 부재이거나 `COMMITTED` entry의 after가 부재인 경우에만 허용하며 state image나 hash 입력으로 사용하지 않는다. `PeerSecret`은 `.bak` 자체를 금지하고 발견 시 fail closed하며, committed 폐기는 primary만 transaction discard로 이동한다. 재기동은 discard를 state로 다시 적용하지 않고 target 부재를 검증한 뒤 transaction과 함께 `.complete` 정리를 계속한다.
- journal의 SHA-256은 write tearing·손상된 로컬 recovery image를 탐지하기 위한 내부 무결성 값이다. 오프라인 설치 파일의 출처를 인증하지 않으며 §8.4에서 제외한 패치 manifest나 설치 gate로 사용하지 않는다.

#### 5.4.3 commit과 기동 복구

1. mutation gate 안에서 expected revision을 다시 확인하고 전체 after 상태를 직렬화한 뒤 schema, 중복·용량, `LogicalClock`, pending base revision, peer epoch를 포함한 교차 파일 불변식을 검증한다.
2. 고정 target의 before·after image를 `.preparing` 디렉터리에 쓰고 write-through와 `Flush(true)` 의미로 내구화한다. image hash를 다시 읽어 확인하고 `PREPARED` `journal.xml`을 내구화한 뒤, 디렉터리를 canonical active 이름으로 원자 rename한다. 이 전에는 실제 대상을 바꾸지 않는다.
3. 실제 고정 대상을 생성·원자 교체·삭제하고 각 결과를 내구화한다. 하나라도 최종 상태를 증명할 수 없으면 새 snapshot을 게시하지 않고 coordinator를 `RecoveryRequired`로 전환한다.
4. 모든 대상이 after image와 일치하면 `journal.xml`을 `COMMITTED`로 원자 교체하고 내구화한다. 이 시점 뒤에만 commit 성공으로 처리하고 immutable snapshot을 게시한다. journal 정리 전 종료되더라도 다음 기동의 roll-forward가 같은 결과여야 한다.
5. 기동 `Load`는 저장 XML보다 active journal을 먼저 처리한다. `PREPARED`는 모든 before image로 rollback하고 `COMMITTED`는 모든 after image로 roll-forward하며 두 동작은 반복 실행해도 같은 결과여야 한다. 복구와 전체 상태 검증이 끝난 뒤에만 transaction 디렉터리를 정리한다.

준비 중 종료되어 남은 canonical `{TransactionId}.preparing`은 대상이 바뀌기 전의 정리 재개 표식이다. 전체 상태 검증이 끝난 transaction은 같은 journal root 안의 canonical `{TransactionId}.complete` 이름으로 먼저 원자 rename한 뒤 파일을 정리한다. 두 suffix 디렉터리는 적용할 active transaction이 아니며 다음 `Load`가 내용을 state로 재적용하지 않고 안전성 검사를 거쳐 삭제를 계속한다. 이름이 비정규이거나 reparse point·하위 디렉터리를 포함한 표식은 임의 삭제하지 않고 `RecoveryFailed`다.

`PREPARED` transaction의 candidate `LogicalVersion`·`LogicalClock`·`LastPeerKeyEpoch`는 durable 발급으로 간주하지 않는다. `COMMITTED` 상태와 대상 전체가 확인된 값만 발급 완료로 처리하며, 이후 backup·journal 복구에서 그 high-water를 낮추지 않는다. active journal은 개별 `.bak`보다 우선한다. 중복 entry, TransactionId·phase·target 불일치, image 누락·SHA-256 불일치, 미지원 schema, 예상 밖 파일·reparse point와 복구 write 실패는 모두 `RecoveryFailed`다. 이 경우 임의 조합, 부분 snapshot, listener 기동이나 낮은 high-water fallback을 금지한다.

active journal이 없는데 `directory.xml`·`pending.xml`·`config.xml` primary가 누락·손상됐거나 필수 파일 조합이 불완전한 경우에는 유효해 보이는 standalone `.bak`이 있어도 자동 승격·복원하지 않고 fail closed한다. `directory.xml`·`pending.xml`이 함께 누락된 경우도 빈 저장소가 아니라 손상 상태다. 개별 backup만으로는 마지막 발급 `LogicalClock`과 `LastPeerKeyEpoch` high-water가 감소하지 않음을 증명할 수 없기 때문이다. 이 경우 snapshot과 listener를 게시하지 않고 명시적 운영 복구 또는 인증된 피어를 통한 복구를 요구한다. 향후 자동 backup 복구는 high-water 비감소를 내구적으로 증명하는 형식을 먼저 설계·구현·테스트한 경우에만 허용하며, `peer.dat.bak`은 계속 금지한다.

## 6. 등록·재등록·삭제 불변식

외부 요청 처리의 상세 계약은 [외부 API 명세 §9](./04-api-01-external-application.md#9-즉시-등록과-인증서-발급)에 둔다.

### 6.1 등록 모드

- 로컬 운영자가 설정 UI의 등록 서비스 화면에서만 시작·종료한다. 트레이 menu와 installer에는 명령을 두지 않는다.
- ProductCode를 입력·선택하지 않는 전역 창이며 고정 1시간·첫 유효 요청 한 건이다.
- 상태는 `CLOSED`, `OPEN`, `CLAIMED`이고 process memory에만 존재한다. 서비스 재시작 시 항상 `CLOSED`다.
- 이미 `OPEN`일 때 다시 open하면 현재 만료를 반환하고 시간을 연장하지 않는다.
- 일일 키·XML·CSR·SAN·도메인 검증에 실패한 요청은 창을 소비하지 않는다.
- 완전히 검증한 신규 요청만 mutation gate에서 `OPEN -> CLAIMED`를 원자 전이한다.
- `CLAIMED` 뒤 발급·저장 성공, 실패 또는 결과 불확실 여부와 관계없이 창을 닫는다.

### 6.2 등록 결과

| 현재 ProductCode 상태 | 열린 창의 첫 유효 신규 요청 | 결과 |
|---|---|---|
| 미등록 | valid service+CSR | `REGISTERED`, 새 record·leaf·ledger 생성 |
| 톰스톤 | valid service+CSR | `REGISTERED`, 새 logical revision·leaf로 대체 |
| 활성 | 같은 request ID·CSR·payload | 기존 성공의 `REPLAYED`, 창을 요구하거나 소비하지 않음 |
| 활성 | 다른 valid request·CSR | `REREGISTERED`, 이전 active serial 폐기·CRL 갱신 뒤 새 record·leaf로 교체 |

- 성공한 `RegistrationRequestId`, CSR hash와 semantic payload는 idempotency 근거로 내구 저장한다.
- 같은 ID의 다른 CSR·payload는 충돌이다.
- 등록과 재등록은 directory revision, certificate ledger, leaf artifact, 이전 serial 폐기와 CRL을 하나의 journal transaction으로 commit한다.
- private key는 서버 앱에만 있고 Directory에는 CSR과 공개 certificate만 저장한다.
- 등록 record와 leaf SAN은 요청·CSR에서 일치한 `ServiceHostName`·`ServiceIpv4Address`만 사용한다. `DirectoryHostName`·`DirectoryIpv4Address`, TCP source IP와 DNS 역조회 값은 등록값을 생성·보완·교정하는 데 사용하지 않는다.
- 활성 서비스 1,000개에서 새 ProductCode 등록은 창을 claim하기 전에 `LIMIT_EXCEEDED`로 거부하고 창을 유지한다. 기존 ProductCode 재등록과 삭제는 허용한다.

### 6.3 삭제와 갱신

- 삭제는 물리 제거 대신 톰스톤을 만들고 해당 ProductCode의 모든 active serial을 폐기해 CRL을 publish한 뒤 즉시 sync를 예약한다.
- directory 삭제만 성공하고 인증서가 유효하게 남는 부분 성공을 허용하지 않는다.
- 동일 ProductCode 갱신은 현재 유효·unrevoked leaf private key가 ProductCode·새 CSR hash·정규화한 `ServiceHostName`·`ServiceIpv4Address` identity hash를 서명한 proof로 자동 처리한다.
- 서버 앱은 만료 30일 전과 자기 설정에 영속화한 service hostname/FQDN·IPv4 쌍을 명시적으로 변경했을 때 재발급을 요청한다. NIC 자동 열거·TCP source IP·DNS 역조회로 identity를 바꾸지 않는다. ProductCode 변경, private key 분실, 폐기·만료 leaf는 등록 모드를 다시 열어 재등록한다.
- 외부 조회는 톰스톤을 없는 항목으로 취급한다.

## 7. 동기화 불변식

실행 시점:

1. 등록·재등록·삭제 또는 인증서 폐기 직후
2. 즉시 동기화 실패 보정을 위한 10분 주기
3. 서비스 기동 시
4. 운영자 수동 요청

모든 사이클은 피어 인증과 handshake부터 시작한다. 데이터는 ProductCode별로 병합하며 `(LogicalVersion, OriginInstanceId)`를 순서대로 비교한다. 버전이 크면 승리하고, 버전이 같으면 소문자 `D` 형식 GUID의 `Ordinal` 비교에서 큰 `OriginInstanceId`가 승리한다. 두 값이 모두 같은데 정규화된 전체 레코드가 다르면 `REVISION_COLLISION`으로 전체 병합을 중단한다. `LastModifiedUtc`·`DeletedUtc`는 표시·감사용이며 승자 판정에 사용하지 않는다.

다음 조건을 지킨다.

- sync 송수신은 일관된 불변 스냅샷을 사용하고 피어별 활성 session을 하나로 제한한다. 최종 병합·게시만 공통 state mutation gate에서 현재 로컬 snapshot과 수행해 관리 변경을 잃지 않는다.
- 한 sync batch는 최대 1,000개 레코드이며 톰스톤 때문에 초과하면 같은 `SnapshotId`의 여러 batch로 교환한다. 전체 batch 검증·병합이 끝나기 전에는 부분 상태를 게시하지 않는다.
- Sync snapshot은 레코드별 `LogicalVersion`과 snapshot `LogicalClock`을 함께 전송한다. 수신 clock이 모든 수신 record version 이상인지 확인하고, 인증된 전체 batch와 병합 후보가 모두 유효할 때만 mutation gate에서 `max(localClock, remoteClock)`을 병합 결과와 한 번에 영속화한다.
- 인증 실패, 일부 batch, revision collision, 용량 초과 또는 저장 실패에서는 원격 clock을 관찰한 것으로 처리하지 않고 현재 clock과 상태를 모두 유지한다. 성공한 원격 관찰 뒤 다음 로컬 변경은 그때까지 관찰한 최댓값보다 커야 한다.
- 한쪽에만 있는 톰스톤도 전파한다.
- 원격 스냅샷을 병합한 후보의 활성 서비스가 1,000개를 넘으면 전체 exchange를 `DIRECTORY_CAPACITY`로 거부하고 현재 상태를 게시·저장하지 않는다. 운영자가 한쪽에서 충분한 활성 서비스를 삭제한 뒤 다시 sync한다.
- 해제 통지 실패와 로컬 해제 성공을 구분해 기록한다.
- Peer 요청 timestamp가 60초 freshness 범위를 벗어나면 인증 단계에서 요청을 거부하고 상태 모니터에 오류를 표시한다. 이 검사는 HMAC replay 방지 조건이며 논리 버전 병합 순서와 무관하다.

최초 페어링은 양쪽 로컬 운영자가 5분 페어링 창을 열고 ECDH P-256 공개키를 교환한 뒤, 각 트레이에 독립적으로 계산된 8자리 SAS가 같은지 양쪽에서 확인하는 상태 머신을 사용한다. 양쪽 확인과 MAC된 commit이 끝나면 `PairedDisabled`가 되고, 운영자가 sync를 활성화해야 `Enabled`가 된다. 상세 계약은 [내부 API 명세](./04-api-02-internal.md#53-최초-페어링과-key-epoch)를 따른다.

## 8. 프로젝트 하드닝 적용

[Directory Service 사용 애플리케이션 하드닝 가이드](./01-hardening.md)는 공통 보안 기준 위에 적용하는 Directory 구조 제품 전용 추가 기준이다. 저장소 공통 보안 기본 지침은 루트 `AGENTS.md` §8을 따른다.

사내 `Directory서비스_애플리케이션_하드닝_가이드` Revision 2(2026-07-19)는 Directory 구조 전용 추가 기준이다. 아래 `Directory §` 표기는 그 문서를 가리킨다.

### 8.1 적용 판정

`적용`은 준수 완료가 아니라 설계·구현·릴리스 검증 대상이라는 뜻이다.

| 하드닝 가이드 항목 | 판정 | 서비스 디렉토리 적용과 근거 |
|---|---|---|
| §1 기본 원칙 | 적용 | 최소 권한, 안전한 실패, 신뢰 경계 검증과 심층 방어를 전 구성요소에 적용 |
| §2.1·§3.4 시크릿 관리 | 적용 | active issuer의 CA private key PKCS#8과 Peer pair root를 각각 DPAPI `LocalMachine`으로 보호하고 서비스 SID exact ACL로 관리한다. standby는 CA key primary를 두지 않는다. CA backup은 운영자 암호 기반 authenticated encryption을 사용한다. External 일일 키에는 저장 secret이 없음 |
| §2.2 제품 내 비밀번호 로그인·MFA | 해당 없음 | 제품 자체 계정·비밀번호 로그인 기능이 없음. 추가되면 재평가 |
| §2.2·§2.3 External·Health 요청 검증 | 예외 승인 | 일일 API 키를 유지하고 등록 모드와 결합해 발급 admission에 사용. strong identity가 아닌 위험과 보완 통제는 §8.3에 기록 |
| §2.2·§2.3 Admin 인증·인가 | 적용 | loopback `HttpListener` Negotiate로 Windows identity를 확인하고 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹으로 인가. AD·Workgroup 모두 지원 |
| §2.2·§2.3 Peer 상호 인증·인가 | 적용 | ECDH P-256+양쪽 SAS 페어링, DPAPI pair root, HMAC-SHA256 요청·응답 서명, timestamp·nonce·session replay 차단 적용 |
| §2.4 전송 구간 암호화 | 적용 | remote External·Peer에 OS 보안 기본값의 TLS 1.2 이상 HTTPS, site CA, SAN·pin·CRL 검증 적용. loopback은 별도 로컬 IPC 경계 |
| Directory §3.1 사이트 CA | 적용 | 최초 설치 자동 CA·Directory leaf, 최대 20년, service SID ACL, 승인된 암호화 backup·restore와 동일 CA 이중화 적용 |
| Directory §3.2 등록 발급 | 예외 승인 | 앱 로컬 key·CSR과 1년 leaf·serial ledger는 적용한다. 전체 NIC·hostname SAN 자동 수집 대신 앱 사용자가 고른 `ServiceHostName`·`ServiceIpv4Address` 한 쌍만 필수 SAN으로 발급한다. 사유·위험·보완 통제는 §8.2.1에 기록 |
| Directory §4.1·§4.4 검증·CRL | 적용 | 저장 CA+SPKI pin, 8개 certificate 검증 조건, 표준 X.509 CRL과 CRL 불가 시 기존 exact leaf만 허용 적용 |
| Directory §4.2 접속정보 파일 우선 | 적용 | 이 제품은 접속정보 파일을 생성·배포할 수 없어 가이드가 허용한 TOFU fallback만 사용. 성공한 Milestone session endpoint로 위치를 정하고 잔여 위험·신뢰 초기화는 §8.2에 기록 |
| Directory §4.5·§5 CA 변경 | 적용 | same-key 갱신, dual-pin 계획 교체, pin 불일치 차단, 관리자 신뢰 초기화와 외부 앱 제거 시 local trust 완전 삭제 적용 |
| Directory §6 고객 CA | 해당 없음(제품 비지원) | 고객 하위 CA·고객 발급 서버 인증서·PFX·Windows system trust 대체 mode를 구현하지 않고 자사 Directory site CA만 지원 |
| §2.5 저장 데이터 보호 | 적용 | ProgramData·백업·임시 파일·로그 ACL과 민감정보 배제에 적용 |
| §2.6 입력 검증·인젝션 | 적용 | XML, 쿼리, 경로, 주소, 포트, GUID, UTC, 항목 수를 수신 측 허용 목록으로 검증 |
| §2.6 SQL·DB | 해당 없음 | DB와 SQL 기능이 없음. 도입 시 파라미터 바인딩을 포함해 재평가 |
| §2.7 시스템 운영 로그 | 적용 | §9의 9개 서비스·등록 서비스·동기화 이벤트와 보존·ACL 정책 적용 |
| §2.7 보안 감사 로그 | 적용 | §9.5의 Windows `Application` Event Log에 인증·인가와 endpoint 경계 거부를 기록하고 9개 시스템 파일 이벤트와 분리 |
| §2.8 오류 처리 | 적용 | 응답과 사용자 UI에 스택, 내부 경로, 시크릿, 상세 빌드 정보를 노출하지 않음 |
| §2.9 의존성 관리 | 적용 | 공식 저장소, 버전 고정, 라이선스·CVE 점검과 EOL 확인 적용 |
| §2.10 패치 배포 | 예외 승인 | 코드 서명·설치용 체크섬·manifest 없이 수동 오프라인 설치 EXE를 직접 실행. 사유와 잔여 위험은 §8.4에 기록 |
| §3.1 실행 권한 | 적용 | 트레이 `asInvoker`, 메인·와치독별 Windows 가상 서비스 계정과 서비스 SID ACL 적용 |
| §3.2 설치·파일 권한 | 적용 | Program Files·ProgramData 분리와 용도별 제한 ACL 적용 |
| §3.3 DLL 하이재킹 방지 | 적용 | DLL 검색 경로 제한과 설치 프로그램 실행 위치 점검 |
| §3.5 로컬 IPC | 적용 | Admin은 loopback+인증, Named Pipe는 호출자 ACL+명령 허용 목록 적용 |
| §3.6 빌드 보호 | 적용 | 운영 빌드의 OS 보호 기능 확인, 우회·테스트 코드 배제, 설치 판단과 분리된 내부 추적용 산출물 해시 기록 |
| §3.7 자동 업데이트 | 해당 없음 | 자동 업데이트 기능은 제품 범위에 없음. 추가 시 전송·무결성 모델을 재평가 |
| §4.1 HTTPS·HSTS | 적용 | remote API는 HTTPS only이며 non-browser API라 HSTS redirect 대신 평문 listener 자체를 열지 않음 |
| §4.1 브라우저 보안 헤더 | 해당 없음 | HTML을 제공하는 브라우저 UI가 없음. 서버·프레임워크 정보 노출 억제는 §2.8로 적용 |
| §4.2·§4.3 세션·쿠키·XSS·CSRF | 해당 없음 | 브라우저·쿠키·HTML 기반 UI가 없음. 아키텍처 변경 시 재평가 |
| §4.4 API 보안 | 적용 | External은 HTTPS·TOFU/pin·일일 키·등록 모드·CSR, Admin은 Windows identity, Peer는 TLS와 HMAC을 적용하고 공통 제한·최소 DTO 사용 |
| §4.5 파일 업로드 | 해당 없음 | 파일 업로드 기능이 없음. 추가 시 재평가 |
| §4.6 서버·인프라 | 적용 | 관리 경계, 폐쇄망 인터페이스 분리, Domain·Private 방화벽 프로필, Public 차단과 불필요한 노출 제거에 적용 |
| §5 개발·배포 프로세스 | 적용 | 보안 변경 리뷰, SAST·시크릿·의존성 스캔, 재현 빌드와 내부 추적용 산출물 해시·대응 절차 적용. 오프라인 패치 검증은 §8.4 예외 |

### 8.2 TLS·사이트 CA 적용

기존 TLS 미사용 예외는 사내 Directory 전용 하드닝 가이드 개정과 사용자의 인증서 전환 결정(2026-07-19)으로 폐기한다.

- remote External·PKI·Peer API는 TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS only다. protocol·cipher suite를 앱 코드에 고정하지 않고 지원 OS 정책에서 SSLv3·TLS 1.0·1.1 비활성화를 통합 검증한다. 평문 remote listener, redirect와 certificate validation fallback을 두지 않는다.
- 기본 trust anchor는 최초 설치에서 생성한 Directory site CA다. Directory leaf와 등록 서버 leaf는 같은 CA가 발급한다.
- 기본 CA·Directory leaf key는 RSA 3072이고 CA·leaf·CRL은 SHA-256 with RSA PKCS#1 v1.5로 서명한다. 등록 CSR은 RSA 2048 이상(exponent exact 65537) 또는 named P-256만 허용하고 CSR signature와 SAN extension의 상세 허용 목록은 외부 API §9.1을 따른다.
- 외부 앱은 성공한 Milestone Management Server session에서 Directory가 함께 설치된 서버의 DNS hostname/FQDN과 실제 remote IPv4를 `DirectoryHostName`·`DirectoryIpv4Address` 한 쌍으로 얻고, 둘 중 선택한 주소로 접속하되 Directory leaf에 두 SAN이 모두 있는지 확인한다. 이 제품은 접속정보 파일을 제공할 수 없으므로 가이드의 TOFU fallback만 사용하며 수동 pin 입력도 요구하지 않는다.
- Directory CA 인증서 자체에는 endpoint SAN을 두지 않는다. Directory leaf는 Directory identity 쌍만, 등록 서비스 leaf는 해당 앱이 제출한 `ServiceHostName`·`ServiceIpv4Address` 쌍만 SAN으로 가지며 서로 복사하지 않는다.
- 이후 chain, CA pin, SAN, validity, EKU·Key Usage, CA constraints, algorithm strength와 CRL을 모두 검증한다.
- 같은 주소의 예고 없는 CA pin 변경은 fail closed한다. same-key CA certificate 갱신과 계획된 dual-pin key rotation만 허용한다.
- 외부 앱의 `Directory 신뢰 초기화`에는 로컬 관리자 권한과 명시적 확인을 요구한다. 외부 앱 제거는 CA·pin·SiteId·CRL cache를 완전히 삭제하고 repair·upgrade는 정상 trust를 보존한다.
- Admin·와치독 `127.0.0.1`은 로컬 IPC 경계로 남기되 exact local/remote endpoint, Negotiate·운영자 인가 또는 health 검증을 유지한다.
- 방화벽 Domain·Private 제한, Public 차단과 request `LocalEndPoint` 검증은 TLS 뒤에도 심층 방어로 유지한다.
- CA private key, encrypted backup, certificate ledger와 CRL은 제한 ACL·복구 journal·감사 정책을 적용한다.

#### 8.2.1 등록 서비스 SAN 범위 예외 기록

| 항목 | 결정 |
|---|---|
| 판정 | `예외 승인` |
| 승인 근거 | 장기간에 걸쳐 서로 다른 네트워크 환경에 설치되는 서비스가 실제 사용할 hostname/FQDN과 IPv4를 명시적으로 선택해야 한다는 사용자 결정(2026-07-19) |
| 승인자 기록 | 릴리스 전 프로젝트 예외 승인 기록에 승인자의 성명·역할을 기입해야 함 |
| 범위 | 외부 등록 서비스의 directory record, CSR requested SAN, 발급 leaf SAN과 자동 갱신 identity. Directory 자체 leaf와 Peer identity에는 적용하지 않음 |
| 방식 | 등록 앱 사용자가 서비스용 `ServiceHostName` 한 개와 `ServiceIpv4Address` 한 개를 선택하고 앱이 제한 ACL 설정에 한 쌍으로 영속화한다. CSR과 요청에 정확히 같은 두 값을 넣고 Directory가 DNS SAN 한 개·IP SAN 한 개로 발급한다. |
| 사유 | 전체 NIC·hostname 자동 수집은 비서비스용·일시적·가상 인터페이스를 인증서에 포함하고 환경 변경 때 identity를 예측 없이 바꿀 수 있다. 서비스가 실제 게시할 두 endpoint identity를 설치 시점의 앱 설정으로 고정한다. |
| 수용 위험 | 선택하지 않은 다른 NIC·alias로는 인증서 검증 연결을 할 수 없고, 잘못 선택하거나 이후 DNS·주소가 바뀌면 갱신 전까지 연결이 실패한다. Directory는 원격 주소 소유권을 독립적으로 증명하지 못한다. |
| 보완 통제 | 앱이 선택 시 IPv4 local assignment·listener 사용 가능성과 hostname 정방향 DNS가 그 IPv4를 포함하는지 확인하고 두 값을 원자 저장한다. Directory는 canonical 문법·CSR 일치·등록 모드·일일 키를 검증하며 TCP source IP·DNS 역조회·Directory 주소로 보완하지 않는다. 어느 한 값이 달라져도 현재 leaf proof와 변경 뒤의 완전한 두 필드를 제출하는 갱신으로 처리한다. |
| 적용 기간 | 선택형 단일 서비스 hostname/FQDN·IPv4 모델이 유지되는 동안. 다중 listener·다중 주소 지원 요구가 생기면 API·인증서·갱신 계약을 재검토 |

IPv6는 프로젝트 전체에서 지원하지 않는다. 외부 앱은 선택한 두 값을 자기 설정에 지속적으로 보존하고, Directory 조회 소비자는 반환된 DNS 또는 IPv4 중 현장 설정에 맞는 target을 쓰되 인증서 오류 뒤 다른 target으로 fallback하지 않는다.

### 8.3 External 일일 API 키 발급 예외 기록

| 항목 | 결정 |
|---|---|
| 판정 | `예외 승인` |
| 승인 근거 | 사용자의 일일 API 키 발급 권한 유지와 등록 모드 보완 통제 결정(2026-07-19) |
| 승인자 기록 | 릴리스 전 프로젝트 예외 승인 기록에 승인자의 성명·역할을 기입해야 함 |
| 범위 | External health·조회·등록·갱신 admission과 와치독 health. Admin·Peer 인증에는 적용하지 않음 |
| 방식 | `[A-Z0-9]{4}` ProductCode와 시스템 로컬 `yyyyMMdd`를 결합하고 날짜의 SHA-256을 AES-256 key로 사용하는 CBC/PKCS#7 암호문. 무작위 IV와 결합해 44자 Base64 헤더로 전송 |
| 사유 | 설치하는 사람에게 ProductCode·등록 token·CA 파일·pin을 입력시키지 않고 앱 자체 ProductCode와 관리자 등록 모드를 사용 |
| 수용 위험 | 알고리즘과 ProductCode를 아는 주체의 자체 생성, 동일 ProductCode caller 구분 불가, 당일 replay, method·path·CSR·body 미결합, 열린 전역 창의 first-wins 경쟁 |
| 적용 기간 | 폐쇄망, HTTPS, 1시간·1건 등록 모드와 현재 운영 결정이 유지되는 동안. 각 릴리스와 신뢰 경계 변경 시 재검토 |

상세 wire contract와 테스트 벡터의 단일 원본은 [외부 애플리케이션 API §3](./04-api-01-external-application.md#3-external-일일-api-키)다.

필수 보완 통제:

- 서비스 조회와 등록 요청은 키에서 복원한 ProductCode가 쿼리·XML의 정규화된 ProductCode와 일치해야 한다.
- 서버의 현재 시스템 로컬 날짜만 허용하고 이전·다음 날짜 유예를 두지 않는다. 자정 실패 시 호출자가 새 날짜 키로 한 번 재시도한다.
- remote HTTPS, 저장 site CA pin, Domain·Private 방화벽, exact local endpoint, endpoint별 rate·concurrency 제한을 적용한다.
- 등록은 로컬 운영자가 설정 UI에서 연 ProductCode 입력 없는 전역 창에서만 허용한다. 창은 1시간과 첫 유효 요청 한 건으로 제한하고 성공·장애·재시작에서 닫는다.
- CSR 자체 서명·key strength, 유일한 CSR attribute인 단일-value `extensionRequest`, 유일한 requested SAN과 등록 요청의 canonical `ServiceHostName` DNS 한 개·`ServiceIpv4Address` IPv4 한 개가 정확히 같은지 검증한다. challengePassword·다른 attribute·다른 requested extension은 critical 여부와 무관하게 거부한다. Directory/Milestone 주소, TCP source IP와 DNS 역조회로 두 값을 만들거나 교정하지 않고 등록·certificate ledger·CRL을 원자 commit한다.
- 잘못 등록된 service 삭제는 active serial 폐기와 CRL publish까지 성공해야 완료다.
- 누락·복호화·padding·날짜·ProductCode 실패는 모두 같은 `401 INVALID_API_KEY`로 응답하고 키 원문과 상세 실패 단계를 로그에 남기지 않는다.
- 알고리즘 문서는 사내 승인 범위에서 배포하되 비공개 자체를 암호학적 secret이나 유일한 통제로 주장하지 않는다.
- 고정 AES secret, 난독화된 공용 secret 또는 별도 master key를 코드·설정에 추가하지 않는다.
- 이 방식은 strong caller identity, CSR/body signature 또는 replay 방지로 표현하지 않는다. 실제 발급 보완 통제는 인증된 관리자의 registration window open 행위다.

### 8.4 코드 서명·체크섬 없는 오프라인 패치 예외 기록

| 항목 | 결정 |
|---|---|
| 판정 | `예외 승인` |
| 승인 근거 | 사용자의 명시적 무검증 오프라인 설치 결정(2026-07-18) |
| 승인자 기록 | 릴리스 전 프로젝트 예외 승인 기록에 승인자의 성명·역할을 기입해야 함 |
| 범위 | 서비스 디렉토리의 수동 오프라인 설치·업그레이드·롤백용 Inno Setup EXE. 자동 업데이트에는 적용하지 않음 |
| 방식 | 실행 파일·라이브러리·설치 EXE에 코드 서명을 하지 않고, 설치용 체크섬·hash·manifest를 생성·전달·비교하지 않은 채 제공된 EXE를 직접 실행 |
| 사유 | 폐쇄망의 수동 설치 환경에서 별도 서명·manifest 배포와 확인 절차 없이 설치하도록 한 제품 운영 결정 |
| 수용 위험 | 설치 파일의 배포자 출처, 반입 중 변조와 손상을 기술적으로 확인할 수 없음. 교체된 EXE가 설치 권한으로 실행되면 임의 코드 실행과 시스템 변경 가능 |
| 운영 전제 | 폐쇄망 내 수동 관리자 설치와 자동 업데이트 부재. 이 조건들은 출처·무결성 검증을 제공하는 보완 통제가 아님 |
| 적용 기간 | 폐쇄망 수동 설치와 현재 승인 결정이 유지되는 동안. 배포 경로·신뢰 경계 변경 시 재검토 |

적용 경계:

- package 산출물은 `installer\DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe` 하나이며 `.sha256` 또는 다른 설치 검증 manifest를 만들지 않는다. 설치 프로그램과 운영 절차도 hash·서명 확인이나 patch별 manifest 승인·폐기를 성공 조건으로 요구하지 않는다.
- 이 예외를 “검증된 설치”, “변조 방지” 또는 “배포자 인증”으로 표현하지 않는다. 설치 오류와 제품 동작 검증은 수행하되 파일 출처·무결성을 확인한 것으로 확대 해석하지 않는다.
- 복구 journal image의 SHA-256과 접근 통제된 내부 심볼 세트의 재현 빌드 hash는 각각 로컬 crash recovery와 장애 분석·빌드 추적 전용이다. 설치 EXE에 포함하거나 패치 전달·승인·실행 gate로 사용하지 않는다.
- 자동 업데이트 기능을 추가하거나 폐쇄망 밖·비신뢰 매체·제3자 배포 경로를 사용하려면 이 예외를 그대로 확장하지 않고 패치 신뢰 모델을 다시 결정한다.

### 8.5 적용 원칙

- `해당 없음`은 보안 기준을 충족했다는 뜻이 아니라 현재 기능이나 공격 표면이 없다는 뜻이다.
- 구현되지 않은 항목은 임시 무인증, 공용 시크릿 또는 넓은 권한으로 대체해 완료 처리하지 않는다.
- TLS는 §8.2에 따라 적용한다. External 일일 API 키 발급 예외와 무검증 오프라인 패치 예외는 각각 §8.3·§8.4 범위에만 적용하고 Admin·Peer·CA private key 보호를 완화하지 않는다.
- 기능, 배포망 또는 신뢰 경계가 바뀌면 이 표와 예외 기록을 같은 변경에서 재평가한다.

## 9. 시스템 로그 정책

### 9.1 경로와 일 단위 파일

- 로그 루트: `%ProgramData%\DEEPAi\ServiceDirectory\logs\system\`
- 데이터 루트 기준 상대 경로: `logs/system/dpai-sd_yyyy-MM-dd.log`
- 실제 예: `%ProgramData%\DEEPAi\ServiceDirectory\logs\system\dpai-sd_2026-07-17.log`
- 파일명의 날짜는 UTC가 아니라 로그를 쓰는 시점의 **현재 시스템 로컬 날짜**다.
- 로컬 날짜가 바뀌면 새 날짜 파일로 전환한다.
- 각 이벤트는 UTF-8 텍스트 한 줄로 append한다.

### 9.2 시각 규칙

- 로그 레코드 시각은 `DateTimeOffset.Now` 의미의 현재 시스템 로컬 시각과 UTC offset을 사용한다.
- 형식: `yyyy-MM-ddTHH:mm:ss.fffzzz`
- 예: `2026-07-17T18:42:31.123+09:00`
- `Z` 또는 UTC로 변환한 시각을 로그에 쓰지 않는다.
- 실행 중 시스템 timezone 또는 DST offset이 바뀌면 다음 레코드부터 변경된 로컬 시각과 offset을 사용한다. 기존 파일은 이름을 바꾸지 않는다.
- 로컬 날짜 규칙은 시스템 로그와 External 일일 API 키에만 적용한다. API payload의 표시·감사 시각은 UTC를 사용하고 동기화 승자 판정은 시각이 아니라 논리 버전을 사용한다.

기본 한 줄 형식:

```text
{LocalTimestampWithOffset} [{EventCode}] {Details}
```

### 9.3 파일에 기록할 이벤트

| EventCode | 기록 시점 | 최소 Details |
|---|---|---|
| `SERVICE_STARTED` | 메인 서비스 초기화와 API listener 준비가 성공한 직후 | 서비스 인스턴스 식별자 |
| `SERVICE_STOPPED` | 정상 종료 절차가 완료되어 프로세스를 끝내기 직전 | 종료 사유 |
| `REGISTERED_SERVICE_CREATED` | 신규 등록·인증서 발급 commit이 영속화된 직후 | `ProductCode` |
| `REGISTERED_SERVICE_UPDATED` | 재등록 commit이 영속화된 직후 | `ProductCode` |
| `REGISTERED_SERVICE_DELETED` | 등록 서비스 톰스톤이 영속화된 직후 | `ProductCode` |
| `SYNC_INITIAL_STARTED` | 페어링·동기화 활성화 후 최초 전체 동기화가 시작될 때 | 피어, 실행 원인 |
| `SYNC_STARTED` | 최초 동기화가 아닌 동기화 사이클이 시작될 때 | 피어, 실행 원인 |
| `SYNC_STOPPED` | 동기화 설정이 로컬에서 비활성화될 때 | 피어, 중지 원인 |
| `SYNC_SUCCEEDED` | 최초 또는 일반 동기화 사이클이 성공하고 결과가 영속화된 뒤 | 피어, 실행 원인 |

추가 규칙:

- `SYNC_INITIAL_STARTED`와 `SYNC_STARTED`는 같은 사이클에 중복 기록하지 않는다.
- 최초 사이클도 성공하면 `SYNC_SUCCEEDED`를 기록한다.
- 등록 모드 열기·닫기, invalid CSR, 인증서 발급·폐기와 CA 수명주기는 위 9개 시스템 파일 event가 아니라 §9.5 보안 감사 대상이다. 성공 등록·재등록은 commit 뒤 기존 `REGISTERED_SERVICE_CREATED`·`UPDATED`를 기록한다.
- 이벤트는 해당 상태 변경이 성공적으로 영속화된 뒤 기록한다. `SERVICE_STOPPED`만 프로세스 종료 전 기록한다.
- 비밀번호, 토큰, 인증서, 전체 XML, 서버 주소와 스택 트레이스를 Details에 기록하지 않는다.
- 위 목록 밖의 보안 감사·진단 이벤트를 같은 파일에 추가하지 않는다. 인증·인가와 endpoint 경계 거부는 §9.5의 별도 sink에 기록한다.

### 9.4 보존기간 설정

- `config.xml`의 `LogRetentionDays`에 보존할 로컬 달력 일수를 정수로 저장한다.
- 설치 기본값은 `30`일이고 허용 범위는 `1..1095`일이다. `1095`일을 최대 3년으로 정의하며 범위 밖 값, 정수가 아닌 값과 overflow는 거부한다.
- 트레이 설정 화면은 현재 값을 조회하고 일 단위 정수로 변경할 수 있어야 한다.
- 설정 변경은 Admin API를 통해 메인 서비스가 검증·영속화한다. 트레이가 `config.xml`을 직접 수정하지 않는다.
- 보존 정리는 서비스 시작, 로컬 날짜 전환 후 첫 로그 기록, 설정값 변경 직후 수행한다.
- 오늘을 포함해 최근 `LogRetentionDays`개의 로컬 날짜 파일을 보존한다.
- 정리 대상은 정확한 로그 디렉터리 안에서 `dpai-sd_yyyy-MM-dd.log` 형식과 일치하는 파일로 제한한다. 다른 파일이나 하위 경로는 삭제하지 않는다.

### 9.5 보안 진단 Event Log

- 별도 sink는 Windows `Application` Event Log이며 source는 `DEEPAi.ServiceDirectory.Security`다. Windows `Security` 채널과 §9.1의 시스템 파일을 사용하지 않는다.
- source는 권한 상승된 설치·repair가 서비스 시작 전에 `HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\DEEPAi.ServiceDirectory.Security`에 등록한다. 서비스는 이 exact key를 read-only로 확인하고 source 자동 생성 API를 사용하지 않으며, 등록을 확인할 수 없으면 listener를 열지 않고 기동을 실패시킨다. 기록은 source를 생성하지 않는 native Event Log API를 사용한다.
- 기존 `4101 EXTERNAL_API_KEY_REJECTED`, `4102 ADMIN_AUTHENTICATION_REJECTED`, `4103 ADMIN_AUTHORIZATION_REJECTED`, `4104 PEER_AUTHENTICATION_REJECTED`, `4105 PIPE_AUTHORIZATION_REJECTED`, `4106 NETWORK_BOUNDARY_REJECTED`와 새 `4107 TLS_TRUST_REJECTED`, `4108 CERTIFICATE_REQUEST_REJECTED`를 `FailureAudit`, category `0`으로 기록한다.
- `4201 REGISTRATION_MODE_CHANGED`, `4202 CERTIFICATE_ISSUED`, `4203 CERTIFICATE_REVOKED`, `4204 CA_LIFECYCLE_CHANGED`는 성공·운영 감사로 기록한다. 등록 모드 actor SID, 상태·원인, 인증서 operation과 serial을 식별 가능한 최소값으로 남기되 private key·CSR 원문·API key·CA backup 암호를 남기지 않는다.
- External 키의 누락·형식·복호화·padding·날짜·ProductCode 결합 실패는 상세 단계를 구분하지 않고 `INVALID_API_KEY` 하나로 기록한다. Peer는 HMAC·identity·epoch·freshness·nonce replay·session binding 실패까지만 인증 실패로 분류한다. 인증 뒤 XML·도메인 오류와 잘못된 Pipe 명령은 입력 오류이므로 이 sink 대상이 아니다.
- 메시지는 고정 enum 기반의 `Schema`, `Event`, `Boundary`, `Operation`, `Reason`, `Outcome`, `ServiceInstanceId`, 서버 생성 `RequestId`, 허용된 경우의 `ActorSid`, canonical `RemoteAddress`, 인증서 운영 이벤트의 `CertificateSerialNumber`, `SuppressedCount`만 담는다. API key·hash·AES/HMAC·signature·nonce·session·SAS·pair root·ProductCode·계정 이름·원문 URL/query/XML/body/header·예외·stack·내부 경로는 기록하지 않는다.
- 메시지는 한 줄 ASCII 2KiB 이하로 제한하고 NUL·CR·LF와 Event Log 삽입문자로 해석될 수 있는 `%` 뒤 숫자를 금지한다.
- 실제 요청 거부는 제한하지 않고 Event Log 쓰기만 `(EventId, Boundary, RemoteAddress)`별 처음 5건 뒤 분당 1건, 전체 분당 60건으로 제한한다. 최대 2,048개 key를 LRU로 유지하고 10분 미사용 key를 정리한다. `SuppressedCount`는 같은 key가 추적되는 동안 생략한 수를 그 key의 다음 허용 이벤트에 포함하며 LRU에서 제거된 key는 새 상태로 시작한다. 경과시간은 시스템 시각이 아닌 monotonic clock을 사용한다.
- 런타임 기록 실패 시 인증 거부는 유지하고 시스템 파일로 우회하지 않는다. 보안 진단 불능 상태를 degraded로 표시하고 listener를 닫은 뒤 제어된 종료를 수행한다.
- `LogRetentionDays`는 §9.1 시스템 파일에만 적용한다. 제품은 공유 Windows `Application` 로그의 보존·용량 정책을 변경하지 않는다.
- HTTP.sys가 request context 생성 전에 차단한 Negotiate 실패와 OS Pipe ACL이 연결 전에 차단한 요청은 애플리케이션이 관찰할 수 없다. 완전한 수집이 필요한 배포는 Windows 감사정책·HTTP.sys 진단을 별도 운영 범위로 구성한다.

## 10. 설치 계획

- 실행 파일 코드 서명 인증서는 없으므로 바이너리·설치 파일 코드 서명은 현재 범위에서 제외한다. 이는 runtime Directory site CA·HTTPS/server certificate와 별개이며 site CA private key를 코드 서명에 재사용하지 않는다.
- 최종 Inno Setup 설치 EXE만 저장소 루트 `installer\` 바로 아래에 출력한다. 파일명은 루트 `VERSION`에서 읽은 `DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe`이며 제품·build 값을 설치 스크립트에 복사하거나 하드코딩하지 않는다. `.sha256` 또는 다른 설치 검증 manifest를 생성하지 않고 생성 EXE는 Git에 커밋하지 않는다. 상세 계약은 [installer 산출물 계약](../../installer/README.md)을 따른다.
- 트레이와 사용자가 직접 실행하는 일반 실행 파일은 `asInvoker`로 실행한다. 권한 상승은 설치 프로그램의 실제 설치 작업에만 제한한다.
- 설치 루트 `%ProgramFiles%\DEEPAi\ServiceDirectory\`와 데이터 루트 `%ProgramData%\DEEPAi\ServiceDirectory\`를 생성한다. 일반 사용자는 실행 파일을 교체할 수 없어야 하며 로그를 포함한 운영 데이터의 수정·삭제 권한은 역할별 최소 범위로 제한한다.
- 설치·데이터 경로는 AGENTS.md §6의 DEEPAi 표준 경로 규칙을 따른다. 설치 루트는 per-machine·관리자 권한 설치이며 런타임에 쓰지 않고, 머신 공용 설정·데이터·로그는 데이터 루트에 둔다. 이 제품은 사용자별 설정·데이터를 두지 않으므로 `%AppData%`·`%LocalAppData%` 경로는 사용하지 않는다. 언인스톨의 기본 데이터 보존과 삭제 확인 절차도 같은 장과 일치한다.
- **AGENTS.md §6 Users Modify 예외**: 데이터 루트에는 표준 규칙의 Users 그룹 수정(Modify) 권한을 부여하지 않고 이 절의 제한 ACL을 유지한다. 데이터 루트는 CA private key·peer 자격증명·ledger·CRL·backup을 담고 파일 기록 주체가 두 서비스(가상 서비스 계정)뿐이며, 일반 사용자 UI(트레이)는 파일을 직접 수정하지 않고 로컬 Admin API로 서비스에 요청하고, 운영자 접근은 `DEEPAi-ServiceDirectory-Operators` 그룹으로 부여한다. Users Modify를 부여하면 비인가 사용자의 CA·자격증명 변조가 가능해져 하드닝 기준 대응(§2.5 저장 데이터 보호, §3.2 설치·파일 권한)과 충돌하므로, 해당 표준 규칙의 취지(일반 사용자로 실행되는 앱의 데이터 수정)가 성립하지 않는 이 제품에는 적용하지 않는다.
- `config.xml`에는 External 일일 API 키·ProductCode·등록 모드 state·CA private key를 저장하지 않는다. Peer pair root, CA key, encrypted CA backup은 서로 다른 목적·ACL·수명주기로 보호한다.
- 메인·와치독은 각각의 Windows 가상 서비스 계정으로 구성하고, 메인 서비스 SID에는 데이터·로그와 DPAPI blob 접근만, 와치독 서비스 SID에는 필요한 메인 서비스 제어 권한만 부여한다.
- installer는 site CA와 Directory HTTPS leaf를 만들거나 보존 CA에서 leaf를 재발급하고 exact HTTP.sys HTTPS certificate binding을 구성한다. remote External·Peer에는 평문 HTTP binding을 만들지 않는다.
- 방화벽은 Domain·Private 프로필의 TCP `21000`만 허용하고 Public 프로필에서는 차단한다. 고정 원격 IP·CIDR allowlist는 만들지 않는다.
- 서비스와 설치 프로그램은 같은 canonical `ListenAddress` formatter를 사용한다. 서비스는 prefix 등록과 별도로 모든 요청의 실제 local endpoint를 검증하며 loopback 경계에서는 remote endpoint도 loopback인지 확인한다.
- 설치·repair는 Directory가 함께 설치된 Management Server의 `DirectoryHostName` 한 개와 선택한 IPv4 `ListenAddress`를 `DirectoryIpv4Address`로 확정·검증하고 Directory leaf에 정확히 그 DNS·IP SAN을 모두 넣는다. 다른 NIC·alias를 자동 추가하지 않는다. 메인 서비스도 기동 때 두 config 값, IPv4 주소 할당·network profile·leaf private key 접근·두 SAN·validity와 binding을 다시 검증하며 실패하면 remote listener를 열지 않는다.
- installer UI와 unattended 인수는 ProductCode, Directory 주소, CA pin, 외부 앱 인증서/PFX를 요구하지 않는다.
- 최초 설치 완료 전 운영자가 암호화 CA backup을 생성·확인하게 하고 평문 export를 금지한다. repair·재설치는 보존 CA를 우선 복원하며 backup이 있는데 새 CA를 조용히 생성하지 않는다.
- 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹과 Admin·Named Pipe ACL을 만들고 AD 사용자·그룹 또는 Workgroup 로컬 사용자를 구성할 수 있게 한다.
- 트레이 자동 시작은 사용자 로그인 단위로 등록한다.
- DLL 검색 경로를 제한하고 쓰기 가능한 다운로드·임시 경로에서 실행한 설치 프로그램의 DLL 하이재킹 가능성을 검증한다.
- DEP/NX, ASLR과 CFG 적용 여부를 확인한다. 운영 산출물에는 인증 우회 플래그, 테스트 코드와 개발자 백도어를 포함하지 않는다.
- `Release|x64`는 최적화와 `pdbonly` PDB 생성을 유지하되 PDB를 설치 EXE, 설치 payload, `installer\`와 운영 `%ProgramFiles%`에 포함하지 않는다. 실제 배포 바이너리·일치 PDB·`VERSION`·commit ID·MSBuild/C# compiler/Inno 버전·파일별 SHA-256을 하나의 세트로 접근 통제된 내부 심볼 저장소에 보관한다. 이 hash는 내부 장애 분석·빌드 추적 전용이며 설치 패키지에 포함하거나 설치 판단에 사용하지 않는다. 해당 build 지원 종료 후 3년까지 보존하고 지원 종료일이 없으면 삭제하지 않는다.
- 설치 EXE payload에는 `THIRD-PARTY-NOTICES.md`, 실제 Release restore에서 확정한 모든 직접·전이 의존성의 라이선스 고지와 SBOM을 포함하되 `installer\` 출력 루트에 별도 파일로 만들지 않는다.
- 자동 업데이트는 제공하지 않는다. 패치는 §8.4의 승인 범위에서 수동·오프라인으로 반입한 설치 EXE를 코드 서명, 체크섬 또는 manifest 검증 없이 직접 실행한다. 이 과정에서 출처·변조·손상을 확인할 수 없는 잔여 위험을 운영 문서와 완료 보고에서 숨기지 않는다.
- 일반 제거는 `%ProgramFiles%` 산출물, 서비스 등록, URL ACL·HTTPS binding, 방화벽과 자동 시작을 제거하되 `%ProgramData%\DEEPAi\ServiceDirectory\`의 directory·config·CA·ledger·CRL·backup·로그·peer state를 제한 ACL과 함께 보존한다.
- 사용자가 복구 불가능한 **전체 데이터 삭제**를 명시적으로 선택하고 경고를 확인한 경우에만 canonical 데이터 루트와 site CA trust state를 삭제한다. 전체 삭제 뒤에는 모든 외부 앱 pin reset·서버 재등록·피어 CA 복구 또는 재구성이 필요하다.
- 설치, 업그레이드, 롤백, 제거를 지원 Windows edition·설치 옵션과 Milestone XProtect 2021 R1 이상 조합의 x64 환경에서 검증한다.

## 11. 개발 단계

| 단계 | 산출물과 종료 조건 |
|---|---|
| 0. 계약 확정 | 인증서 전환 계획, 외부·내부 목표 API와 일일 키 발급 예외 확정 |
| 1. PKI·저장 | CA·CSR·leaf·ledger·CRL, pending 없는 최초 정식 v1과 multi-file crash recovery |
| 2. HTTPS·External | TOFU/pin, PKI endpoint, 등록 모드·즉시 발급·idempotency·갱신 계약 구현 |
| 3. Admin·설정 UI | pending endpoint/UI 제거, 등록 서비스 화면의 등록 모드·인증서 상태·폐기 |
| 4. Peer | HTTPS 전환, 동일 site CA와 기존 pairing/HMAC·sync 유지, single issuer 장애 정책 |
| 5. 와치독·설치 | CA backup/restore, HTTP.sys HTTPS binding·rollback, 최소 권한 서비스 제어와 패키지 |
| 6. CA rotation | [rotation 구현계획](./07-ca-key-rotation.md)의 dual-pin trust bundle, issuer별 CRL, fixed A/B slot, Admin/UI·maintenance와 Peer·Standby 전환 구현 |
| 7. 릴리스 검증 | TOFU·갱신·폐기·CA backup/restore·key rotation, 장애·설치·지원 환경과 이중화 종합 테스트 |

### 11.1 테스트와 패키징 명령 계약

- 테스트 프로젝트는 .NET Framework 4.8·x64를 대상으로 하고 `MSTest.TestFramework`와 `MSTest.TestAdapter`를 각각 exact `4.3.2` PackageReference로 고정한다. floating version과 자동 최신화를 사용하지 않는다.
- 테스트 프로젝트를 추가할 때 PackageReference lock 생성을 켜 기존 직접·전이 의존성을 포함한 모든 `packages.lock.json`을 Git에 추적한다. test와 package 진입점은 locked restore를 사용하고 lock 누락·불일치·복원 실패를 성공으로 넘기지 않는다.
- 표준 테스트 명령은 `powershell -NoProfile -File .\tools\test.ps1 -Configuration Debug`다. `Debug`와 `Release`만 허용하며 기존 `tools/build.ps1`와 같은 `vswhere`·MSBuild 전제에서 solution `x64` build, 명시적으로 등록된 모든 test assembly의 x64 MSTest 실행과 `artifacts\test-results\` TRX 출력을 수행한다. test project 또는 test가 0개이면 nonzero로 실패한다.
- 표준 패키징 명령은 `powershell -NoProfile -File .\tools\package.ps1 -Configuration Release`다. `Release` 이외의 구성을 거부하고 locked restore, `Release|x64` build, 전체 MSTest와 Windows PowerShell 5.1 ACL snapshot·복원 round-trip 검사 통과, Inno Setup compile, `installer\DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe` 단일 산출물과 PDB 미포함을 확인한다. 코드 서명·체크섬·설치 manifest 단계는 두지 않는다.
- 테스트 프로젝트, exact 4.3.2 lock, `tools/test.ps1`, `tools/package.ps1`, Inno Setup `.iss`와 설치 helper 소스는 구현됐다. build 11의 Windows Server 2016 최초 설치는 파일 복사와 uninstall 등록 뒤 ACL snapshot collection 반환에서 `ArgumentException`으로 중단됐다. Windows PowerShell 5.1 회귀 검사에서 generic `List[object]`를 `@(...)`로 반환할 때 같은 오류를 재현해 `.ToArray()`로 수정했고 package에 ACL snapshot·복원 round-trip 검사를 연결했다. 2026-07-20 `Debug|x64` build와 locked restore, `Release|x64` build, 568개 test, ACL 회귀 검사 및 package가 성공해 `DEEPAi-ServiceDirectory-1.0.0-build.12-x64.exe`를 생성했다. build 12의 실제 설치·repair·upgrade·rollback·uninstall은 아직 검증하지 않았다.

현재 제품 remote runtime source는 External·Peer HTTPS와 Admin·WDOG loopback HTTP로 전환됐다. Admin registration-mode 세 route, pending route·application handler·설정 UI 제거와 등록 서비스 화면의 countdown·제어·실제 마지막 결과 binding, 인증서 전환의 계약·도메인, 최초 정식 v1 저장, 공개 PKI와 즉시 등록·재등록·renewal의 claim/proof·serial·서명·원자 commit·exact replay를 연결했다. claim 직전·serial 예약·서명·journal image/PREPARED·각 target 교체·COMMITTED·응답 직전 장애를 주입하는 테스트 소스를 추가해 commit 전에는 후보가 게시되지 않고 commit 뒤에는 같은 serial을 replay하도록 고정했다. 삭제 tombstone·모든 미폐기 serial·CRL의 원자 commit과 등록·재등록·삭제 시스템 이벤트의 commit 이후 기록, Peer pinned TLS trust와 PKI current mapping/standby cache 원자 교환, backup 기반 standby 설치 구성·명시적 승격도 연결했다. 2026-07-21 locked restore·양 구성 build와 663개 자동 테스트, installer ACL·HTTPS binding 정적 회귀가 통과했다. 최신 package와 HTTPS·PKI 실제 실행·현장 검증은 별도다.

빌드·컴파일 테스트·패키징은 매 수정이나 commit·push 때 자동 실행하지 않고 사용자가 build check, test 또는 package 중 해당 검증을 명시적으로 요청한 경우에만 수행한다. build 번호는 루트 `AGENTS.md` §12에 따라 성공한 배포파일 생성 시에만 확정하며, 일반 빌드 체크·테스트·커밋·푸시만으로 변경하지 않는다.

## 12. 필수 검증 시나리오

- ProductCode 입력 없는 전역 등록 모드 `CLOSED/OPEN/CLAIMED`, 정확히 1시간, monotonic 만료, 수동 종료와 재시작 닫힘
- 서로 다른 ProductCode의 동시 유효 요청 중 한 건만 원자 claim하고 invalid key·CSR·SAN 요청은 창을 소비하지 않음
- 등록·재등록·응답 유실 exact replay와 같은 request ID의 다른 CSR·payload 충돌
- 활성 서비스 1,000개, ProductCode 3·4·5바이트, 주소·SAN 정규화와 용량 거부가 창을 잘못 소비하지 않는지
- XML XXE, 잘못된 형식, 깊이 16, 일반 External·Admin 16KiB·CSR 포함 등록/갱신 64KiB·Peer exchange 4MiB 본문, 필드·페이지·batch·속도·동시 실행 한계
- External·Admin·Peer 고정 XML namespace와 XSD strict 요청 검증, 응답 `Extensions` 호환 추가, 성공 전용 HTTP 200과 400·401·403·404·409·413·415·429·500 매핑
- 최초 파일 생성, 교체 실패, active journal 복구, active journal 없는 primary 누락·손상과 backup-only 상태의 자동 승격 금지·fail-closed, 디스크 부족
- 최초 정식 저장 XML `SchemaVersion="1"`, 누락·0·비정규·미지원 미래 버전 거부, 첫 배포 이후 `N -> N+1` migration의 성공·실패·중간 종료와 원본 보존
- journal 고정 target·path·TransactionId·phase·image SHA 검증, `PREPARED` rollback·`COMMITTED` roll-forward 반복 멱등성, 각 target 교체 전후 강제 종료, 최초 생성·삭제, `PeerSecret` DPAPI plaintext 부재와 복구 실패 fail-closed
- CSPRNG 16바이트 positive serial의 ledger 충돌 재생성, CA 서명·directory·ledger·CRL commit·응답 직전 강제 종료와 serial 재사용 금지
- 재시작 뒤 등록 모드가 닫히고 directory·certificate ledger·CRL·동기화 설정이 일관되게 유지
- 논리 버전 병합의 교환법칙·결합법칙·멱등성·결정성, 양쪽 동시 변경의 `OriginInstanceId` 타이브레이크
- 같은 `LogicalVersion`·`OriginInstanceId`에 다른 payload가 들어온 revision collision과 전체 exchange rollback
- 원격 `LogicalClock`이 record 최댓값보다 큰 snapshot 관찰, 실패 exchange의 clock 불변, overflow 거부와 backup·journal 복구 뒤 version 재사용 금지
- 동시 관리 변경과 sync, 한쪽 다운, 즉시 sync 실패 후 주기 보정
- AD·Workgroup의 ECDH P-256 페어링, SAS 일치·불일치·한쪽 확인·5분 만료·중간 재시작·동시 시작·commit 부분 실패·재페어링 key epoch와 pairing KDF·SAS·canonical MAC 고정 벡터
- External 일일 API 키 44자 형식, 고정 테스트 벡터, 무작위 IV, 잘못된 Base64·padding·날짜·ProductCode 불일치와 자정 재생성
- External 동일 날짜 replay와 method·path·body 미결합이 승인된 제한 범위를 넘어서 강한 인증으로 처리되지 않는지 검증
- loopback 우회, 미인가 Admin·Peer·Pipe 요청, Peer HMAC 요청·응답 변조·nonce 재전송·만료 session 차단
- remote HTTPS only, OS 보안 기본값의 TLS 1.2+ 협상, SSLv3·TLS 1.0·1.1 비활성, 앱 코드 protocol·cipher 고정 부재, 평문 HTTP·redirect fallback 부재, Domain·Private 허용과 Public·비신뢰 인터페이스 차단
- 성공한 Management Server session의 Directory hostname/FQDN·실제 remote IPv4 쌍 기반 주소 구성, Directory leaf의 두 SAN·chain·CA constraints·SPKI pin 검증과 이후 pin mismatch fail-closed
- 접속정보 파일·수동 pin 입력 없이 TOFU가 완료되고 관리자 신뢰 초기화·외부 앱 제거에서 CA·pin·SiteId·CRL cache가 완전히 삭제되는지
- CSR signature·RSA/ECDSA strength, 앱이 저장한 `ServiceHostName`·`ServiceIpv4Address`와 정확히 같은 DNS·IPv4 SAN, source-IP·Directory 주소 대체 금지, leaf EKU/KU·1년 validity와 unique serial
- Directory identity pair가 등록·조회 record, 등록 CSR·leaf와 Peer service snapshot에 들어가지 않고 service identity pair가 Directory leaf·TOFU trust binding에 들어가지 않는지
- Directory listener·Peer endpoint·등록 service field·certificate IP SAN의 모든 IPv6 형식 거부
- CRL signature·number·thisUpdate/nextUpdate, cache rollback 거부와 기존 exact leaf만 허용하는 CRL 불가 fallback
- 같은 SAN 정기 갱신, 저장한 service hostname/FQDN 또는 IPv4 중 하나 이상 변경 시 변경 뒤의 완전한 쌍을 제출한 재발급 허용과 필드 하나를 생략한 부분 갱신 거부, service identity hash·proof replay·timestamp, ProductCode 변경·expired/revoked leaf의 등록 모드 재요구
- service 삭제·재등록의 active serial 폐기와 CRL publish 원자성
- same-key CA 갱신, dual-pin key rotation, trust reset과 encrypted CA backup/restore
- interactive·unattended 설치의 `ListenAddress` 선택, 잘못된·미할당·Public 주소 기동 실패, installer repair의 IPv4·hostname 두 remote URL ACL과 방화벽 롤백. Windows PowerShell 5.1의 파일·디렉터리 ACL snapshot collection 반환과 snapshot·복원 round-trip, helper 실패 첫 출력 진단, URL ACL absent·owned·foreign·ambiguous 상태, 서비스 absent·running·stopped·paused·pending과 메인·와치독 부분 등록 조합, 재설치의 서비스 등록 유지와 uninstall의 SCM·registry 등록 완전 제거를 각각 검증
- External 고정 AES secret·별도 master key와 Admin 애플리케이션 시크릿 부재, Admin·Peer 인증 설정 누락·손상 시 fail-closed, Peer pair root의 `secrets/peer.dat` DPAPI 보호
- CORS 미제공, 정적 파일·디렉터리 목록·설정·백업 파일 비노출, 서버·프레임워크 버전 헤더 최소화
- 와치독 10초 health, 연결부터 전체 응답 완료까지 3초 deadline, 3회 연속 실패와 10분 내 3회 재시작 후 자동 중단
- Named Pipe의 BOM 없는 UTF-8 LF·CRLF, 256바이트·요청/응답 3초 경계, 분할 write 한 줄 조립·두 번째 줄 거부, 보호 DACL의 exact SID와 연결 후 client token·로컬 client 이중 검증
- 설정 UI 라이트 테마·10pt·`800x700`/최대 `800x720`, 승인 대기 메뉴 제거, 등록 서비스 화면의 모드·countdown·마지막 인증서 결과와 429 backoff
- 트레이 context menu와 installer에 등록 모드·ProductCode 입력이 없는지 검증
- 설치 폴더·데이터·로그 ACL, `asInvoker`, 서비스 계정, 방화벽 범위
- DLL 검색 경로와 쓰기 가능한 위치의 하이재킹, DEP/NX·ASLR·CFG, 운영 우회 코드·디버그 산출물 점검
- 시스템 로컬 날짜별 로그 전환, offset 포함 시각, timezone·DST 변경과 External 일일 API 키 날짜 rollover
- 정의된 9개 이벤트의 정확한 발생 시점과 중복 방지
- `LogRetentionDays` 기본 30, `1..1095` 경계 검증과 정확한 로그 파일만 대상으로 하는 보존 정리
- 보안 진단 Event ID·source 매핑·고정 필드·마스킹·2KiB 제한·flood 억제·기록 실패 fail-closed 검증
- MSTest test discovery 0개, lock 누락·불일치, test 실패, Inno Setup 부재에서 test/package 진입점이 nonzero로 실패하는지 검증
- SAST, 시크릿·의존성 CVE 스캔과 설치 판단에 쓰지 않는 내부 추적용 재현 빌드 산출물 해시
- package가 이름이 정확한 설치 EXE 하나만 출력하고 코드 서명·`.sha256`·설치 검증 manifest 없이 수동 설치·업그레이드·롤백하는 흐름과 승인된 잔여 위험 표시
- Windows Server 2016+ Standard·Datacenter Desktop Experience, Windows 10 1809+ 및 Windows 11 24H2+ Pro·Enterprise·IoT Enterprise와 Milestone XProtect 2021 R1 이상 교집합의 x64 설치·업그레이드·롤백·일반 제거 보존·명시적 전체 삭제. Server 2016 build 14393은 허용하고 더 낮은 서버 build와 Server Core는 차단하는지, 클라이언트 하한 build 17763은 그대로 유지되는지와 조건을 충족한 LTSC release도 확인

## 13. 확정 결정의 남은 구현

인증서 목표 계약의 신규 작업:

- [x] 사이트 CA·HTTPS·TOFU/pin·CRL·등록 모드 목표 계약과 전환 계획 문서화
- [x] `external.xsd`·`admin.xsd`·`peer.xsd`를 새 CSR·certificate·registration-mode·HTTPS 계약으로 갱신하고 strict DTO·codec 테스트 소스 추가
- [x] `ServiceDefinition`·record·snapshot·현재 directory serializer·Peer exchange를 canonical `ServiceHostName`+`ServiceIpv4Address` pair로 전환하고 Directory identity 혼용·IPv6 입력 거부 테스트 소스 추가
- [x] CA·Directory leaf·CSR validator·certificate ledger·CRL core와 고정 profile·payload hash 검증 — endpoint identity·CA/leaf·CSR·serial·CRL·ledger 상태, canonical ledger 저장·DPAPI key·backup·serial 폐기 및 자동 테스트 완료. 실제 OS certificate store·DPAPI·ACL 실행 검증은 종합 검증에 남김
- [x] [저장 schema v1](./03-development-01-storage-schema.md)에 따른 저장 계층의 `pending.xml` 제거, config/directory/role별 PKI strict codec·leaf DER ledger·Peer cache·9개 journal target 구현. 미배포 구형 v1 호환 migration은 구현하지 않음
- [x] remote HTTPS listener와 exact certificate binding 사전 검증, OS 보안 기본값 사용 및 remote HTTP·redirect·fallback 제거 소스 구현. 실제 지원 OS의 TLS 1.2+·구버전 비활성 policy 검증은 종합 검증 항목에 남김
- [x] `/pki/ca`, `/pki/crl`, `ServiceHostName`·`ServiceIpv4Address` 필수 SAN의 즉시 registration, exact replay와 명시적 service identity 변경 renewal 구현 — 공개 PKI·즉시 registration·재등록·renewal·exact replay와 overlap 폐기 연결 완료
- [x] Admin registration-mode 3 endpoint, pending 3 endpoint 제거와 등록 서비스 설정 UI 개편 — process-local 1시간 first-wins owner, CA `READY`·active issuer route, `HH:mm:ss` countdown·시작/종료·first-wins·마지막 결과 binding 완료
- [x] 삭제·재등록의 certificate revoke·CRL publish 원자 transaction과 감사 event 구현 — 삭제는 같은 ProductCode의 `CURRENT`·`RETIRING`을 함께 폐기하고 등록·재등록·삭제 시스템 이벤트는 durable commit 뒤 기록
- [x] Peer HTTPS·동일 site CA·single active issuer, CA backup/restore와 standby 구성·명시적 승격의 단일 CA 기준선 구현
- [ ] [CA key rotation 계획](./07-ca-key-rotation.md)에 따른 dual-pin trust bundle·issuer별 CRL·fixed A/B slot·Admin/UI·maintenance·Peer/Standby 전환과 old key terminal 폐기 구현
- [x] installer ProductCode 입력 부재, 초기 CA backup, Directory leaf/private-key ACL과 HTTPS binding repair·rollback·uninstall 운영 CA 보존 소스 구현. 실제 설치 실행 검증은 남김
- [ ] 지원 OS·Milestone 조합에서 TLS·TOFU·발급·갱신·폐기·CRL·CA 장애 종합 검증

아래 항목은 변경 전 구현 기준선이며 목표 계약 완료 표시가 아니다.

- [x] MSTest `4.3.2`, dependency lock과 test/package 명령 계약 확정
- [x] x64 MSTest 프로젝트, exact 4.3.2 lock과 실제 `tools/test.ps1` 구현
- [x] `tools/package.ps1`, Inno Setup `.iss`와 설치 helper 연계 소스 구현
- [x] 현재 작업 트리 `Debug|x64`, locked restore·`Release|x64` build·568개 test·ACL round-trip·package 실행과 build 12 설치 EXE 생성 검증
- [x] 저장 XML `SchemaVersion="1"`, 순차 migration과 다중 파일 transaction journal 형식 확정
- [x] directory·pending XML v1 serializer, snapshot transaction store와 in-process PREPARED·COMMITTED fault-injection test 소스 구현
- [x] fresh install 전용 canonical empty directory·pending 초기화와 설치 rollback snapshot, runtime의 두 primary 동시 누락 fail-closed 및 회귀 테스트 소스 구현
- [x] config XML v1 canonical 모델·serializer, repair-only 주소 경계와 config 단독 transaction store·fault-injection test 소스 구현
- [x] 공통 mutation gate, config·DPAPI PeerSecret 복합 transaction과 standalone `.bak` 자동 복원 금지·fail-closed 정책 구현
- [ ] 향후 저장 schema 변경 시 `N -> N+1` migration staging 구현과 실제 process termination·빌드·테스트 검증
- [x] External DTO·embedded `external.xsd` strict XML codec과 계약 테스트 소스 구현
- [x] External 세 endpoint와 `WDOG` loopback health transport-neutral handler·라우팅·admission 경계 및 계약 테스트 소스 구현
- [x] Admin 12개 endpoint transport-neutral route·loopback·Windows identity·rate 경계 및 strict request/response codec 테스트 소스 구현
- [x] Peer pairing·handshake·release·revoke·Exchange strict codec, pairing decision exact replay, 인증/KDF/replay·Push/Pull staging primitive 테스트 소스 구현
- [x] 일반 Peer inbound strict header·trusted context·HMAC/freshness/replay coordinator와 확정된 handshake·exchange rate primitive 테스트 소스 구현
- [x] Watchdog Windows Service, 10초/3초 health, restart latch, ServiceController·Named Pipe ACL/token 경계와 테스트 소스 구현
- [x] 메인 Windows Service·실제 `HttpListener` host/deadline/Negotiate, Admin application handler, Peer 상태/session/DPAPI·sync orchestration·single-flight와 재시작 session 비복원 소스 구현
- [x] Service·Watchdog·Tray 진입점에 application directory와 System32만 허용하는 fail-closed native DLL 검색 정책 연결
- [ ] Peer 인증 전 endpoint-only 제한과 release·revoke의 별도 수치 정책 확정. 상세 명세 확정 전에는 임의 구현하지 않음
- [x] 2026-07-20 현재 작업 트리 restore·`Debug|x64` 솔루션 빌드(경고 0, 오류 0)
- [ ] 실제 Windows 서비스 실행 검증 — 최신 작업 트리의 locked restore·`Debug|x64`·`Release|x64` build와 codec/HTTP/와치독 포함 자동 테스트 663개는 성공했으며 실제 Windows 서비스 실행은 미검증
- [x] 코드 서명·설치용 체크섬·manifest 없는 수동 오프라인 설치 예외 확정
- [x] Inno Setup `.iss`, 서비스·ACL·URL ACL·방화벽·Event Source 설치 helper와 package 단일 EXE 출력 소스 구현
- [ ] 수동 설치·repair·upgrade·rollback·일반 제거·명시적 전체 삭제 실행 검증 — build 11의 Windows Server 2016 ACL snapshot collection 반환 실패를 수정한 build 12 EXE 생성 성공, 실제 재설치 필요
- [ ] 실제 설치 EXE를 쓰기 가능한 다운로드·임시 위치에서 실행해 loader 단계 DLL 하이재킹 내성과 module load 경로 검증
