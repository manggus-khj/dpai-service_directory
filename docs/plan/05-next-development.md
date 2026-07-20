# 다음 개발 실행계획

```text
최초 작성일: 2026-07-20
최종 변경일: 2026-07-20
revision: 3
```

## 1. 목적과 범위

이 문서는 [인증서 전환 변경계획](./02-certificate-transition.md#8-구현-단계와-종료-조건)과 [전체 개발 단계](./03-development.md#11-개발-단계)를 실제 개발 순서로 나눈 실행계획이다. 저장 형식은 [최초 정식 저장 schema v1](./03-development-01-storage-schema.md), API 필드·오류·인증 절차는 [외부 API](./04-api-01-external-application.md)와 [내부 API](./04-api-02-internal.md)를 단일 원본으로 사용한다.

다음 개발의 목표는 현재의 `HTTP + pending 승인` runtime을 `HTTPS + 등록 모드 + CSR 즉시 발급` runtime으로 완전히 전환하는 것이다. 새 계약의 일부만 원격에 노출하거나 HTTP와 HTTPS를 장기간 병행하지 않는다. 새 도메인·저장·프로토콜 구현은 테스트 가능한 내부 단위로 먼저 추가하고, 저장 복구·Admin 등록 모드·HTTPS 설치 흐름·External 발급이 모두 준비된 시점에 원격 runtime을 한 번에 전환한다.

## 2. 현재 기준선

- 현재 제품 버전은 `v1.0.0 build 12`다.
- 사이트 CA, Directory/service leaf, CSR 검증, serial, certificate ledger, signed CRL, DPAPI CA key, 암호화 backup, CA 상태·원장 조회·serial 폐기 Admin/UI와 repair restore 진입점 소스가 있다.
- 2026-07-20 `Debug|x64`, locked restore·`Release|x64`, 568개 자동 테스트, Windows PowerShell 5.1 ACL round-trip과 build 12 패키징은 성공했다.
- 실제 remote runtime은 여전히 HTTP, 단일 `ServerAddress`, `pending.xml`, 승인·거절 API와 승인 대기 UI를 사용한다.
- build 12의 Windows Server 2016 실제 재설치와 DPAPI·서비스 SID ACL·SCM·HTTP.sys 현장 동작은 검증하지 않았다.

## 3. 확정된 데이터 전환 정책과 원칙

### 3.1 미배포 초기화 정책 확정

이 제품은 아직 배포된 적이 없고 외부 앱·운영 등록 데이터도 없다. 따라서 build 12 이하의 단일 `ServerAddress`·`pending.xml` 형식은 배포 호환성 대상이 아닌 개발 기준선으로 확정한다. 해당 레코드만으로 목표 schema의 `ServiceHostName`과 `ServiceIpv4Address`를 모두 신뢰성 있게 만들 수 없으며 DNS 조회, 요청 source IP, 임의 NIC 선택으로 누락값을 보완하지 않는다.

- 인증서 기반 목표 형식을 최초 정식 `SchemaVersion="1"`로 구현한다.
- `pending.xml`과 단일 `ServerAddress` serializer·상태 머신은 최초 정식 형식에서 제거한다.
- build 12 이하 개발·테스트 데이터의 자동 migration, 운영자 hostname·IPv4 매핑 UI와 하위 호환 reader는 구현하지 않는다.
- 기존 개발·테스트 설치는 서비스를 중지하고 명시적으로 정확한 `%ProgramData%\DEEPAi\ServiceDirectory\` 데이터 루트를 초기화한 뒤 fresh install한다. 다른 경로나 상위 `%ProgramData%\DEEPAi\`를 삭제 대상으로 넓히지 않는다.
- 첫 정식 배포 뒤 schema를 변경할 때부터 명시적인 `N -> N+1` migration과 before image·rollback을 구현한다.

정식 v1 이후에는 active journal이 없는 primary 누락·손상이나 schema 불일치를 자동 초기화하지 않고 기존 복구 원칙대로 fail closed한다. 미배포 개발 데이터 초기화 정책을 운영 데이터 자동 삭제 근거로 재사용하지 않는다.

### 3.2 전환 원칙

- CA key rotation·dual-pin 배포는 이번 실행계획에서 제외한다. 기존 CA backup 복원은 rotation으로 사용하지 않는다.
- API version 경로·필드, 임시 호환 endpoint, HTTP redirect와 인증서 검증 우회를 추가하지 않는다.
- 등록 모드는 process-local이며 재시작 시 항상 `CLOSED`다. ProductCode와 등록 모드 상태를 설치 입력이나 `config.xml`에 저장하지 않는다.
- Directory identity는 로컬 Management Server hostname/FQDN 한 개와 선택한 IPv4 `ListenAddress` 한 개다. 등록 서비스 identity와 서로 복사하지 않는다.
- 각 작업 단위는 새 코드와 실패 경로 테스트를 함께 추가한다. 실제 빌드·테스트·패키징 실행은 사용자가 별도로 요청한 경우에만 수행한다.

## 4. 실행 순서

| 순서 | 작업 단위 | 핵심 산출물 | 완료 기준 |
|---:|---|---|---|
| 0 | build 12 설치 기준선 확인 | Windows Server 2016 fresh install·중지 서비스 재설치·일반 제거 로그 | ACL snapshot 오류가 재발하지 않고 서비스 등록·제거 상태가 계획과 일치 |
| 1 | 계약·도메인 기반 전환 | 목표 External/Admin/Peer XSD·DTO·codec, 서비스 DNS+IPv4 identity 모델 | strict XML·정규화·IPv6 거부·기존 `ServerAddress` 잔존 검사 테스트 소스 완료 |
| 2 | 저장 schema와 등록 transaction | 최초 정식 v1 serializer, pending 제거, idempotency·directory·ledger·CRL 복합 commit | 모든 fault point에서 전부 rollback 또는 전부 roll-forward, serial 재사용 없음 |
| 3 | Admin 등록 모드와 설정 UI | mode 조회·열기·닫기, pending endpoint/UI 제거, countdown·last result | 1시간·수동 종료·재시작 닫힘·동시 first-wins 상태 전이 테스트 소스 완료 |
| 4 | HTTPS와 설치 전환 | Directory leaf provisioning, exact HTTP.sys binding, HTTPS listener·repair rollback | remote HTTP 부재, Admin/WDOG loopback 유지, binding·ACL·방화벽 실패 시 원상 복구 |
| 5 | External 발급 전환 | `/pki/ca`, `/pki/crl`, 즉시 등록·exact replay·renewal | 외부 API 계약과 CSR/SAN/일일 키/등록 모드/idempotency 테스트 소스 완료 |
| 6 | 삭제·재등록과 Peer | 삭제·재등록 revoke/CRL transaction, Peer HTTPS·PKI state·single issuer | 서비스·인증서 상태 원자성, 낮은 CRL/ledger와 split-brain issuer 거부 |
| 7 | 릴리스 검증 | 지원 OS·Milestone 교집합 설치·TLS·TOFU·발급·갱신·폐기·복구 검증 기록 | [개발계획 §12](./03-development.md#12-필수-검증-시나리오)의 해당 시나리오 통과 |

순서 0의 현장 환경을 즉시 사용할 수 없더라도 순서 1~3의 소스 작업은 진행할 수 있다. 다만 순서 4에서 installer를 다시 변경하기 전에는 build 12 설치 실패가 해결됐는지 확인해 기존 installer 문제와 HTTPS 전환 문제를 분리한다.

## 5. 작업 단위별 상세 계획

### 5.1 계약·도메인 기반

- `external.xsd`와 External DTO·codec에 PKI 조회, `ServiceHostName`·`ServiceIpv4Address`, CSR registration response와 renewal 계약을 반영한다.
- `admin.xsd`와 Admin DTO·codec에 registration-mode 세 endpoint를 반영하고 pending 세 endpoint 모델을 제거한다.
- `peer.xsd`와 Peer DTO·codec에 서비스 DNS+IPv4 pair와 PKI state high-water를 반영한다.
- `ServiceDefinition`, `ServiceRecord`, snapshot·sync 모델에서 단일 `ServerAddress`를 제거하고 `ServiceEndpointIdentity`의 canonical DNS+IPv4 pair만 사용한다.
- 알 수 없는 XML, 부분 identity, 비정규 DNS·IPv4, 모든 IPv6 표현과 Directory identity를 서비스 identity로 잘못 넣는 경우를 거부하는 계약 테스트를 추가한다.

이 단계에서는 새 원격 endpoint를 활성화하지 않는다. 목표 codec과 도메인 모델을 먼저 완성해 이후 저장·handler가 같은 타입을 사용하게 한다.

### 5.2 저장 schema·복구·등록 transaction

- [저장 schema v1](./03-development-01-storage-schema.md)에 따라 directory·config·PKI metadata·active issuer ledger·standby Peer cache strict codec, DER·DPAPI 검증과 9개 journal target을 구현한다. build 12 이하 구형 v1 reader·migration·운영자 매핑 경로는 추가하지 않는다.
- `pending.xml`을 목표 state에서 제거하고 등록 모드 상태는 메모리에만 둔다. 개발·테스트 구형 데이터는 §3.1의 명시적 초기화 절차로만 처리한다.
- 성공한 registration/renewal request ID, CSR hash, canonical semantic payload와 공개 인증서 결과를 ledger에 내구 저장한다. exact replay만 같은 결과를 반환하고 같은 ID의 다른 payload는 충돌로 거부한다.
- 등록·재등록·삭제와 service definition이 바뀌는 갱신에서 필요한 `directory.xml`, certificate ledger, CRL과 logical clock을 mutation gate 안의 한 recovery transaction으로 commit한다. definition이 그대로인 갱신과 serial 단독 폐기는 바뀌지 않는 directory target을 포함하지 않는다.
- claim 직전, serial 예약, 서명, 각 파일 교체, COMMITTED 전후와 응답 직전 장애를 주입해 부분 게시·serial 재사용·CRL number rollback이 없음을 검증한다.

### 5.3 Admin 등록 모드·설정 UI

- process-local `CLOSED/OPEN/CLAIMED` owner를 monotonic deadline으로 구현한다. 유효성 검사가 끝난 첫 요청만 원자 claim하며 invalid key·CSR·SAN·용량 초과는 창을 소비하지 않는다.
- Admin route·handler에 registration-mode 조회·열기·닫기를 연결하고, CA가 `READY`가 아니거나 active issuer가 아니면 열기를 거부한다.
- pending 조회·승인·거절 route, application handler, DTO와 설정 UI를 제거한다.
- `등록 서비스` 화면 상단에 상태, `HH:mm:ss` countdown, 시작·종료, first-wins 경고와 마지막 성공 결과를 표시한다. tray context menu에는 등록 모드를 추가하지 않는다.

### 5.4 HTTPS·설치

- 설치·repair가 로컬 Management Server hostname/FQDN과 선택한 IPv4를 검증하고, CA certificate가 아니라 Directory leaf에 정확한 DNS·IP SAN과 두 absolute HTTPS CRL URI를 발급한다.
- HTTP.sys exact `ipport` certificate binding, URL ACL, Domain·Private 방화벽과 파일·private key ACL을 rollback 가능한 설치 state에 포함한다.
- 메인 서비스 기동 시 config identity, 실제 IPv4 할당·network profile, leaf/private-key 접근, SAN·chain·validity와 binding을 검증한 뒤에만 remote HTTPS listener를 연다.
- `/admin/*`와 `WDOG` health의 exact loopback 경계는 유지하고, External·Peer remote HTTP prefix·redirect·fallback을 제거한다.
- repair 주소 변경, 보존 CA leaf 재발급, 일반 제거의 binding 제거와 운영 CA·ledger·CRL 보존을 검증할 수 있는 installer 회귀 검사를 추가한다.

### 5.5 External 발급과 갱신

- `/pki/ca`와 `/pki/crl`은 HTTPS로 같은 site CA·signed DER CRL을 제공하고 cache·본문·오류 계약을 상세 명세와 일치시킨다.
- registration handler는 일일 키·ProductCode, CSR signature·key strength, exact DNS+IPv4 SAN, 등록 모드와 용량을 순서대로 검증한 뒤 한 건을 claim하고 즉시 발급 transaction을 commit한다.
- 응답 유실 뒤 exact replay는 등록 모드가 닫혀도 같은 certificate 결과를 반환한다. 다른 payload나 CSR은 충돌이다.
- renewal은 현재 유효·미폐기 leaf 개인키 proof, 새 CSR proof-of-possession과 identity hash를 검증하고, hostname 또는 IPv4가 바뀌면 변경 뒤의 완전한 pair로 재발급한다.
- 이전 승인 대기 응답·`PendingId`와 approve/reject 의존을 protocol·handler·테스트에서 제거한다.

### 5.6 삭제·재등록·Peer

- 서비스 삭제와 열린 모드 재등록은 현재 serial 폐기, CRL publish, directory tombstone/활성 record와 ledger 상태를 한 transaction으로 바꾼다.
- Peer endpoint·outbound transport를 HTTPS IPv4로 바꾸고 TLS 검증을 기존 ECDH·SAS·HMAC보다 먼저 수행한다.
- `pki-state`에서 CRL high-water와 ProductCode별 current serial을 검증해 standby 전용 `pki/peer-cache.xml`과 CRL에 원자 저장한다. full ledger·CA private key와 process-local 등록 모드는 동기화하지 않는다.
- standby 구성은 인증된 동일-site backup으로 Directory leaf와 공개 cache를 만들되 full ledger·CA key primary를 남기지 않는다. 승격은 중지된 repair에서 관찰 high-water 이상인 backup을 복원하고 issuer identity·role·`PkiRevision`을 원자 전환하는 명시적 절차로만 허용한다.
- 이번 범위는 동일 site CA와 single active issuer·명시적 승격까지다. CA key rotation·dual-pin endpoint/UI는 별도 후속 계획으로 남긴다.

## 6. 주요 변경 위치

| 영역 | 주 변경 위치 |
|---|---|
| 도메인·상태 | `src/DEEPAi.ServiceDirectory.Domain/`, `Application/State/` |
| External 계약 | `docs/plan/04-api/external.xsd`, `ExternalProtocol/ExternalApi/` |
| Admin·Peer 계약 | `docs/plan/04-api/admin.xsd`, `peer.xsd`, `InternalProtocol/Admin/`, `Peer/` |
| 저장·PKI | `Infrastructure/Persistence/`, `Infrastructure/Pki/` |
| HTTP·TLS | `Infrastructure/Http/`, `Infrastructure/Networking/`, 메인 Service composition |
| 설정 UI | `Tray/MainWindow.xaml`, `Tray/ViewModels/`, `Tray/Clients/` |
| 설치 | `installer/ServiceDirectory.iss`, `installer/scripts/`, `tools/package.ps1` |
| 검증 소스 | `tests/DEEPAi.ServiceDirectory.Tests/`, `tests/installer/` |

기존 파일이 약 1000줄을 넘었거나 이번 변경으로 책임이 섞이는 경우에만 자연스러운 책임 경계로 분리한다. 인증서 전환과 무관한 리팩터링은 함께 수행하지 않는다.

## 7. 완료 판정과 중단 조건

- 각 작업 단위가 끝날 때 관련 계획·API 명세·XSD·DTO·codec·handler·테스트의 의미가 일치해야 한다.
- 데이터 migration 또는 복구가 모호하면 listener와 snapshot을 게시하지 않고 fail closed한다.
- HTTPS certificate·binding·ACL·Event Log source 검증 중 하나라도 실패하면 remote listener를 열지 않는다.
- 실제 빌드 성공만으로 HTTPS·DPAPI·ACL·SCM·Milestone 상호운용을 완료로 표시하지 않는다.
- CA rotation, 인증 전 Peer endpoint-only 수치 제한, release·revoke 별도 제한은 상세 계약이 확정되기 전 임의 구현하지 않는다.
- 다음 구현 착수는 [todo-01.md](./todo-01.md)의 이 계획 항목을 위에서부터 수행한다.
