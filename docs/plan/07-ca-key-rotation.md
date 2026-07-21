# CA key rotation과 dual-pin 전환 구현계획

```text
최초 작성일: 2026-07-22
최종 변경일: 2026-07-22
revision: 3
```

> 결정 상태: 최초 정식 릴리스 필수 범위
> 구현 상태: 1차 구현으로 최초 정식 dual-slot state·issuer-aware ledger, A/B CA·CRL·DPAPI key, `PUBLISHED` Prepare/Cancel, dual-key backup/active repair, External trust bundle·live issuer CRL, Admin 상태·Prepare·Cancel과 설정 UI를 반영했다. Cancel request는 Admin request-side canonical GUID parser와 canonical 소문자 `D` 형식을 사용하고 rotation 저장소의 `InvalidDataException` 의존 namespace를 명시했다. `ACTIVATED` 전환과 Complete maintenance, retiring issuer mutation, retired archive, dual-CA Peer·standby와 설치 전환은 후속 구현 중이며 이 범위가 끝나기 전에는 릴리스 완료가 아니다.
> 적용 범위: 자사 Directory site CA의 계획된 key rotation만 지원한다. 고객 하위 CA와 고객 발급 서버 인증서 모드는 구현하지 않는다.

## 1. 목적과 완료 기준

이 계획은 현재 site CA의 개인키를 새 개인키로 교체하면서 외부 앱, 등록 서비스와 상대 Directory가 수동 pin 재입력이나 연결정보 파일 없이 신뢰를 이어 가는 절차를 정의한다. CA backup/restore는 같은 CA key를 복구하는 절차이고, 같은 key로 CA certificate만 갱신하는 작업은 SPKI pin이 유지되므로 둘 다 key rotation이 아니다.

rotation 완료는 다음을 모두 만족할 때만 선언한다.

- 기존 CA로 인증된 Directory TLS 연결에서 차기 CA certificate와 SPKI pin을 먼저 배포한다.
- 외부 앱과 Peer가 기존·차기 CA를 동시에 신뢰하는 dual-pin 기간을 거친다.
- Directory leaf와 신규·갱신 service leaf의 issuer를 차기 CA로 전환한다.
- 기존 CA가 발급한 활성 service leaf를 모두 차기 CA leaf로 갱신하거나 명시적으로 폐기한다.
- 두 CA의 CRL을 issuer별로 독립 제공·검증하고 CRL number rollback을 거부한다.
- active issuer, standby, backup, Peer PKI cache와 설치 상태가 같은 rotation revision으로 수렴한다.
- 기존 CA의 terminal CRL을 만든 뒤 private key를 복구 불가능하게 폐기하고, 인증된 완료 bundle을 받은 client만 기존 pin을 제거한다.
- 자동 테스트와 실제 지원 Windows·Milestone·외부 시험 앱·두 Directory 현장 검증을 통과한다.

여기서 무중단은 **인증 신뢰의 연속성**을 뜻한다. 준비된 client는 CA가 바뀐 뒤에도 관리자 trust reset 없이 자동 재연결할 수 있어야 한다. 단일 Windows 서버의 HTTP.sys binding을 안전하게 교체하는 maintenance 구간에는 짧은 drain·재연결이 있을 수 있으며, 기존 TLS connection이나 모든 TCP connection이 한 순간도 끊기지 않는다는 의미로 확대하지 않는다. 완전한 connection 무중단은 별도 front-end나 다중 active endpoint가 필요하므로 현재 제품 범위가 아니다.

## 2. 확정 결정과 비범위

### 2.1 확정 결정

- CA key rotation과 dual-pin은 후속 선택 기능이 아니라 최초 정식 릴리스의 필수 기능이다.
- 제품은 자사 site CA만 생성·운영한다. 고객 CA CSR, 고객 하위 CA, 고객 PFX와 Windows system trust mode를 추가하지 않는다.
- 제품이 아직 정식 배포되지 않았으므로 rotation 저장 형식을 최초 정식 `SchemaVersion="1"`에 포함한다. build 14 이하 개발 데이터·`.dpca`는 운영 migration 입력으로 인정하지 않고 시험 환경에서 명시적으로 초기화한다.
- planned rotation은 로컬 운영자가 시작·활성화·완료한다. 자동 key 교체, peer의 자동 승격과 네트워크를 통한 원격 강제 전환을 금지한다.
- 현재 CA가 침해되지 않았다는 전제에서만 기존 TLS 채널을 차기 pin 배포 근거로 사용한다.
- CA key compromise가 의심되면 planned rotation을 사용하지 않는다. 발급·등록·Peer를 fail closed하고 관리자 trust reset과 재설치·재등록을 포함한 사고 복구 절차로 전환한다.
- rotation 중에도 single active issuer를 유지한다. `PUBLISHED`에서는 기존 CA만, `ACTIVATED`에서는 차기 CA만 새 leaf를 발급한다.
- 기존 CA와 차기 CA의 private key를 같은 파일이나 같은 DPAPI blob에 결합하지 않는다. 고정된 두 slot에 별도로 보호하고 각 key buffer를 사용 직후 지운다.
- 동일 site의 모든 Directory가 같은 두 CA와 rotation revision을 관찰해야 하며, 각 Directory leaf는 자기 DNS·IPv4 SAN으로 별도 발급한다.

### 2.2 비범위

- 고객 CA와 AD CS 연동
- 고객이 제공한 PFX 설치 또는 Windows system trust store 기반 대체 모드
- 둘 이상의 차기 CA를 동시에 준비하는 multi-hop rotation
- CA compromise 상황의 자동 신뢰 이전
- 외부 앱별 pin 배포 확인을 강한 사용자 identity로 간주하는 것
- 서비스 디렉토리와 별개인 등록 서비스 private key의 중앙 보관·복구

## 3. 용어와 신뢰 상태

| 용어 | 의미 |
|---|---|
| current CA | 현재 Directory leaf와 신규 발급에 사용하는 CA |
| next CA | 기존 CA TLS 채널로 공개했지만 아직 Directory leaf·신규 발급에 사용하지 않는 차기 CA |
| retiring CA | 차기 CA 활성화 뒤 신규 발급을 중단했으나 기존 leaf 검증·폐기용 CRL 때문에 보존하는 이전 CA |
| retired CA | private key가 폐기되고 public certificate와 terminal CRL만 보존되는 CA |
| trust bundle | 같은 SiteId에 속한 current·next 또는 current·retiring CA certificate, SPKI pin, CRL URI와 단조 증가 revision의 집합 |
| trust continuity | 이미 current pin을 가진 client가 current TLS로 받은 next pin을 저장한 뒤 새 Directory leaf를 자동 신뢰하는 성질 |

CA role은 `CURRENT`, `NEXT`, `RETIRING`, `RETIRED` 네 값으로 고정한다. external trust bundle에는 `CURRENT`와 rotation 중의 `NEXT` 또는 `RETIRING`만 제공한다. `RETIRED`는 runtime trust 대상이 아니라 issuer별 terminal CRL 제공과 감사 근거다.

## 4. Rotation 상태 머신

내구 상태는 `STABLE`, `PUBLISHED`, `ACTIVATED` 세 값만 사용한다. `READY_TO_ACTIVATE`와 `READY_TO_COMPLETE`는 저장 상태가 아니라 현재 조건에서 계산한 표시값이다.

| 현재 상태 | 허용 명령 | 다음 상태 | 핵심 효과 |
|---|---|---|---|
| `STABLE` | Prepare | `PUBLISHED` | 빈 slot에 새 CA key/certificate·초기 CRL을 생성하고 dual-pin bundle을 공개 |
| `PUBLISHED` | Cancel | `STABLE` | 차기 CA가 발급에 사용되지 않았음을 확인하고 차기 key·public state를 원자 폐기 |
| `PUBLISHED` | Activate | `ACTIVATED` | 차기 CA를 current로, 기존 CA를 retiring으로 바꾸고 Directory leaf·HTTP.sys binding 전환 |
| `ACTIVATED` | Complete | `STABLE` | terminal CRL 생성, 기존 private key 폐기, public artifact를 retired archive로 이동하고 free slot 확보 |

다음 전이는 금지한다.

- `PUBLISHED`가 아닌 상태의 Cancel
- `ACTIVATED`에서 기존 CA로 rollback
- 준비되지 않은 차기 CA를 current로 지정
- 같은 rotation에서 두 번째 next CA 생성
- 네트워크 partition 중 standby 자동 Activate·Complete
- `UInt64.MaxValue` revision에서 wrap하거나 새 rotation 시작

### 4.1 Prepare

Prepare는 active issuer의 CA 상태가 `READY`이고 등록 모드가 `CLOSED`이며 active recovery journal이 없을 때만 허용한다.

1. CSPRNG로 `RotationId` GUID와 RSA 3072 차기 CA key를 생성한다.
2. 현재 CA와 같은 SiteId·profile을 갖되 새 serial·새 SPKI인 self-signed CA certificate를 만든다.
3. CRL number `1`의 빈 차기 CRL을 만든다.
4. free slot의 CA·CRL·DPAPI key와 `PUBLISHED` metadata를 한 recovery transaction으로 저장한다.
5. `TrustRevision`과 `PkiRevision`을 증가시키고 `PublishedUtc`, `ActivationNotBeforeUtc`를 저장한다.
6. 기존 CA가 발급자와 Directory TLS issuer로 계속 동작하는 상태에서 `/pki/ca`로 dual-pin bundle을 공개한다.
7. 새 revision을 포함한 암호화 CA backup이 성공하기 전까지 Activate를 금지한다.

Prepare 뒤 차기 CA는 service leaf나 Directory leaf를 발급하지 않는다. 따라서 Cancel은 차기 CA 파일을 원자 폐기하고 기존 상태로 되돌릴 수 있다.

### 4.2 Activate

Activate는 다음 조건을 모두 만족해야 한다.

- `PUBLISHED`이고 최소 배포 기간 30일이 지났다.
- current revision 이상인 승인된 암호화 backup이 있다.
- 구성된 standby/peer가 rotation bundle revision과 두 CA·CRL hash를 인증된 Peer exchange로 관찰했다.
- active와 구성된 standby 각각에 차기 CA가 발급한 자기 DNS·IPv4 Directory leaf가 staging되어 있다.
- 등록 모드는 `CLOSED`이고 신규 발급·갱신·폐기 transaction과 sync commit이 실행 중이지 않다.
- 차기 CA와 CRL, private key, Directory leaf, HTTP.sys binding의 사전 검증이 성공했다.

Activate maintenance는 와치독의 자동 재시작을 억제하고 메인 서비스 요청을 drain한 뒤 fixed before state를 저장한다. CA slot role·ledger issuer 상태와 Directory leaf·exact HTTP.sys binding을 하나의 복구 가능한 maintenance 작업으로 전환한다. 실패하면 기존 CA leaf·binding과 `PUBLISHED` 파일 상태 전체로 rollback하고, rollback이 완전하지 않으면 두 서비스를 중지한 채 repair를 요구한다.

성공 후:

- 차기 CA가 `CURRENT`, 기존 CA가 `RETIRING`이 된다.
- 새 등록과 갱신은 차기 CA로만 발급한다.
- 기존 CA private key는 기존 serial 폐기와 CRL 갱신에만 사용하고 새 leaf 서명에는 사용할 수 없다.
- Directory는 새 CA leaf로 TLS를 제공하고 trust bundle에는 새 CA `CURRENT`, 기존 CA `RETIRING`을 넣는다.
- Activate revision을 포함한 새 backup 전에는 등록 모드 open과 신규 발급을 `BACKUP_REQUIRED`로 차단한다.
- client는 짧은 maintenance 연결 실패에 bounded retry를 적용하되 pin 검증을 완화하지 않는다.

### 4.3 기존 service leaf 이관

등록 서비스 앱은 `/pki/ca`를 Directory session마다 확인하고 최소 24시간마다 다시 조회한다. `ACTIVATED` bundle에서 자기 현재 leaf issuer가 `RETIRING`이면 만료일까지 기다리지 않고 30일 안에 기존 leaf private-key proof와 새 CSR로 renewal을 요청한다.

- Directory는 유효하고 미폐기된 retiring CA leaf proof를 갱신 인증으로 허용한다.
- 새 leaf는 current CA로 발급한다.
- 동일 identity 갱신은 기존 계약의 7일, identity 변경 갱신은 24시간 overlap을 유지한 뒤 이전 serial을 retiring CA CRL에 `Superseded`로 폐기한다.
- 조회 client는 service leaf issuer가 current 또는 retiring bundle CA인지 확인하고 해당 issuer 전용 CRL만 사용한다.
- offline·폐기·제거된 서비스는 관리자가 삭제 또는 serial 폐기로 정리해야 하며 자동으로 성공 처리하지 않는다.

### 4.4 Complete

Complete는 다음 조건을 모두 만족할 때만 허용한다.

- `ACTIVATED` 뒤 최소 30일이 지났다.
- 기존 CA ledger에 `CURRENT` 또는 `RETIRING`인 leaf가 0개다.
- 기존 CA가 발급한 아직 만료되지 않은 모든 leaf가 retiring CA CRL에 포함돼 있다.
- current CA Directory leaf와 HTTP.sys binding이 모든 Directory에서 검증됐다.
- standby/peer가 활성화 revision, current CA와 두 CRL high-water를 관찰했고 자기 Directory leaf도 current CA로 전환했다.
- 활성화 뒤 current revision 이상인 승인 backup이 있다.

Complete는 기존 CA로 terminal CRL을 만들고 `nextUpdate`를 기존 CA가 발급한 모든 leaf의 최대 `NotAfterUtc` 이상이면서 기존 CA `NotAfterUtc` 이하로 설정한다. 그 뒤 기존 CA certificate와 terminal CRL을 bounded retired archive로 옮기고 private key와 slot secret primary·backup·메모리 buffer를 폐기한다. complete commit 뒤에는 기존 CA로 CRL이나 certificate를 다시 서명할 수 없어야 한다.

완료 직후 current CA만 남은 새 revision은 `BACKUP_REQUIRED`다. 새 current-only backup이 성공하면 `READY`로 돌아간다. 외부 앱은 current CA로 인증된 `STABLE` bundle의 더 높은 revision을 확인한 뒤에만 retiring pin과 CRL cache를 제거한다.

## 5. 시간·용량·운영 정책

- 외부 앱과 Peer의 trust bundle refresh 주기는 Directory session마다이며, 장시간 실행 프로세스도 마지막 성공 후 24시간을 넘기지 않는다.
- Prepare부터 Activate까지 최소 30일, Activate부터 Complete까지 최소 30일이다. 이 최소값을 일반 UI나 설정으로 줄이지 않는다.
- CA 만료 730일 전부터 설정 UI와 보안 진단에 rotation 준비 경고를 표시한다.
- 계획된 rotation은 늦어도 current CA 만료 400일 전에 Prepare해야 한다. 그보다 늦으면 운영자에게 정상 dual-pin 완료를 보장할 수 없음을 표시하고 임의 단축하지 않는다.
- retired archive는 최대 16개 authority이며 전체 canonical 파일은 16 MiB 이하로 제한한다. 한계에서는 오래된 artifact를 자동 삭제하지 않고 Prepare를 거부해 명시적 schema·보존 정책 변경을 요구한다.
- 동시에 존재하는 private CA key는 current와 next 또는 current와 retiring의 최대 2개다.
- trust bundle에는 최대 2개 live authority만 허용한다.
- CA certificate 유효기간, 알고리즘과 key strength는 기존 site CA profile을 유지한다.

## 6. External API와 외부 앱 계약 변경계획

wire 계약은 구현 전에 [외부 API 명세](./04-api-01-external-application.md)와 `external.xsd`에서 먼저 확정한다. URL·media type·XML에 API version 필드를 추가하지 않고 기존 응답의 마지막 `Extensions` 확장점만 사용한다.

### 6.1 Trust bundle

`GET /pki/ca`의 기존 `TrustInfo`는 현재 Directory TLS issuer 정보를 계속 반환한다. `Extensions` 마지막에는 새 client가 반드시 처리하는 `TrustBundle`을 추가한다.

`TrustBundle`의 최소 필드는 다음과 같다.

- `SiteId`
- 단조 증가 `TrustRevision`
- `RotationId` — `STABLE`에서는 생략, rotation 중에는 필수
- `Phase` — `STABLE`, `PUBLISHED`, `ACTIVATED`
- `PublishedUtc`, `ActivationNotBeforeUtc`, `ActivatedUtc`, `RetirementNotBeforeUtc` — phase에 맞는 필드만 허용
- 1~2개 `Authority`
  - `Role`
  - `CaSerialNumber`
  - `CaCertificate`
  - `CaSpkiSha256`
  - issuer 고정 `CrlUri`
  - `NotBeforeUtc`, `NotAfterUtc`

client는 다음 순서로 갱신한다.

1. 현재 TLS leaf를 이미 저장한 current pin·CA로 검증한다. 최초 TOFU에서만 기존 제한적 TOFU 절차를 사용한다.
2. SiteId와 Directory DNS·IPv4 binding이 저장값과 같은지 확인한다.
3. bundle revision이 저장 revision보다 낮으면 rollback으로 거부한다. 같은 revision의 byte 불일치도 거부한다.
4. 각 CA profile·self-signature·serial·SPKI와 role 조합을 검증한다.
5. `STABLE -> PUBLISHED -> ACTIVATED -> STABLE` 또는 같은 상태의 revision 증가만 허용한다.
6. 전체 bundle과 issuer별 CRL을 제한 ACL 저장소에 한 번에 원자 교체한다.

`PUBLISHED` bundle은 기존 current TLS로 받은 경우에만 next pin을 추가한다. `ACTIVATED`를 처음 본 client가 next pin을 저장하지 않았으면 TLS 단계에서 실패해야 하며 응답을 이용해 새 CA를 자동 신뢰하지 않는다. 이 client는 관리자 trust reset 절차가 필요하다.

### 6.2 Issuer별 CRL

rotation 뒤 한 `/pki/crl`이 두 issuer의 CRL을 표현할 수 없으므로 다음 불변 endpoint를 추가한다.

```text
GET /pki/crl/{CaSerialNumber}
```

- path serial은 exact 32자리 uppercase hexadecimal이다.
- live current·next·retiring authority와 보존 중인 retired authority의 exact DER CRL만 반환한다.
- 기존 `GET /pki/crl`은 현재 Directory TLS issuer의 CRL alias로 유지하되 새 leaf의 CRL Distribution Point에는 사용하지 않는다.
- 새 Directory/service leaf의 두 absolute CDP URI는 DNS·IPv4 authority 각각에 issuer serial 경로를 사용한다.
- client는 leaf issuer CA에 선언된 URI와 bundle의 같은 CA `CrlUri`가 일치하는지 확인하고 다른 CA CRL로 fallback하지 않는다.
- 알 수 없거나 보존 종료된 issuer는 `404`; signature·number·시간 검증 실패는 fail closed다.

### 6.3 발급·갱신 영향

- 발급 응답의 `IssuerCertificate`와 `CrlUri`는 실제 서명 CA와 같아야 한다.
- `PUBLISHED`에서는 기존 CA, `ACTIVATED`와 완료 뒤에는 새 current CA만 발급한다.
- 외부 앱은 issuer가 `RETIRING`이 되는 즉시 rotation renewal을 예약하고 최대 30일 안에 완료한다.
- request ID exact replay가 retired issuer의 과거 인증서를 다시 정상 current 결과로 만들지 않도록 retired issuer replay 오류 계약을 API 명세에서 확정한다.
- 외부 앱의 local trust 저장은 current·next/retiring 두 CA, 각 CRL과 bundle revision을 하나의 원자 snapshot으로 관리한다.

## 7. Admin API·설정 UI·maintenance 계획

내부 wire 계약은 구현 전에 [내부 API 명세](./04-api-02-internal.md)와 `admin.xsd`에서 확정한다.

### 7.1 Admin API

다음 loopback Negotiate·local operator endpoint를 추가한다.

| method/path | 책임 |
|---|---|
| `GET /admin/ca/rotation` | phase, revision, current·other CA 요약, 최소 시각, old-leaf count, backup·peer·Directory leaf readiness 조회 |
| `POST /admin/ca/rotation/prepare` | active issuer에서 차기 CA 생성과 `PUBLISHED` commit |
| `POST /admin/ca/rotation/cancel` | Activate 전 차기 CA를 원자 폐기 |

HTTP endpoint는 전역 HTTP.sys binding 변경이나 old key 최종 폐기를 직접 수행하지 않는다. Activate와 Complete는 local Administrator 권한의 설치된 maintenance executable이 수행한다. 이는 메인 service account에 시스템 전체 HTTP.sys 설정 권한을 부여하지 않기 위한 경계다.

기존 `GET /admin/ca/status`, backup, ledger, serial revoke 응답에는 호환 가능한 마지막 확장점으로 rotation 요약과 issuer serial을 추가한다. 고객 CA mode, PFX path, customer trust 선택 필드는 추가하지 않는다.

### 7.2 설정 UI

설정 UI의 CA 영역에 다음을 표시한다.

- phase와 current·next/retiring CA serial·SPKI fingerprint
- Prepare/Activate/Complete 최소 가능 시각과 남은 시간
- current revision backup 여부
- retiring CA가 발급한 `CURRENT`·`RETIRING` leaf 수
- peer/standby bundle·Directory leaf 준비 상태
- 마지막 rotation 작업 결과와 안전한 일반 오류

Prepare·Cancel은 Admin API를 사용한다. Activate·Complete 버튼은 설치 경로의 exact maintenance executable을 `runas`로 실행하고 process command line·환경 변수·임시 파일에 암호나 key를 전달하지 않는다. backup 암호는 기존 Admin backup body를 통해서만 전달하고 로그·상태 화면에 남기지 않는다. tray context menu에는 rotation 명령을 넣지 않는다.

### 7.3 Maintenance executable

새 x64 .NET Framework 4.8 maintenance executable은 `activate-ca-rotation`과 `complete-ca-rotation` 두 고정 명령만 허용하며 `requireAdministrator` manifest를 사용한다. 임의 경로·thumbprint·serial·password 인수를 받지 않고 `%ProgramData%`의 canonical state와 installer-owned exact binding만 대상으로 한다.

- watchdog·main 상태를 snapshot하고 자동 재시작을 억제한다.
- main 요청 drain과 서비스 중지를 확인한다.
- certificate store, private-key ACL, HTTP.sys binding, 파일 journal before state를 검증한다.
- fixed rotation 작업을 실행하고 실패 시 exact before state로 rollback한다.
- 성공 시 원래 정책에 따라 서비스를 기동하고 installed-state/live-endpoint 검증을 실행할 수 있는 결과를 남긴다.
- rollback이나 재기동이 불완전하면 서비스를 중지 상태로 두고 repair를 요구한다.

## 8. 최초 정식 저장 schema v1 개정계획

[저장 schema v1](./03-development-01-storage-schema.md)은 구현 전에 rotation 형식으로 개정한다. 동적 파일명과 임의 journal target을 피하기 위해 CA material은 고정 `A`·`B` 두 slot을 번갈아 사용한다.

### 8.1 파일과 모델

| 파일 | 변경 책임 |
|---|---|
| `pki\state.xml` | phase, RotationId, TrustRevision, current slot, other slot role, 시각, slot별 serial·SPKI·CRL high-water·backup marker |
| `pki\ledger.xml` | 모든 발급 entry에 `IssuerCaSerialNumber`를 추가한 active issuer full ledger |
| `pki\peer-cache.xml` | standby가 관찰한 rotation bundle, 두 issuer CRL high-water와 ProductCode별 current leaf issuer |
| `pki\ca-a.der`, `pki\ca-b.der` | 고정 slot의 CA certificate |
| `pki\crl-a.der`, `pki\crl-b.der` | 고정 slot의 issuer별 CRL |
| `secrets\ca-a.key`, `secrets\ca-b.key` | active issuer에만 존재하는 slot별 DPAPI `LocalMachine` private key |
| `pki\retired-authorities.xml` | 최대 16개 retired CA certificate·terminal CRL·보존 high-water |

`STABLE`은 한 slot만 current이고 다른 slot key·CA·CRL이 없다. `PUBLISHED`는 current+next, `ACTIVATED`는 current+retiring 두 slot을 가진다. Complete는 retiring public artifact를 archive에 넣고 private slot을 폐기해 다시 한 slot만 남긴다.

ledger의 serial과 request ID 유일성은 issuer와 무관하게 전체 파일에서 유지한다. CRL entry는 `IssuerCaSerialNumber`별로 분리하고 한 issuer key로 다른 issuer CRL을 서명하지 않는다. current mapping과 exact replay 결과도 issuer serial을 포함한다.

### 8.2 Secret·backup

- 두 slot은 서로 다른 public DPAPI entropy label과 exact ACL을 사용한다.
- `.bak` secret을 만들지 않고 rollback 근거는 active journal의 DPAPI-protected before image뿐이다.
- rotation backup은 state, full ledger, 두 live slot의 CA·CRL·필요 private key와 retired archive를 인증·암호화한다.
- backup restore는 phase·slot·SiteId·issuer·revision·CRL·key 일관성을 전체 검증하고 일부 slot만 복원하지 않는다.
- standby 구성과 승격은 backup의 rotation phase를 그대로 보존한다. current와 next/retiring key가 필요한 phase에서 하나라도 빠진 backup은 거부한다.
- 기존 개발용 `DPAICAB1`과 단일 `ca.key` backup은 최초 릴리스 입력이 아니다. rotation 포함 canonical container를 새 magic으로 확정하고 구형 시험 데이터는 명시적으로 초기화한다.

### 8.3 Recovery journal

기존 9 target allowlist를 fixed dual-slot·retired archive target으로 확장한다. canonical 순서, image 이름과 operation별 exact target 집합은 저장 schema 문서에서 확정하며 동적 CA serial 경로나 slot 밖 파일을 허용하지 않는다.

fault injection은 다음 각 경계에 둔다.

- next key 생성·DPAPI 보호·CA/CRL 서명
- PREPARED 전과 각 slot target 교체 전후
- state·ledger·CRL·retired archive 교체
- journal COMMITTED 전후
- Directory leaf 설치·private-key ACL
- HTTP.sys binding 교체 전후
- old key discard와 terminal CRL archive
- 응답 또는 maintenance 성공 표시 직전

commit 전 장애는 전체 before state, commit 뒤 장애는 전체 after state로만 복구해야 하며 slot role과 실제 key·certificate·CRL이 섞인 상태를 게시하지 않는다.

## 9. Peer·Standby 계획

Peer `pki-state`는 rotation phase, TrustRevision, current·other CA certificate/pin/CRL/hash, issuer별 active certificate mapping과 Directory leaf issuer를 전달한다. HMAC·session·replay 계약은 유지하고 API version 필드를 추가하지 않는다.

peer가 구성되지 않은 standalone 설치에서는 peer readiness를 `NOT_REQUIRED`로 계산하며 가짜 ACK를 만들지 않는다. peer 관계가 `PairedDisabled` 또는 `Enabled`로 존재하면 아래 ACK·leaf·high-water 조건을 모두 만족하거나 운영자가 관계를 명시적으로 해제하기 전에는 Activate·Complete할 수 없다.

- TLS 검증은 저장된 dual-pin set 중 leaf issuer에 맞는 CA와 그 issuer CRL을 사용한다.
- `PUBLISHED`를 받은 standby는 두 public CA·CRL과 revision을 한 transaction으로 저장한다.
- active issuer는 standby가 같은 revision과 hash를 ACK하기 전 Activate할 수 없다.
- standby의 차기 Directory leaf staging은 인증된 backup을 maintenance process에서 열어 발급하고 CA key를 primary로 남기지 않는다.
- `ACTIVATED` 뒤 양쪽 Directory leaf issuer가 current CA인지 확인한다.
- Complete 전 standby가 current·retiring high-water와 current Directory leaf를 확인해야 한다.
- rotation 중 standby 승격은 동일 phase와 관찰 high-water 이상인 backup만 허용하며 두 key가 필요한 phase에서 단일-key backup을 거부한다.
- partition 중 자동 Activate·Complete·승격은 금지한다. 운영자는 관계 해제 또는 peer 복구 중 하나를 명시적으로 선택한다.

## 10. 인증서·CRL·원장 불변식

- 각 leaf에는 자기 issuer serial 전용 DNS·IPv4 absolute CRL URI 두 개를 넣는다.
- next CA는 `PUBLISHED`에서 service leaf를 발급하지 않는다.
- retiring CA는 `ACTIVATED`에서 새 leaf를 발급하지 않고 기존 serial의 CRL 갱신에만 사용한다.
- serial은 두 CA ledger 전체에서 중복될 수 없다.
- current leaf 한 건과 overlap `RETIRING` leaf의 issuer가 달라도 ProductCode current mapping은 하나다.
- CA별 CRL number는 독립적인 unsigned 64-bit high-water이며 감소·wrap하지 않는다.
- trust revision과 PKI revision은 rotation state·bundle·Peer cache·backup에서 감소하지 않는다.
- 같은 revision인데 bundle, CA, CRL 또는 mapping bytes가 다르면 collision으로 전체 채택을 중단한다.
- retired CA private key는 파일·backup·journal `.complete`·discard·메모리에 남지 않는다.
- retired archive의 terminal CRL signature와 `nextUpdate`가 검증되지 않으면 Complete하지 않는다.
- registration mode, CA backup, serial revoke와 rotation command는 하나의 mutation gate에서 서로 충돌 없이 직렬화한다.

## 11. 감사·보안 진단

rotation 작업은 일반 9개 파일 로그 이벤트에 섞지 않고 Windows Application Event Log source `DEEPAi.ServiceDirectory.Security`의 고정 이벤트로 기록한다.

- prepare·cancel·activate·complete 요청과 성공·실패
- backup 미완료, 최소 기간 미충족, old leaf 잔존과 peer readiness 거부
- bundle revision rollback·동일 revision collision
- dual-pin TLS·issuer별 CRL 검증 실패
- old key 폐기와 폐기 검증 실패
- maintenance rollback과 서비스 재기동 실패

이벤트에는 RotationId, phase, CA serial, revision과 안전한 error code만 기록한다. private key, backup 암호, CSR·certificate 원문, API key와 전체 request body는 기록하지 않는다. 반복 원격 실패는 기존 flood 억제를 적용한다.

## 12. 구현 단계와 변경 위치

### 12.1 단계 0 — 계약 선행 확정

1. 외부 API 명세와 `external.xsd`에 trust bundle, issuer별 CRL, client state transition과 rotation renewal을 확정한다.
2. 내부 API 명세와 `admin.xsd`·`peer.xsd`에 Admin status/prepare/cancel, Peer dual-CA state를 확정한다.
3. 저장 schema v1에 dual slot, ledger issuer, retired archive, backup container와 journal target을 확정한다.
4. 고객 CA 비지원과 planned/emergency rotation 경계를 모든 관련 문서에서 일치시킨다.

계약·XSD·golden XML과 오류 매핑이 확정되기 전 runtime 코드를 작성하지 않는다.

### 12.2 단계 1 — Domain·codec·저장 기반

- `ExternalProtocol`: trust bundle·issuer별 CRL DTO/strict codec
- `InternalProtocol`: Admin rotation·Peer PKI rotation DTO/strict codec
- `Infrastructure/Pki`: rotation state, fixed slot, issuer-aware ledger·CRL·retired archive
- `Infrastructure/Persistence`: 확장 journal target과 recovery
- tests: canonical XML/DER/backup golden bytes, unknown·partial·rollback 입력 거부

### 12.3 단계 2 — PKI operation

- Prepare/Cancel transaction
- issuer별 발급·갱신·폐기와 terminal CRL
- retired issuer replay 정책
- dual-slot encrypted backup/restore
- old key zeroization·discard 검증

### 12.4 단계 3 — External trust와 service migration

- `/pki/ca` trust bundle
- `/pki/crl/{CaSerialNumber}`와 legacy current alias
- issuer-specific CDP leaf 발급
- retiring leaf proof renewal과 30일 migration 상태
- 외부 시험 client의 atomic dual-pin/CRL cache reference 구현

### 12.5 단계 4 — Admin UI·maintenance

- Admin status/prepare/cancel handler와 rate/concurrency 경계
- CA 설정 화면과 readiness 표시
- x64 elevated maintenance executable
- Directory leaf staging, HTTP.sys binding switch, rollback·repair
- Activate/Complete 뒤 backup-required gate

### 12.6 단계 5 — Peer·Standby

- dual-CA Peer TLS validator
- rotation PKI state exchange·ACK
- standby dual-public cache와 Directory leaf staging
- rotation phase를 보존하는 backup 승격
- partition·stale backup·revision collision 차단

### 12.7 단계 6 — Installer·검증 도구

- 최초 설치를 dual-slot schema로 생성
- repair·upgrade에서 development-only 단일 CA 데이터를 자동 migration하지 않음
- installed-state와 live-endpoint 보고서에 phase·slot·두 binding trust·issuer별 CRL 추가
- uninstall 일반 보존과 purge의 두 key·archive 완전 삭제
- maintenance executable payload·ACL·UAC manifest 검증

### 12.8 단계 7 — 자동·현장 검증

자동 검증을 모두 통과한 뒤 [현장 검증 실행계획](./06-release-validation.md)에 따라 실제 active·standby와 외부 시험 앱으로 전체 전환을 수행한다.

## 13. 필수 테스트 행렬

### 13.1 상태·시간

- 모든 정상 전이와 금지 전이
- restart 뒤 phase·최소 시각 유지
- wall-clock 역행과 monotonic deadline
- 30일 경계 직전·정확한 경계·직후
- revision·CRL number overflow
- CA 만료 730일 경고와 400일 시작 한계

### 13.2 Trust bundle·client

- 최초 `STABLE`, `PUBLISHED`, `ACTIVATED`, 완료 `STABLE` golden XML
- revision rollback과 같은 revision 다른 bytes
- current TLS로 받은 next pin만 저장
- next pin을 놓친 client의 활성화 뒤 fail-closed
- 완료 bundle 전 old pin 자동 삭제 금지
- atomic cache write 실패 뒤 이전 bundle 유지
- DNS·IPv4 두 Directory identity에서 동일 bundle 검증

### 13.3 인증서·CRL

- 두 CA serial·SPKI·key 분리
- next·retiring CA의 금지된 leaf 발급
- old proof로 new CA renewal
- 두 issuer CRL number 독립 증가
- 다른 issuer CRL 대체·rollback·서명 오류 거부
- terminal CRL coverage와 old key 폐기 후 서명 불가
- retired issuer exact replay가 정상 current 인증서를 반환하지 않음

### 13.4 저장·복구·backup

- 모든 fixed target PREPARED rollback·COMMITTED roll-forward
- slot swap·key discard 각 경계 process termination
- dual-key backup round-trip과 부분·구형·변조 backup 거부
- standby promotion의 phase·high-water 보존
- active journal 없는 누락 slot·backup-only 자동 복원 금지
- retired archive 16개·16 MiB 경계

### 13.5 HTTP.sys·UI·운영

- Directory leaf staging과 DNS·IPv4 SAN
- Activate binding 전환 성공·실패·rollback
- main/watchdog running·stopped·pending 상태 조합
- maintenance UAC·고정 명령·임의 인수 거부
- UI countdown, readiness와 backup-required 표시
- 설치·repair·일반 제거 보존·명시적 purge

### 13.6 Peer·현장

- active/standby `PUBLISHED` ACK 뒤 Activate
- 각 Directory leaf 순차 전환 중 상호 TLS
- partition·stale peer·stale backup에서 전이 거부
- rotation 중 standby 승격과 이후 수렴
- 외부 앱 TOFU부터 dual-pin·서비스 renewal·Complete까지 end-to-end
- Windows Server 2016 이상과 지원 Milestone 교집합에서 TLS 1.2+, DPAPI, ACL, Event Log와 firewall 검증

## 14. 릴리스 완료 조건

다음 중 하나라도 남으면 인증서 기능과 최초 릴리스를 완료로 표시하지 않는다.

- API 명세·XSD와 실제 codec가 다름
- single CA development schema 또는 단일 `/pki/crl` CDP가 남음
- Prepare/Activate/Complete 장애에서 partial slot·binding 상태가 게시됨
- next pin을 current trust 없이 저장하거나 old pin을 인증된 Complete 전에 삭제함
- retiring CA가 신규 leaf를 발급함
- old leaf가 남아 있는데 Complete할 수 있음
- old private key가 backup·disk·journal에 남음
- standby가 rotation state를 보존하지 못함
- 실제 외부 시험 앱과 두 Directory 전환 증거가 없음

고객 CA 기능의 부재는 완료 실패가 아니다. 고객 CA는 이 제품의 명시적 비지원 범위다.
