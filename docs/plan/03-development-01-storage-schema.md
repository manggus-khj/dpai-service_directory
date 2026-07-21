# 최초 정식 저장 schema v1

```text
최초 작성일: 2026-07-20
최종 변경일: 2026-07-22
revision: 14
```

> 구현 상태: 최초 정식 dual-slot v1 계약을 아래와 같이 확정했다. 코드에는 A/B·retired journal target, slot별 DPAPI entropy, issuer-aware ledger, phase state codec, B-slot Prepare/Cancel transaction과 dual-slot backup/active repair를 반영했다. Activate/Complete maintenance, retired archive와 dual-CA Peer cache 전환은 아직 구현 중이다. 2026-07-21 빌드·663개 테스트 결과는 이 개정 전 단일 CA 기준선이며 현재 작업 트리의 검증 결과가 아니다.

## 1. 목적과 책임

이 문서는 서비스 디렉토리 최초 정식 배포의 로컬 저장 형식과 복구 transaction을 정의하는 단일 원본이다. 제품은 아직 배포되지 않았으므로 build 14 이하의 단일 `ServerAddress`·`pending.xml`·단일 CA 형식과 `DPAICAB1` backup은 호환 입력이 아니다. 아래 fixed A/B slot, issuer별 CRL과 `DPAICAB2`를 포함한 형식이 최초 정식 `SchemaVersion="1"` 계약이며 구형 개발 형식을 자동 migration하지 않는다.

- API XML namespace·요청·응답은 [외부 API](./04-api-01-external-application.md)와 [내부 API](./04-api-02-internal.md)가 소유한다.
- 데이터 소유권·mutation·sync 불변식은 [개발계획](./03-development.md)이 소유한다.
- 이 문서는 파일명, canonical byte 형식, 필드 순서·cardinality, 파일 조합, 교차 파일 불변식과 recovery journal target을 소유한다.
- 저장 XML에는 XSD를 배포하지 않는다. exact element 순서와 canonical 재직렬화, 파일 간 불변식은 strict codec과 고정 byte 테스트로 검증한다. API XSD를 저장 파일에 재사용하지 않는다.

## 2. 공통 canonical 규칙

모든 저장 XML은 다음 규칙을 공통 적용한다.

- BOM 없는 strict UTF-8이며 잘못된 UTF-8, NUL, DTD, 외부 entity, processing instruction, comment와 CDATA를 거부한다.
- XML declaration은 exact `<?xml version="1.0" encoding="utf-8"?>`, 줄바꿈은 CRLF, 들여쓰기는 공백 2개다. 마지막 root 닫기 뒤 CRLF 한 번으로 끝난다.
- XML namespace는 없고 root의 첫 속성은 exact `SchemaVersion="1"`이다. 문서별로 명시한 속성 외에는 허용하지 않는다.
- 요소와 속성은 이 문서의 순서를 지킨다. 알 수 없는·중복·순서가 다른 항목, 혼합 content와 단순 요소의 자식·속성은 거부한다.
- parser가 복원한 값으로 다시 직렬화한 bytes가 입력과 정확히 같아야 한다. XML escape도 serializer가 생성한 canonical 표현만 허용한다.
- XML depth는 최대 16, 각 XML과 journal image는 최대 16 MiB, `journal.xml`은 최대 16 KiB다. 빈 파일과 한계를 넘는 파일은 거부한다.
- GUID는 빈 값이 아닌 lowercase `D`, UTC는 `yyyy-MM-ddTHH:mm:ss.fffffffZ`, boolean은 lowercase `true`·`false`다.
- unsigned decimal은 `0` 또는 `1..UInt64.MaxValue`이며 `+`·공백·앞자리 0이 없다. 양수 전용 필드는 `1..UInt64.MaxValue`다. signed decimal은 `Int64.MinValue..Int64.MaxValue`의 `0` 또는 선택적 `-`와 0이 아닌 숫자로 쓰며 `+`·공백·앞자리 0·`-0`을 금지한다. port는 `1..65535` canonical decimal이다.
- certificate serial은 첫 바이트 `0x01..0x7F`, 나머지 15바이트 임의값인 16바이트 positive integer의 정확히 32자리 uppercase hex다. SHA-256은 32바이트 standard Base64의 정확히 44자 canonical padding 형식이며 whitespace·base64url을 허용하지 않는다.
- `Name`은 trim된 1~128 Unicode scalar·UTF-8 최대 512바이트이며 제어문자와 XML 1.0 금지 문자를 포함하지 않는다. ProductCode, DNS hostname/FQDN과 IPv4는 개발계획 §5.1의 canonical 값만 저장한다.
- 파일을 읽으면서 trim, 대소문자 변경, DNS 조회, source IP 보완과 enum 별칭 처리를 하지 않는다. 비정규 입력은 수정하지 않고 fail closed한다.

이 문서의 XML 조각에서 `base64-*`, `...`, `64-lowercase-hex`로 줄인 암호 자료는 구조 설명용이며 저장 가능한 canonical 입력이 아니다. 구현의 golden vector는 실제 DER·hash를 넣은 별도 test fixture로 고정한다.

## 3. 파일 집합과 소유권

데이터 루트는 exact `%ProgramData%\DEEPAi\ServiceDirectory\`다.

| 경로 | 형식·상한 | 존재 조건 | 소유 내용 |
|---|---|---|---|
| `directory.xml` | canonical XML, 16 MiB | 항상 필수 | 서비스 record·tombstone과 `LogicalClock` |
| `config.xml` | canonical XML, 16 MiB | 항상 필수 | Directory endpoint identity, instance, 로그와 Peer 운영 설정 |
| `pki\state.xml` | canonical XML, 16 MiB | 항상 필수 | SiteId, phase, fixed slot role, trust·PKI·issuer별 CRL high-water와 backup marker |
| `pki\ledger.xml` | canonical XML, 16 MiB | `Role=ACTIVE_ISSUER`에서 필수, `STANDBY`에서 금지 | active issuer의 등록·갱신 leaf, exact replay 근거와 폐기 이력 |
| `pki\peer-cache.xml` | canonical XML, 16 MiB | `Role=STANDBY`에서 필수, active issuer에서 금지 | 인증된 active issuer에서 받은 공개 current mapping과 PKI high-water |
| `pki\crl-a.der`, `pki\crl-b.der` | slot별 strict DER X.509 CRL, 각 16 MiB | live authority slot에 필수 | issuer별 signed CRL |
| `pki\ca-a.der`, `pki\ca-b.der` | slot별 strict DER X.509 certificate, 각 128 KiB | live authority slot에 필수 | site CA 공개 인증서 |
| `secrets\ca-a.key`, `secrets\ca-b.key` | slot별 DPAPI `LocalMachine`, 각 128 KiB | active issuer의 live authority slot에 필수, standby에서 금지 | slot별 site CA PKCS#8 private key 보호값 |
| `pki\retired-authorities.xml` | canonical XML, 16 MiB | 첫 Complete 뒤 필수, 이전에는 생략 | 최대 16개 retired CA와 terminal CRL 공개 artifact |
| `secrets\peer.dat` | DPAPI `LocalMachine`, 128 KiB | `Sync.State != Unpaired`일 때 필수 | pair root와 내구 pairing state |
| `backups\ca\site-ca-{SiteId}-{yyyyMMddTHHmmssfffZ}.dpca` | authenticated encrypted binary, 32 MiB | 승인 backup 뒤 1개 이상 | CA·metadata·ledger·CRL·private key 복구 묶음 |
| `journal\{TransactionId}\` | §9 recovery journal | transaction 중에만 | fixed target before·after image |

`pending.xml`은 최초 정식 파일 집합에 없다. 새 runtime이 `pending.xml`, build 12 이하 구형 XML shape 또는 구형 active journal을 발견하면 이를 빈 상태로 해석하거나 자동 삭제하지 않고 기동을 중단한다. 개발·테스트 장비는 서비스 중지와 명시적 확인 뒤 정확한 데이터 루트를 초기화하고 fresh install한다.

Directory HTTPS leaf와 private key는 XML·backup 파일에 저장하지 않는다. `LocalMachine\My` certificate store의 non-exportable private key와 exact HTTP.sys `ipport` binding이 소유한다. 서비스는 remote listener를 열기 전에 binding이 가리키는 leaf의 chain·CA·DNS/IP SAN·profile·validity와 private-key ACL을 `config.xml`과 `state.xml`의 `CURRENT` slot CA에 대해 검증한다. 누락·불일치는 repair 대상이며 CA backup restore는 Directory leaf private key를 복원하지 않고 repair가 새 leaf를 발급·binding한다.

`SiteId`, issuer 역할, rotation phase, slot, CA SPKI와 trust·PKI·issuer별 CRL high-water는 `pki\state.xml`만 소유한다. `config.xml`에 이를 복제하지 않는다. active issuer의 전체 원장과 standby가 인증된 Peer에서 관찰한 공개 PKI cache를 같은 파일로 혼합하지 않는다. build 14 이하 단일 `ca.der`·`crl.der`·`ca.key`와 `DPAICAB1`은 정식 v1 입력이 아니며 자동 migration하지 않는다.

## 4. `directory.xml`

### 4.1 문서 형태

```xml
<?xml version="1.0" encoding="utf-8"?>
<Directory SchemaVersion="1">
  <LogicalClock>42</LogicalClock>
  <Records>
    <Record>
      <Name>VMS Bridge</Name>
      <ProductCode>ABCD</ProductCode>
      <ServiceHostName>vms-bridge.example.local</ServiceHostName>
      <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
      <Port>21500</Port>
      <LastModifiedUtc>2026-07-20T01:02:03.0000000Z</LastModifiedUtc>
      <Deleted>false</Deleted>
      <LogicalVersion>42</LogicalVersion>
      <OriginInstanceId>87a26d80-fbce-4b4e-b843-413227f3e925</OriginInstanceId>
    </Record>
  </Records>
</Directory>
```

빈 저장소는 `LogicalClock=0`과 self-closing `<Records />`를 사용한다.

### 4.2 요소 계약

| 순서 | 요소 | 규칙 |
|---:|---|---|
| 1 | `LogicalClock` | `0..UInt64.MaxValue`. 모든 record `LogicalVersion` 이상 |
| 2 | `Records` | `Record` 0개 이상. ProductCode `Ordinal` 오름차순 |

`Record`는 다음 순서다.

| 순서 | 요소 | cardinality와 규칙 |
|---:|---|---|
| 1 | `Name` | 필수 canonical 표시 이름 |
| 2 | `ProductCode` | 필수 canonical 4바이트 ASCII, 문서 안에서 유일 |
| 3 | `ServiceHostName` | 필수 lowercase ASCII DNS hostname/FQDN |
| 4 | `ServiceIpv4Address` | 필수 canonical dotted-decimal IPv4 |
| 5 | `Port` | 필수 `1..65535` |
| 6 | `LastModifiedUtc` | 필수 canonical UTC. create 또는 마지막 service definition 변경 시각 |
| 7 | `Deleted` | 필수 `true` 또는 `false` |
| 8 | `DeletedUtc` | `Deleted=true`에서만 필수. `LastModifiedUtc`보다 이르지 않음 |
| 9 | `LogicalVersion` | 필수 양수. `LogicalClock` 이하 |
| 10 | `OriginInstanceId` | 필수 non-empty lowercase GUID |

`directory.xml`은 인증서 serial이나 상태를 중복 저장하지 않는다. active service와 인증서의 연결은 active issuer의 `ledger.xml` 또는 standby의 `peer-cache.xml`이 소유한다. 톰스톤은 마지막 승인 service definition을 보존한다. 삭제 transaction은 같은 ProductCode의 `CURRENT`와 `RETIRING` 인증서를 모두 `REVOKED/CESSATION_OF_OPERATION`으로 바꾸므로 삭제 record와 유효 인증서가 공존하지 않는다.

## 5. `config.xml`

### 5.1 문서 형태

```xml
<?xml version="1.0" encoding="utf-8"?>
<Config SchemaVersion="1">
  <ListenAddress>10.0.0.10</ListenAddress>
  <DirectoryHostName>management.example.local</DirectoryHostName>
  <DirectoryIpv4Address>10.0.0.10</DirectoryIpv4Address>
  <InstanceId>87a26d80-fbce-4b4e-b843-413227f3e925</InstanceId>
  <LastPeerKeyEpoch>0</LastPeerKeyEpoch>
  <LogRetentionDays>30</LogRetentionDays>
  <Sync>
    <State>Unpaired</State>
    <LastResult>NOT_RUN</LastResult>
    <LastPeerNotificationOperation>NONE</LastPeerNotificationOperation>
    <LastPeerNotificationResult>NOT_RUN</LastPeerNotificationResult>
  </Sync>
</Config>
```

### 5.2 루트 요소

| 순서 | 요소 | 규칙 |
|---:|---|---|
| 1 | `ListenAddress` | 필수 non-loopback Domain·Private canonical IPv4 |
| 2 | `DirectoryHostName` | 필수 local Management Server canonical lowercase DNS hostname/FQDN |
| 3 | `DirectoryIpv4Address` | 필수이며 `ListenAddress`와 `Ordinal`로 정확히 같음 |
| 4 | `InstanceId` | 필수 non-empty lowercase GUID. 최초 설치 뒤 불변 |
| 5 | `LastPeerKeyEpoch` | 필수 unsigned high-water. peer 폐기 뒤에도 감소·삭제 금지 |
| 6 | `LogRetentionDays` | 필수 `1..1095`, 최초값 `30` |
| 7 | `Sync` | 필수 내구 Peer 상태 |

ProductCode, 등록 모드, API key, SiteId, CA 역할·SPKI·rotation, private key와 nonce·SAS는 저장하지 않는다.

### 5.3 `Sync`

자식 순서는 다음과 같다. 조건에 맞지 않는 누락·추가 요소를 거부한다.

| 순서 | 요소 | cardinality와 조건 |
|---:|---|---|
| 1 | `State` | 필수 `Unpaired`, `PairedPendingCommit`, `PairedDisabled`, `Enabled` 중 하나 |
| 2 | `PeerEndpoint` | paired 상태에서 필수 canonical `https://{IPv4}:21000` |
| 3 | `PeerInstanceId` | paired 상태에서 필수 non-empty lowercase GUID |
| 4 | `KeyEpoch` | paired 상태에서 필수 양수이며 root `LastPeerKeyEpoch`와 같음 |
| 5 | `PairingId` | `PairedPendingCommit`에서만 필수 non-empty lowercase GUID |
| 6 | `CommitExpiresUtc` | `PairedPendingCommit`에서만 필수 canonical UTC |
| 7 | `LocalCommitConfirmed` | `PairedPendingCommit`에서만 필수 boolean |
| 8 | `RemoteCommitConfirmed` | `PairedPendingCommit`에서만 필수 boolean |
| 9 | `LastResult` | 필수 `NOT_RUN`, `OK` 또는 내부 API의 닫힌 오류 code 이름 |
| 10 | `LastSyncUtc` | `LastResult != NOT_RUN`에서 필수 canonical UTC |
| 11 | `ClockSkewSeconds` | 실제 handshake 편차를 관찰한 경우만 canonical signed Int64 |
| 12 | `LastPeerNotificationOperation` | 필수 `NONE`, `RELEASE`, `REVOKE` |
| 13 | `LastPeerNotificationResult` | 필수 `NOT_RUN`, `CONFIRMED`, `UNCONFIRMED`, `NOT_REQUIRED` |
| 14 | `LastPeerNotificationUtc` | operation/result가 초기값이 아닐 때 필수 canonical UTC |

`Unpaired`에서는 2~8이 모두 없고 `secrets\peer.dat`도 없어야 한다. 나머지 상태에서는 2~4와 `peer.dat`이 필수이며 config·복호화한 peer credential의 local/peer instance, endpoint, epoch와 pending commit 필드가 정확히 같아야 한다. process-local pairing window·ECDH private key·SAS·session·replay cache는 저장하지 않는다.

## 6. `pki\state.xml`

### 6.1 문서 형태

```xml
<?xml version="1.0" encoding="utf-8"?>
<CertificateAuthorityState SchemaVersion="1">
  <SiteId>89ae5f68-3146-480a-b3fa-8699a64d4f40</SiteId>
  <IssuerInstanceId>87a26d80-fbce-4b4e-b843-413227f3e925</IssuerInstanceId>
  <Role>ACTIVE_ISSUER</Role>
  <RotationPhase>PUBLISHED</RotationPhase>
  <TrustRevision>2</TrustRevision>
  <PkiRevision>43</PkiRevision>
  <CurrentSlot>A</CurrentSlot>
  <RotationId>2b3fded9-fbe8-4d69-b201-746eb922f767</RotationId>
  <PublishedUtc>2026-07-22T03:00:00.0000000Z</PublishedUtc>
  <ActivationNotBeforeUtc>2026-08-21T03:00:00.0000000Z</ActivationNotBeforeUtc>
  <Authority Slot="A">
    <Role>CURRENT</Role>
    <CaSerialNumber>01A4B5C6D7E8F90123456789ABCDEE01</CaSerialNumber>
    <CaSpkiSha256>base64-sha256-value</CaSpkiSha256>
    <NotBeforeUtc>2026-07-20T00:00:00.0000000Z</NotBeforeUtc>
    <NotAfterUtc>2046-07-20T00:00:00.0000000Z</NotAfterUtc>
    <CrlNumber>19</CrlNumber>
  </Authority>
  <Authority Slot="B">
    <Role>NEXT</Role>
    <CaSerialNumber>01A4B5C6D7E8F90123456789ABCDEE02</CaSerialNumber>
    <CaSpkiSha256>base64-sha256-value</CaSpkiSha256>
    <NotBeforeUtc>2026-07-22T03:00:00.0000000Z</NotBeforeUtc>
    <NotAfterUtc>2046-07-22T03:00:00.0000000Z</NotAfterUtc>
    <CrlNumber>1</CrlNumber>
  </Authority>
</CertificateAuthorityState>
```

phase별 공통 root 자식 순서는 `SiteId`, `IssuerInstanceId`, `Role`, `RotationPhase`, `TrustRevision`, `PkiRevision`, `CurrentSlot`, phase별 선택 시각, `Authority` 1~2개, 선택 backup marker 순서다.

| 순서 | 요소 | 규칙 |
|---:|---|---|
| 1 | `SiteId` | 필수 non-empty lowercase GUID. CA 수명 동안 불변 |
| 2 | `IssuerInstanceId` | 필수 non-empty lowercase GUID. 현재 active issuer |
| 3 | `Role` | 필수 `ACTIVE_ISSUER` 또는 `STANDBY` |
| 4 | `RotationPhase` | 필수 `STABLE`, `PUBLISHED`, `ACTIVATED` |
| 5 | `TrustRevision` | 필수 양수 high-water. bundle bytes가 바뀌는 commit에서 증가하고 감소·wrap 금지 |
| 6 | `PkiRevision` | 필수 양수 full ledger·issuer 상태 high-water |
| 7 | `CurrentSlot` | 필수 `A` 또는 `B`, `CURRENT` authority의 `Slot`과 같음 |
| 8 | `RotationId` | rotation 중 필수 non-empty lowercase GUID, `STABLE`에서 금지 |
| 9 | `PublishedUtc` | rotation 중 필수 canonical UTC |
| 10 | `ActivationNotBeforeUtc` | rotation 중 필수이며 PublishedUtc보다 최소 30일 뒤 |
| 11 | `ActivatedUtc` | `ACTIVATED`에서만 필수이며 ActivationNotBeforeUtc 이상 |
| 12 | `RetirementNotBeforeUtc` | `ACTIVATED`에서만 필수이며 ActivatedUtc보다 최소 30일 뒤 |
| 13 | `Authority` | `STABLE` 1개, 그 외 2개. Slot `A`·`B` canonical 오름차순 |
| 14 | `LastBackupTrustRevision` | 현재 또는 과거 승인 backup marker. `LastBackupUtc`와 함께만 존재하고 감소 금지 |
| 15 | `LastBackupUtc` | marker backup 생성 시각. 단독 존재 금지, 감소 금지 |

`Authority`는 exact `Slot` attribute 하나와 `Role`, `CaSerialNumber`, `CaSpkiSha256`, `NotBeforeUtc`, `NotAfterUtc`, `CrlNumber` 자식을 이 순서로 가진다. `Role`은 `CURRENT`, `NEXT`, `RETIRING`만 저장한다. `RETIRED`는 live slot이 아니라 `retired-authorities.xml`에만 있다. serial·SPKI는 두 live authority 사이에 달라야 하며 각 slot CA·CRL·key와 교차 검증한다.

- `STABLE`: `CURRENT` 하나, 다른 slot 세 파일과 key backup이 모두 없어야 한다.
- `PUBLISHED`: `CURRENT`와 `NEXT`; current slot은 기존 issuer이고 next는 leaf 발급 금지다.
- `ACTIVATED`: 새 `CURRENT`와 기존 `RETIRING`; retiring key는 그 issuer CRL 갱신에만 사용한다.

`ACTIVE_ISSUER`이면 `IssuerInstanceId=config.InstanceId`이고 phase가 허용한 issuer만 발급·폐기한다. `STANDBY`는 발급·폐기·등록 모드 open과 CA backup 생성을 거부하며 private slot key가 없어야 한다. `PkiRevision`은 ledger 또는 issuer state가 바뀔 때 증가하고 CA별 `CrlNumber`는 그 issuer CRL publish 때만 증가한다. `LastBackupTrustRevision >= TrustRevision`인 인증 backup이 있어야 해당 revision backup readiness가 `READY`다. 필수 조합·CA·ledger/cache·CRL 검증 실패를 상태 API로 축소하지 않고 기동을 실패시킨다.

## 7. `pki\ledger.xml`

### 7.1 문서 형태

```xml
<?xml version="1.0" encoding="utf-8"?>
<CertificateLedger SchemaVersion="1" PkiRevision="2">
  <Certificate>
    <SerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</SerialNumber>
    <IssuerCaSerialNumber>01A4B5C6D7E8F90123456789ABCDEE01</IssuerCaSerialNumber>
    <ProductCode>ABCD</ProductCode>
    <IssuanceRequestId>7f35b4b8-854d-4ca1-90bc-da196772f49f</IssuanceRequestId>
    <IssuanceKind>REGISTRATION</IssuanceKind>
    <Name>VMS Bridge</Name>
    <ServiceHostName>vms-bridge.example.local</ServiceHostName>
    <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
    <Port>21500</Port>
    <CsrSha256>base64-sha256-value</CsrSha256>
    <RequestPayloadSha256>base64-sha256-value</RequestPayloadSha256>
    <SubjectPublicKeyInfoSha256>base64-sha256-value</SubjectPublicKeyInfoSha256>
    <LeafCertificate>base64-der-certificate</LeafCertificate>
    <IssuedUtc>2026-07-20T01:02:03.0000000Z</IssuedUtc>
    <NotBeforeUtc>2026-07-20T00:57:03.0000000Z</NotBeforeUtc>
    <NotAfterUtc>2027-07-20T01:02:03.0000000Z</NotAfterUtc>
    <Status>CURRENT</Status>
  </Certificate>
</CertificateLedger>
```

빈 ledger도 root `PkiRevision` 속성을 가지며 자식 없이 self-closing한다. root 속성 순서는 `SchemaVersion`, `PkiRevision`이다. CRL number는 issuer별이므로 `state.xml`의 각 `Authority`만 소유한다. `Certificate`는 serial `Ordinal` 오름차순이며 serial과 `IssuanceRequestId`는 issuer와 무관하게 문서 전체에서 각각 유일하다. 이 파일은 active issuer만 보유하며 standby는 full ledger를 Peer protocol로 추정·합성하지 않는다.

### 7.2 `Certificate` 요소

| 순서 | 요소 | 규칙 |
|---:|---|---|
| 1 | `SerialNumber` | 필수 unique 32자리 uppercase hex |
| 2 | `IssuerCaSerialNumber` | 필수 live 또는 retired authority serial. leaf signature issuer와 같고 변경 금지 |
| 3 | `ProductCode` | 필수 canonical ProductCode |
| 4 | `IssuanceRequestId` | 필수 unique non-empty lowercase GUID |
| 5 | `IssuanceKind` | 필수 `REGISTRATION` 또는 `RENEWAL` |
| 6 | `Name` | 발급 성공 시점의 canonical service 이름 |
| 7 | `ServiceHostName` | 발급 leaf DNS SAN과 같은 canonical 값 |
| 8 | `ServiceIpv4Address` | 발급 leaf IP SAN과 같은 canonical 값 |
| 9 | `Port` | 발급 성공 시점 service port |
| 10 | `CsrSha256` | canonical CSR DER SHA-256 |
| 11 | `RequestPayloadSha256` | §7.3 stable semantic payload SHA-256 |
| 12 | `SubjectPublicKeyInfoSha256` | leaf SPKI DER SHA-256 |
| 13 | `LeafCertificate` | standard Base64 single DER leaf, DER 최대 16 KiB |
| 14 | `IssuedUtc` | 발급 commit 기준 UTC |
| 15 | `NotBeforeUtc` | leaf notBefore와 같음 |
| 16 | `NotAfterUtc` | leaf notAfter와 같음 |
| 17 | `Status` | `CURRENT`, `RETIRING`, `REVOKED` |
| 18 | `ScheduledRevocationUtc` | `RETIRING`에서 필수. RETIRING을 거쳐 REVOKED가 된 항목에는 보존 가능 |
| 19 | `RevokedUtc` | `REVOKED`에서만 필수 |
| 20 | `RevocationReason` | `REVOKED`에서만 필수. 내부 API의 허용 enum |

`LeafCertificate`는 exact replay 응답을 재구성하기 위한 공개 artifact다. 별도 동적 certificate 파일을 만들지 않는다. load 때 DER을 strict parse하고 CA 서명, serial, SPKI hash, ProductCode 연계, SAN·EKU·KU·validity와 저장 필드 일치를 모두 확인한다. `LeafCertificateSha256`은 DER에서 계산할 수 있으므로 중복 저장하지 않고 Admin/Peer 응답 시 계산한다.

ProductCode별 `CURRENT`는 정확히 0개 또는 1개다. 연속 renewal이나 `CURRENT`의 명시적 serial 폐기 뒤에도 아직 예약 폐기 시각이 오지 않은 이전 leaf가 있을 수 있으므로 `RETIRING`은 0개 이상을 허용하며 `CURRENT` 존재를 요구하지 않는다. 삭제 tombstone에는 CURRENT·RETIRING이 없어야 한다. `REVOKED`는 `RevokedUtc`와 reason을 바꾸거나 제거할 수 없다.

ledger history는 시간만으로 삭제·압축하지 않는다. 새 발급을 반영한 canonical ledger가 16 MiB를 넘으면 serial을 예약·서명하거나 등록 모드를 claim하기 전에 `1004 LIMIT_EXCEEDED`로 전체 요청을 거부한다. 저장 상한을 늘리거나 archive를 도입하려면 정식 배포 뒤의 명시적 다음 schema와 migration을 먼저 정의한다.

### 7.3 idempotency payload hash

`RequestPayloadSha256`은 raw XML, 일일 API 키, renewal timestamp·nonce·proof signature를 hash하지 않는다. 서버가 검증·정규화한 stable semantic 값으로 다음 bytes를 만들고 SHA-256한다.

stream은 아래 ASCII label raw bytes로 시작하며 label에는 길이를 붙이지 않는다. 그 뒤 나열한 각 값은 고정 길이 값도 포함해 모두 `UInt32 big-endian byte length || raw bytes`로 결합한다. 문자열은 strict UTF-8, ProductCode·hostname·IPv4는 canonical ASCII, port는 2바이트 unsigned big-endian, serial은 raw 16바이트, hash는 raw 32바이트다.

- registration: ASCII label `DPAI-SD-REGISTRATION-PAYLOAD-V1`, ProductCode, Name, ServiceHostName, ServiceIpv4Address, Port, CSR SHA-256
- renewal: ASCII label `DPAI-SD-RENEWAL-PAYLOAD-V1`, ProductCode, CurrentSerialNumber raw 16바이트, Name, ServiceHostName, ServiceIpv4Address, Port, CSR SHA-256

idempotency key는 `IssuanceKind + IssuanceRequestId`가 아니라 문서 전체에서 유일한 `IssuanceRequestId` 하나다. 같은 request ID·kind·CSR hash·payload hash면 저장한 service 값과 leaf DER로 exact 결과를 반환한다. 같은 ID의 kind·CSR·payload 중 하나라도 다르면 충돌이며 새 serial을 발급하지 않는다.

### 7.4 `pki\peer-cache.xml`

이 파일은 `STANDBY`만 사용하며 [내부 API §5.9](./04-api-02-internal.md#59-post-apisyncpki-state)의 인증된 응답에서 받은 공개 정보만 저장한다. full ledger, request ID·hash, CSR, leaf DER과 CA private key를 만들거나 추정하지 않는다.

```xml
<?xml version="1.0" encoding="utf-8"?>
<PeerPkiCache SchemaVersion="1">
  <IssuerInstanceId>87a26d80-fbce-4b4e-b843-413227f3e925</IssuerInstanceId>
  <TrustRevision>7</TrustRevision>
  <PkiRevision>42</PkiRevision>
  <DirectoryLeafIssuerCaSerialNumber>11A4B5C6D7E8F90123456789ABCDEF01</DirectoryLeafIssuerCaSerialNumber>
  <Authorities>
    <Authority Slot="A">
      <Role>RETIRING</Role>
      <CaSerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</CaSerialNumber>
      <CrlNumber>18</CrlNumber>
      <CrlSha256>base64-sha256-value</CrlSha256>
    </Authority>
    <Authority Slot="B">
      <Role>CURRENT</Role>
      <CaSerialNumber>11A4B5C6D7E8F90123456789ABCDEF01</CaSerialNumber>
      <CrlNumber>3</CrlNumber>
      <CrlSha256>base64-sha256-value</CrlSha256>
    </Authority>
  </Authorities>
  <ActiveCertificates>
    <Certificate>
      <ProductCode>ABCD</ProductCode>
      <SerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</SerialNumber>
      <IssuerCaSerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</IssuerCaSerialNumber>
      <LeafSha256>base64-sha256-value</LeafSha256>
      <NotAfterUtc>2027-07-20T01:02:03.0000000Z</NotAfterUtc>
    </Certificate>
  </ActiveCertificates>
</PeerPkiCache>
```

root 자식은 `IssuerInstanceId`, `TrustRevision`, `PkiRevision`, `DirectoryLeafIssuerCaSerialNumber`, `Authorities`, `ActiveCertificates` 순서다. 앞의 세 값과 authority slot·role·serial·CRL number는 `state.xml`과 같고 `DirectoryLeafIssuerCaSerialNumber`는 phase의 `CURRENT` authority serial이어야 한다. `Authority`는 slot A/B 순서이며 각 자식은 `Role`, `CaSerialNumber`, `CrlNumber`, `CrlSha256` 순서다. hash는 같은 slot의 exact `crl-a.der` 또는 `crl-b.der` SHA-256이다. `STABLE`에는 `CURRENT` 하나, `PUBLISHED`에는 `CURRENT`와 `NEXT`, `ACTIVATED`에는 `RETIRING`과 `CURRENT` 둘이 있어야 한다.

`Certificate`는 ProductCode `Ordinal` 오름차순이며 ProductCode와 serial이 각각 유일하고 최대 1,000개다. 자식은 `ProductCode`, `SerialNumber`, `IssuerCaSerialNumber`, `LeafSha256`, `NotAfterUtc` 순서이며 issuer serial은 같은 cache의 authority 중 하나여야 한다. wire 응답의 canonical 값과 정확히 같아야 하고 `RETIRED` leaf는 이 active mapping에 넣지 않는다.

같은 `TrustRevision`·`PkiRevision`의 cache bytes, CA bytes 또는 CRL hash가 다르면 split-brain으로 실패한다. 더 낮은 revision이나 authority별 CRL number를 채택하지 않는다. `STANDBY`의 Admin full ledger 조회·backup·serial 폐기는 이 cache로 흉내 내지 않고 `409`·`1002 CONFLICT`로 거부한다. 현재 소스의 단일 authority Peer cache codec은 이 dual-CA 형식으로 교체되기 전까지 `PUBLISHED`·`ACTIVATED`를 수신하거나 제공하지 않는다.

## 8. DER·secret·backup 형식

### 8.1 CA와 CRL

- `pki\ca-a.der`와 `pki\ca-b.der`는 각 slot의 trailing bytes 없는 단일 strict DER self-signed RSA 3072/SHA-256 CA certificate다. BasicConstraints CA=true·critical, KeyUsage keyCertSign+cRLSign·critical, 최대 20년과 계획의 DN/profile을 검증한다.
- `pki\crl-a.der`와 `pki\crl-b.der`는 같은 slot CA가 서명한 strict DER X.509 CRL이다. issuer·signature와 CRLNumber는 `state.xml`의 해당 `Authority`와 같아야 한다. active issuer에서는 CRL entry 집합이 ledger에서 같은 `IssuerCaSerialNumber`를 갖는 모든 `REVOKED` serial·시각·reason과 정확히 같고, standby에서는 cache의 issuer별 hash·number와 CA signature를 검증한다.
- 각 slot CA·CRL과 ledger leaf DER은 canonical 재인코딩과 signature 검증에 성공해야 하며 parse 성공만으로 신뢰하지 않는다.

### 8.2 DPAPI secret

- `secrets\ca-a.key`와 `secrets\ca-b.key`는 각각 최대 64 KiB PKCS#8을 public entropy label `DEEPAi.ServiceDirectory.ca-a.key.v1`, `DEEPAi.ServiceDirectory.ca-b.key.v1`과 DPAPI `LocalMachine`으로 따로 보호한 최대 128 KiB blob이다. 복호화한 RSA key는 같은 slot CA DER과 일치해야 하며 두 key를 한 DPAPI blob으로 합치지 않는다.
- `secrets\peer.dat` plaintext는 ASCII magic `DPAISDPC`, big-endian `UInt16=1` 뒤에 모든 값을 `UInt32BE length || bytes`로 둔다. 필드 순서는 ASCII state, ASCII local role, PairingId, LocalInstanceId, PeerInstanceId, LocalEndpoint, PeerEndpoint, KeyEpoch, raw 32-byte transcript hash, raw 32-byte pair root, CommitExpiresUtc, local confirmed, remote confirmed, local evidence, remote evidence다. GUID·UTC·boolean·정수는 §2 canonical 형식이며 text는 strict ASCII 최대 128바이트다.
- 각 commit evidence는 비어 있으면 길이 0이고 confirmed와 존재 여부가 같아야 한다. 존재하면 내부 bytes도 `request MAC 32바이트`, ASCII HTTP status, raw response body, `response MAC 32바이트`를 각각 같은 UInt32BE length-prefix로 직렬화한다. plaintext 전체는 최대 64 KiB이고 trailing bytes를 거부하며 public entropy label `DEEPAi.ServiceDirectory.peer.dat.v1`과 DPAPI `LocalMachine`으로 보호한 blob은 최대 128 KiB다.
- 모든 secret은 상속 차단 exact ACL로 메인 서비스 SID·`SYSTEM`·로컬 `Administrators`만 허용한다. plaintext는 메모리 사용 직후 지우고 API·로그·journal image에 쓰지 않는다.
- `ca-a.key.bak`, `ca-b.key.bak`과 `peer.dat.bak`은 어떤 상태에서도 금지한다. secret rollback 근거는 active journal의 DPAPI 보호 before image뿐이다.

### 8.3 CA backup

CA backup은 active issuer에서만 `.dpca` v1 authenticated encrypted binary 계약을 사용한다. outer bytes는 ASCII magic `DPAICAE2`, `Int32BE=600000`, salt 16바이트, IV 16바이트, `Int32BE ciphertext length`, AES-256-CBC PKCS#7 ciphertext, HMAC-SHA256 32바이트 순서다. PBKDF2-HMAC-SHA256은 password와 salt로 64바이트를 만들고 앞 32바이트를 encryption key, 뒤 32바이트를 MAC key로 사용하며 MAC은 앞선 outer bytes 전체를 대상으로 한다. 구형 `DPAICAE1`은 거부한다.

복호화 plaintext는 ASCII magic `DPAICAB2`, 다음 8개 `Int32BE` component 길이, 이어서 같은 순서의 bytes를 둔다.

1. exact `pki\state.xml`
2. exact `pki\ledger.xml`
3. `pki\ca-a.der`
4. `pki\crl-a.der`
5. 복호화한 slot A CA PKCS#8
6. `pki\ca-b.der`
7. `pki\crl-b.der`
8. 복호화한 slot B CA PKCS#8

1~5는 현재 구현의 최초 provisioning slot A 때문에 항상 양수 길이다. 6~8은 `STABLE`에서 모두 `0`, `PUBLISHED`·`ACTIVATED`에서 모두 양수여야 하며 일부만 0인 조합을 거부한다. `state.xml`의 phase·slot 존재 조건과 각 CA·CRL·key가 정확히 일치해야 한다. encrypted container 전체는 최대 32 MiB이고 trailing bytes를 거부한다. `peer-cache.xml`, `directory.xml`, `config.xml`, `peer.dat`, retired archive, Directory HTTPS leaf private key와 등록 모드는 포함하지 않는다. 구형 `DPAICAB1`은 거부한다.

active issuer repair는 container MAC, 모든 component, slot별 CA key/certificate, state·ledger·issuer별 CRL high-water와 SiteId를 검증한 뒤 §9의 고정 target transaction으로 복원한다. 복원 뒤 config의 Directory identity로 새 Directory leaf를 발급·binding하는 repair를 별도로 수행한다. 현재 standby 구성·승격은 `STABLE` single-authority backup만 허용하며 dual-slot backup 지원은 dual-CA Peer cache와 함께 완료한다.

## 9. recovery journal v1

### 9.1 target allowlist

최초 정식 v1에서 `Pending` target을 제거하고 다음 13개만 허용한다. 순서도 canonical entry 순서다.

| 순서 | Target | 실제 대상 | before / after image |
|---:|---|---|---|
| 1 | `Directory` | `directory.xml` | `directory.before.bin` / `directory.after.bin` |
| 2 | `Config` | `config.xml` | `config.before.bin` / `config.after.bin` |
| 3 | `PeerSecret` | `secrets\peer.dat` | `peer.before.bin` / `peer.after.bin` |
| 4 | `PkiMetadata` | `pki\state.xml` | `pki-state.before.bin` / `pki-state.after.bin` |
| 5 | `CertificateLedger` | `pki\ledger.xml` | `ledger.before.bin` / `ledger.after.bin` |
| 6 | `PeerPkiCache` | `pki\peer-cache.xml` | `peer-pki.before.bin` / `peer-pki.after.bin` |
| 7 | `CertificateRevocationListA` | `pki\crl-a.der` | `crl-a.before.bin` / `crl-a.after.bin` |
| 8 | `CaCertificateA` | `pki\ca-a.der` | `ca-a.before.bin` / `ca-a.after.bin` |
| 9 | `CaPrivateKeyA` | `secrets\ca-a.key` | `ca-a-key.before.bin` / `ca-a-key.after.bin` |
| 10 | `CertificateRevocationListB` | `pki\crl-b.der` | `crl-b.before.bin` / `crl-b.after.bin` |
| 11 | `CaCertificateB` | `pki\ca-b.der` | `ca-b.before.bin` / `ca-b.after.bin` |
| 12 | `CaPrivateKeyB` | `secrets\ca-b.key` | `ca-b-key.before.bin` / `ca-b-key.after.bin` |
| 13 | `RetiredAuthorities` | `pki\retired-authorities.xml` | `retired-authorities.before.bin` / `retired-authorities.after.bin` |

각 target은 표에 정의한 고정 primary·image 이름과 target별 고정 backup·discard 이름만 사용한다. 임의 경로, 동적 certificate 파일, target key, `..`, absolute path와 대체 image 이름을 허용하지 않는다.

### 9.2 `journal.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<RecoveryJournal SchemaVersion="1" TransactionId="c8a1b518-060d-47fd-b6b2-705aa3ef7920" Phase="PREPARED">
  <Entry Target="Directory" BeforeExists="true" AfterExists="true" BeforeSha256="64-lowercase-hex" AfterSha256="64-lowercase-hex" />
</RecoveryJournal>
```

- root 속성 순서는 `SchemaVersion`, `TransactionId`, `Phase`이고 phase는 `PREPARED` 또는 `COMMITTED`다.
- `Entry`는 1~13개, target 중복 없이 §9.1 순서다. 속성 순서는 `Target`, `BeforeExists`, `AfterExists`, 존재하는 쪽의 `BeforeSha256`, `AfterSha256`이다.
- SHA-256은 exact image bytes의 64자리 lowercase hex다. `Exists=false`인 쪽은 image와 hash가 모두 없어야 한다.
- `{TransactionId}.preparing`, active `{TransactionId}`, `{TransactionId}.complete` 이름·원자 rename·write-through·discard와 PREPARED rollback/COMMITTED roll-forward는 개발계획 §5.4 절차를 그대로 적용한다.

### 9.3 operation별 target 집합

| operation | 필수 target 집합 |
|---|---|
| fresh provisioning | `Directory`, `Config`, `PkiMetadata`, `CertificateLedger`, `CertificateRevocationListA`, `CaCertificateA`, `CaPrivateKeyA` |
| 신규 registration | `Directory`, `PkiMetadata`, `CertificateLedger` |
| 기존 ProductCode 재등록 | `Directory`, `PkiMetadata`, `CertificateLedger`, 현재 issuer의 `CertificateRevocationList{Slot}` |
| renewal 발급 | service definition이 바뀌면 `Directory`를 포함하고 항상 `PkiMetadata`, `CertificateLedger` |
| scheduled retirement 폐기 | `PkiMetadata`, `CertificateLedger`, 대상 issuer의 `CertificateRevocationList{Slot}` |
| 서비스 삭제 | `Directory`, `PkiMetadata`, `CertificateLedger`, 폐기 대상 issuer별 `CertificateRevocationList{Slot}` |
| Admin serial 폐기 | `PkiMetadata`, `CertificateLedger`, 대상 issuer의 `CertificateRevocationList{Slot}` |
| rotation Prepare | `PkiMetadata`, `CertificateLedger`, free slot의 `CertificateRevocationList{Slot}`, `CaCertificate{Slot}`, `CaPrivateKey{Slot}` 생성 |
| rotation Cancel | `PkiMetadata`, `CertificateLedger`, next slot의 `CertificateRevocationList{Slot}`, `CaCertificate{Slot}`, `CaPrivateKey{Slot}` 삭제 |
| Peer pairing·release·revoke | 변경 내용에 따라 `Config`, `PeerSecret` |
| 인증된 Peer service snapshot 채택 | `Directory` |
| standby의 인증된 Peer PKI 채택 | `PkiMetadata`, `PeerPkiCache`, `CertificateRevocationList`를 항상 한 transaction으로 교체 |
| backup 승인 marker | `PkiMetadata` |
| active issuer CA repair restore | `PkiMetadata`, `CertificateLedger`, phase에 존재하는 A/B slot별 CRL·CA·key 전체 |
| standby 구성 | `PkiMetadata`, `PeerPkiCache`, `CertificateRevocationList`, `CaCertificate`; 기존 `CertificateLedger`·`CaPrivateKey`가 있으면 삭제 entry 포함 |
| standby 명시적 승격 | `PkiMetadata`, `CertificateLedger`, `PeerPkiCache` 삭제, `CaPrivateKey`; backup과 현재 bytes가 다르면 `CertificateRevocationList`, `CaCertificate` 포함 |
| ListenAddress·Directory identity repair | `Config`; certificate store·HTTP.sys·방화벽은 installer rollback snapshot으로 별도 관리 |

target을 바꾸지 않는 operation은 해당 entry를 넣지 않는다. 모든 after image와 교차 파일 검증을 끝내기 전 실제 target을 바꾸지 않는다. Windows certificate store·HTTP.sys·SCM·방화벽은 journal target이 아니며 installer가 exact before state를 별도로 snapshot하고 실패 시 rollback한다. 파일 transaction이 commit됐지만 OS state 재구성이 실패하면 손상된 이전 bytes로 자동 rollback하지 않고 서비스를 중지한 채 repair를 요구한다.

## 10. 교차 파일 불변식

기동과 commit 전 다음을 모두 검증한다.

1. role별 required primary가 모두 있고 금지된 `pending.xml`, 반대 role의 ledger/cache·CA key, secret `.bak`, 구형 active journal과 reparse point가 없다. active issuer는 공통 4개 `directory.xml`·`config.xml`·`state.xml`·`ledger.xml`과 phase에 존재하는 각 live slot의 CA·CRL·key 3개가 필수이고 peer cache는 금지한다. standby는 공통 `directory.xml`·`config.xml`·`state.xml`·`peer-cache.xml`과 각 live slot의 공개 CA·CRL이 필수이고 ledger·CA key는 금지한다.
2. `config.DirectoryIpv4Address == config.ListenAddress`이고 실제 local Domain·Private IPv4 할당과 같다.
3. active issuer는 `state.PkiRevision == ledger.PkiRevision`이고 각 live authority의 `CrlNumber`가 같은 slot CRL의 CRLNumber extension과 같다. standby는 `state.TrustRevision == peer-cache.TrustRevision`, `state.PkiRevision == peer-cache.PkiRevision`이고 authority별 slot·role·serial·CRL number와 exact CRL hash가 cache와 같다.
4. 각 live slot의 CA DER serial·SPKI·validity가 state와 같다. active issuer에서만 같은 slot의 DPAPI CA key가 존재하고 CA public key와 일치한다. 두 live CA의 serial과 SPKI는 서로 달라야 한다.
5. active issuer의 active directory record에는 같은 ProductCode의 ledger `CURRENT`가 0개 또는 1개 있다. 존재하면 Name·hostname·IPv4·port가 record와 같아야 한다. 0개는 같은 current service identity로 발급됐다가 명시적으로 폐기된 ledger 이력이 있을 때만 허용하며 외부 갱신 대신 등록 모드 재등록을 요구한다.
6. active issuer의 ledger `CURRENT`마다 active directory record가 정확히 한 건 있다. tombstone ProductCode에는 `CURRENT`·`RETIRING`이 없고 모든 기존 비폐기 인증서는 삭제 transaction에서 `REVOKED`가 된다. standby의 service snapshot과 PKI cache는 서로 다른 인증 endpoint·high-water로 갱신되므로 ProductCode 집합의 일시 차이를 파일 손상으로 취급하지 않는다.
7. active issuer의 ledger leaf DER은 serial·SPKI·SAN·profile·validity·CA signature와 저장 field가 모두 같고 serial·request ID가 중복되지 않는다. standby는 cache를 full ledger로 해석하지 않는다.
8. active issuer의 각 issuer CRL entry는 ledger에서 같은 `IssuerCaSerialNumber`를 가진 `REVOKED` 집합과 정확히 같다. standby는 인증된 cache의 issuer별 hash·number와 CRL signature·issuer를 검증한다. 어느 role도 낮은 revision·number·revocation time으로 회귀하지 않는다.
9. `LogicalClock`은 모든 record version 이상이며 local `InstanceId`, origin, peer epoch와 PKI high-water가 wrap·감소하지 않는다.
10. `Sync.State`와 `peer.dat` 존재, 복호화한 local/peer identity·endpoint·epoch·commit state가 정확히 일치한다.
11. active issuer만 발급·폐기·backup·등록 모드 open이 가능하고, `LastBackupTrustRevision < TrustRevision`이면 `BACKUP_REQUIRED` 제한 상태다. standby는 공개 cache만으로 이 작업을 수행하지 않는다.
12. HTTP.sys binding leaf는 config Directory DNS·IPv4 SAN, phase의 `CURRENT` site CA와 private key를 만족한다. 실패하면 loopback 진단 경계 외 remote listener를 열지 않는다.

하나라도 실패하면 일부 파일을 메모리에 게시하거나 standalone `.bak`을 승격하지 않는다.

## 11. 초기화·복구·제거

- 진짜 최초 설치는 데이터 루트·제품 등록·서비스·필수 primary가 모두 없는 경우에만 fresh provisioning transaction을 만든다.
- empty `directory.xml`은 `LogicalClock=0`, empty ledger와 slot A initial CRL은 `TrustRevision=1`, `PkiRevision=1`, `CrlNumber=1`, config는 `Unpaired`, CA state는 `ACTIVE_ISSUER`·`STABLE`·`CurrentSlot=A`와 backup marker 부재로 시작한다. slot B와 retired archive는 만들지 않는다.
- fresh transaction commit 뒤 Directory leaf를 Windows certificate store에 import하고 exact HTTPS binding·ACL·방화벽을 구성한다. 실패하면 installer가 OS state와 fresh files를 모두 rollback한다.
- standby 구성은 운영자가 제공한 인증된 동일-site CA backup을 maintenance 경계에서 검증하고 phase의 `CURRENT` CA key를 메모리에서만 사용해 해당 서버의 새 Directory leaf를 발급한 뒤, local state는 `STANDBY`·backup issuer identity와 rotation phase를 그대로 기록한다. full ledger와 CA key primary는 만들지 않고 backup 파일 자체만 제한 ACL로 보존하며 공개 current mapping과 slot별 CA·CRL을 `peer-cache.xml`·`ca-a/b.der`·`crl-a/b.der`에 초기화한다. 현재 구현은 dual-CA Peer cache 완료 전까지 `STABLE` backup만 허용한다.
- standby 승격은 서비스가 중지된 repair에서만 수행한다. 선택 backup의 full ledger·CRL high-water가 standby에서 마지막으로 관찰한 값 이상이어야 하며, 복원 뒤 `IssuerInstanceId=config.InstanceId`, `Role=ACTIVE_ISSUER`, `PkiRevision+1`을 state·ledger에 함께 기록하고 `peer-cache.xml`을 같은 transaction에서 제거한다. `PkiRevision` 최댓값이거나 최신성을 증명하지 못하면 승격하지 않는다.
- 재설치·repair는 required state를 빈 파일로 재생성하지 않는다. active journal이 없는데 primary가 누락·손상됐으면 `.bak`이 유효해 보여도 fail closed한다.
- 일반 제거는 서비스·프로그램·binding·방화벽을 제거하고 이 문서의 데이터·secret·backup을 보존한다. 명시적 전체 삭제만 exact 데이터 루트를 삭제한다.
- 미배포 build 12 이하 개발 데이터 초기화는 첫 정식 배포 전 개발 절차에만 적용하며 일반 제거·운영 복구의 자동 삭제 근거로 사용하지 않는다.

## 12. 구현·검증 순서

1. target `ServiceDefinition`·record·configuration·ledger·peer cache 모델을 확정하고 실제 DER·hash를 포함한 별도 canonical fixture를 golden vector로 만든다.
2. `directory.xml`, `config.xml`, `state.xml`, `ledger.xml`, `peer-cache.xml` strict codec과 canonical round-trip·16 MiB·depth·XXE·unknown field 테스트를 구현한다.
3. `Pending` target·serializer·store를 제거하고 `PeerPkiCache`, A/B slot과 retired archive target을 포함한 journal 순서·1~13 entry 검증을 갱신한다.
4. ledger에 leaf DER과 stable payload hash를 추가하고 exact replay·certificate profile·cross-file validator를 구현한다.
5. operation별 transaction과 모든 target 교체 전후 fault-injection을 구현한다.
6. installer fresh provisioning·명시적 개발 reset·repair/restore·일반 제거 보존을 연결한다.
7. 사용자가 build check를 요청하면 locked restore·x64 build·전체 테스트를 실행하고, package 요청 시에만 설치 EXE를 만든다.
