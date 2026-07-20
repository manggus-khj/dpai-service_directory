# 서비스 디렉토리 외부 애플리케이션 API 명세

```text
최초 작성일: 2026-07-17
최종 변경일: 2026-07-19
revision: 1
```

> 문서 상태: 인증서 기반 목표 wire 계약 확정
> 구현 상태: PKI core 1차 소스만 부분 구현. 현재 wire·XSD·listener는 평문 HTTP·CSR 없는 승인 대기 계약이며 이 명세와 일치하지 않음
> 대상 독자: Milestone Management Server에 연결한 뒤 Directory에서 서비스를 조회하거나 자기 서비스를 등록하는 애플리케이션 개발자
> 배포 범위: 사내 연동 개발자와 승인된 운영 담당자

이 문서는 외부 애플리케이션이 Directory의 위치와 인증서를 신뢰하고, 서비스를 조회하고, 자기 서버 인증서를 발급·갱신하고, 조회한 대상 서비스의 인증서를 검증하는 전체 연동 계약이다. endpoint 형식만 구현하고 이 문서의 최초 신뢰·pin·CSR·CRL 절차를 생략하면 호환 구현이 아니다.

현재 저장소의 `DEEPAi.ServiceDirectory.ExternalProtocol`은 서버 내부 구현체이며 재배포 가능한 client SDK가 아니다. 외부 앱은 이 문서와 향후 갱신할 [`xsd/external.xsd`](./04-api/external.xsd)를 단일 원본으로 사용한다. 현재 XSD와 코드는 이 목표 계약으로 아직 변경하지 않았다.

## 1. 역할과 계약 범위

### 1.1 호출자 역할

| 역할 | 사용 기능 |
|---|---|
| 조회 클라이언트 | Directory trust bootstrap, health, 자기 ProductCode 서비스 조회, CRL 수신, 조회한 서버 인증서 검증 |
| 등록 서버 앱 | 조회 기능과 함께 등록 모드 중 CSR 제출·서버 인증서 수령, 기존 인증서 기반 자동 갱신 |
| 와치독 | loopback health만 사용. 원격 TOFU·서비스 등록·PKI 계약은 사용하지 않음 |

### 1.2 endpoint

| 메서드와 경로 | 인증·신뢰 | 목적 |
|---|---|---|
| `GET /pki/ca` | 최초에는 제한적 TOFU, 이후 저장한 CA pin | SiteId, 사이트 CA 공개 인증서, SPKI pin과 CRL 위치 수신 |
| `GET /pki/crl` | 서명된 CRL 자체 검증 | 사이트 CA의 현재 DER CRL 수신 |
| `GET /api/health` | HTTPS + 일일 API 키 | Directory 응답 가능 여부 확인 |
| `GET /api/services?productCode={code}` | HTTPS + 일일 API 키 | 활성 서비스 한 건 조회 |
| `POST /api/registration` | HTTPS + 일일 API 키 + 열린 등록 모드 + CSR | 첫 유효 요청의 즉시 서비스 등록·인증서 발급 또는 정확한 성공 재시도 |
| `POST /api/certificates/renew` | HTTPS + 일일 API 키 + 현재 인증서 개인키 proof | 같은 ProductCode의 인증서 자동 갱신과 저장 hostname·IPv4 pair 변경 재발급 |

외부 앱은 `/admin/*`, `/api/sync/*`, Named Pipe와 Directory의 XML·CA private key·ledger 파일에 접근하면 안 된다.

## 2. Directory 위치와 최초 신뢰

이 명세에는 서로 바꿔 쓸 수 없는 두 network identity가 있다.

| identity | 값의 출처 | 사용처 | 금지 |
|---|---|---|---|
| Directory identity | 먼저 연결한 Milestone Management Server와 같은 서버의 `DirectoryHostName`·`DirectoryIpv4Address` | Directory base URL, Directory TLS leaf 검증, CA·pin trust 저장 | 서비스 등록 record나 등록 서비스 인증서 SAN에 복사 금지 |
| Registered service identity | 등록 외부 앱이 자기 서버에서 선택·영속화한 `ServiceHostName`·`ServiceIpv4Address` | `POST /api/registration`, 디렉토리 조회 record, 등록 서비스 TLS leaf SAN | Directory/Milestone 주소, 요청 source IP와 DNS 역조회 값 사용 금지 |

Directory CA certificate는 trust anchor이므로 endpoint hostname·IP SAN을 갖지 않는다. CA가 발급하는 Directory TLS leaf는 Directory identity의 DNS+IPv4를, 등록 서비스 leaf는 해당 service identity의 DNS+IPv4만 사용한다.

### 2.1 주소 생성

외부 앱은 Directory 주소를 별도 입력받거나 연결정보 파일에서 읽지 않는다. 성공한 Milestone Management Server 연결에서 Directory가 함께 설치된 서버의 DNS hostname/FQDN과 실제 remote IPv4를 얻어 각각 `DirectoryHostName`, `DirectoryIpv4Address`로 저장한다. 이 값은 Directory 접속에만 사용하며 등록할 외부 서비스의 주소가 아니다. IPv6 주소는 지원하지 않는다.

```text
Milestone hostname:     management.example.local
Milestone IPv4:         10.0.0.10
Directory base by DNS:  https://management.example.local:21000
Directory base by IPv4: https://10.0.0.10:21000
```

- Management Server hostname은 Milestone 연결 설정 또는 인증된 session metadata에서 얻고, IPv4는 그 session이 실제로 연결된 remote endpoint에서 얻는다.
- DNS 역조회, Directory 요청의 출발지 주소, redirect의 다른 host와 별도 자동 검색 결과로 두 값을 만들거나 교체하지 않는다.
- 두 값은 반드시 같은 Management Server를 가리켜야 한다. hostname의 정상 이름 해석 결과에 저장 IPv4가 없으면 Directory 연결을 시작하지 않고 Milestone 연결 설정 오류로 처리한다.
- Directory installer는 로컬 Management Server hostname/FQDN 한 개와 선택한 `ListenAddress` IPv4 한 개를 Directory leaf SAN에 각각 `dNSName`, `iPAddress`로 반드시 포함한다.
- 외부 앱은 현장 설정에 따라 DNS base 또는 IPv4 base를 선택할 수 있다. 인증서 검증 실패 뒤 다른 base로 자동 fallback하지 않는다.
- Directory installer는 ProductCode, Directory 주소, CA 파일, CA pin 또는 외부 앱 인증서를 입력받지 않는다.
- Directory leaf에 저장한 hostname과 IPv4 SAN이 둘 다 없거나 어느 하나라도 값이 다르면 연결은 실패한다.

### 2.2 최초 TOFU 절차

해당 Milestone server identity에 저장된 Directory trust가 없을 때만 아래 절차를 한 번 수행한다. 이는 임의 인증서를 허용하는 일반 validation bypass가 아니다.

1. exact Directory base에 TLS 1.2 이상으로 연결하고 서버가 제시한 leaf와 issuer 후보를 수집한다.
2. leaf SAN에 저장한 exact `DirectoryHostName`의 `dNSName`과 `DirectoryIpv4Address`의 `iPAddress`가 모두 있는지 확인한다. CN fallback은 사용하지 않는다.
3. `GET /pki/ca`에서 SiteId와 CA 공개 인증서를 받는다.
4. leaf가 그 CA로 서명됐는지 exclusive trust anchor 의미로 chain을 검증한다. OS trust store의 다른 root로 조용히 fallback하지 않는다.
5. CA의 `BasicConstraints`가 `CA=TRUE, pathLen=0`, Key Usage가 `keyCertSign`·`cRLSign`인지 확인한다.
6. leaf의 EKU가 `Server Authentication`, Key Usage가 `digitalSignature`인지 확인한다.
7. leaf와 CA의 유효기간, SHA-256 이상 signature, RSA 2048 이상 또는 ECDSA P-256 이상을 확인한다.
8. 응답의 pin과 직접 계산한 `Base64(SHA-256(DER SubjectPublicKeyInfo(CA)))`가 같은지 constant-time 의미로 비교한다.
9. Milestone server identity, DirectoryHostName, DirectoryIpv4Address, SiteId, CA DER과 SPKI pin을 제한된 로컬 저장소에 원자적으로 저장한다.
10. 저장이 성공한 뒤에만 health·조회·등록 요청을 보낸다.

최초 접속 시 활성 중간자 공격을 완전히 제거하지 못한다는 TOFU 잔여 위험은 폐쇄망 운영 조건으로 수용한다. 외부 앱은 최초 CA fingerprint를 진단 UI에 표시할 수 있지만 사용자에게 연결정보 파일이나 pin 입력을 요구하지 않는다.

### 2.3 이후 검증과 trust reset

두 번째 연결부터 다음을 모두 만족해야 한다.

- chain root가 저장한 CA 인증서와 byte 또는 canonical certificate identity로 일치
- 계산한 CA SPKI pin이 저장 pin과 일치
- 저장한 DirectoryHostName·DirectoryIpv4Address가 leaf SAN의 DNS·IP 항목과 모두 일치
- leaf·CA 유효기간과 certificate profile 충족
- 유효한 CRL 정책 충족

같은 주소라도 CA pin이 다르면 연결을 차단한다. 새 CA를 자동 저장하거나 `ServerCertificateValidationCallback => true` 같은 우회를 사용하지 않는다.

- 같은 CA key로 CA 인증서만 갱신하면 SPKI pin은 유지한다.
- 계획된 CA key rotation은 현재 유효한 TLS 연결에서 전달된 다음 CA와 pin을 dual-pin 기간에만 수락한다.
- 전환을 놓쳤거나 예고 없이 pin이 바뀌면 외부 앱의 명시적 `Directory 신뢰 초기화`가 필요하다.
- trust reset은 해당 Milestone server identity의 CA DER, pin, SiteId binding과 CRL cache를 함께 삭제한다.
- trust reset은 로컬 관리자 권한과 명시적 확인이 필요하며 해당 Directory에 대한 다음 연결을 새로운 TOFU로 만든다는 경고를 표시한다.
- 외부 앱 제거는 저장한 CA DER·pin·SiteId binding·CRL cache를 완전히 삭제한다. repair·upgrade는 정상 trust를 보존한다.
- 일반 네트워크 오류나 인증서 만료를 pin 자동 삭제 조건으로 사용하지 않는다.

## 3. External 일일 API 키

### 3.1 헤더와 생성

```http
X-DPAI-API-Key: {44-character Base64 value}
```

헤더는 정확히 한 번만 보낸다. 누락·중복·공백 포함·44자가 아닌 값과 strict Base64가 아닌 값은 거부한다.

| 항목 | 규칙 |
|---|---|
| ProductCode | trim 후 `ToUpperInvariant()`로 정규화한 `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII |
| LocalDate | 호출 호스트 시스템 로컬 `yyyyMMdd`, 8바이트 ASCII |
| PlainText | `ASCII(ProductCode + LocalDate)`, 정확히 12바이트 |
| AES Key | `SHA-256(ASCII(LocalDate))`, 32바이트 |
| 암호 | AES-256-CBC, 128비트 block, PKCS#7 padding |
| IV | 요청마다 CSPRNG로 새로 생성한 16바이트 |
| TokenBytes | `IV || CipherText`, 32바이트 |
| 헤더 값 | `Base64(TokenBytes)`, padding 포함 정확히 44자 |

상호운용 테스트 벡터:

```text
ProductCode       = ABCD
LocalDate         = 20260717
PlainText         = ABCD20260717
SHA256(Date)      = 0EBA83E757B79452F6D44FCFE3E9E3AC0CD301001B5211B03C7962D1CF0D3AC1
IV                = 000102030405060708090A0B0C0D0E0F
CipherText        = 7ECC55FE5D61E89FE12B3A3A31800BE8
X-DPAI-API-Key    = AAECAwQFBgcICQoLDA0OD37MVf5dYeif4Ss6OjGAC+g=
```

고정 IV는 테스트에만 사용한다. 운영 요청마다 새 IV를 생성한다.

### 3.2 서버 검증

서버는 요청당 한 번 캡처한 시스템 로컬 시각으로 다음을 수행한다.

1. 헤더가 정확히 하나이고 strict 44자 Base64인지 확인한다.
2. 32바이트로 decode해 IV 16바이트와 ciphertext 16바이트로 나눈다.
3. 서버 현재 로컬 날짜의 SHA-256을 AES key로 사용해 복호화한다.
4. 결과가 4바이트 ProductCode와 같은 8바이트 로컬 날짜인지 확인한다.
5. 조회·등록·갱신 payload의 ProductCode가 복원 ProductCode와 같은지 확인한다.

서버 현재 날짜만 허용하고 전날·다음 날 유예를 두지 않는다. 자정 경계의 `401`은 새 날짜와 새 IV로 한 번만 재시도한다. API 키 원문과 실패 단계는 응답·로그에 기록하지 않는다.

### 3.3 보안 성격과 발급 예외

일일 API 키는 별도 secret이 없고 알고리즘과 ProductCode를 아는 주체가 생성할 수 있다. 호출 인스턴스 identity, request body 무결성, replay 방지를 제공하지 않는다. 외부 앱 개발자가 구현해야 하는 내부 계약이며 구현 비공개를 영구적인 비밀로 간주하지 않는다.

그럼에도 이 제품은 다음 결합을 초기 인증서 발급 승인으로 사용하는 프로젝트 예외를 적용한다.

- 관리자가 로컬 Negotiate·운영자 인가를 거쳐 전역 등록 모드를 직접 연다.
- 등록 모드는 1시간·첫 유효 요청 한 건으로 제한되고 성공 즉시 닫힌다.
- 요청은 Directory HTTPS·저장 pin·일일 키·ProductCode·CSR 검증을 모두 통과한다.
- 발급·재등록·폐기는 감사되고 잘못된 등록 삭제는 CRL 폐기까지 수행한다.

## 4. 등록 모드 전제

외부 앱은 등록 모드를 원격으로 열거나 상태를 상세 조회할 수 없다. 관리자가 Directory 설정 UI의 `등록 서비스` 화면에서 시작한다.

- ProductCode를 입력하거나 선택하지 않는 전역 창이다.
- 고정 1시간이며 첫 번째 완전히 유효한 등록 요청 한 건만 처리한다.
- 성공, 만료, 수동 종료, Directory service 종료·재시작 또는 불확실한 발급 장애에서 닫힌다.
- 잘못된 일일 키·XML·CSR·SAN 요청은 창을 소비하지 않지만 rate limit과 보안 감사를 적용한다.
- 이미 처리 중이면 다른 신규 요청은 `1002 CONFLICT`다.
- 닫힌 상태의 신규 요청은 `1005 REGISTRATION_MODE_CLOSED`다.
- 성공 응답 유실 뒤 정확히 같은 요청의 idempotent replay는 닫힌 상태에서도 허용한다.

설치하는 사람은 ProductCode, Directory 주소, CA 인증서, pin, 등록 token 또는 PFX를 입력하지 않는다. 서버 앱은 자기 제품에 컴파일된 ProductCode와 이미 성립한 Management Server session endpoint를 사용한다.

## 5. 공통 HTTP·XML 계약

### 5.1 transport

- 원격 protocol: TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS·HTTP/1.1. 프로토콜과 cipher suite를 애플리케이션 코드에서 고정하지 않으며 배포 OS에서 SSLv3·TLS 1.0·1.1 비활성화를 통합 검증
- remote base: `https://{DirectoryHostName}:21000` 또는 `https://{DirectoryIpv4Address}:21000`; 둘은 같은 Directory identity에 결합되고 IPv4만 지원
- 평문 HTTP remote listener, HTTP→HTTPS redirect, 인증서 검증 fallback 금지
- API XML: `application/xml; charset=utf-8`
- CRL: `application/pkix-crl`, DER
- URL·media type·XML에 API version을 두지 않음
- External XML namespace: `urn:deepai:service-directory:external`
- DTD·외부 entity 금지, unknown request 요소·속성·중복·mixed content 거부
- 응답 확장은 마지막 선택 요소 `Extensions` 아래에서만 허용
- CORS·브라우저 호출·정적 파일·디렉터리 목록 미지원
- payload 시각은 UTC ISO 8601, 일일 키 날짜만 시스템 로컬 날짜

`GET /pki/ca`와 `GET /pki/crl`은 최초 trust와 폐기 확인에 필요하므로 일일 키를 요구하지 않고 원격 IP별 제한을 적용한다. `/api/*`는 모두 일일 키를 요구한다.

### 5.2 크기와 제한

| 대상 | 최대 raw body |
|---|---:|
| GET 전체 | 0바이트 |
| 일반 External XML | 16 KiB |
| 등록·갱신 CSR 포함 XML | 64 KiB |
| `/pki/ca` 응답 | 32 KiB |
| `/pki/crl` 응답 | 4 MiB |

- raw query는 optional `?` 포함 2,048바이트 ASCII, field 최대 16개다.
- XML 최대 깊이는 root를 1로 계산해 16이다.
- 요청 body 압축은 지원하지 않는다. 비어 있지 않은 `Content-Encoding`은 `415`다.
- External 전체 동시 실행은 32개다.
- health는 ProductCode+IP당 분당 30회, burst 5다.
- service 조회는 ProductCode당 분당 12회와 IP당 분당 60회, 각 capacity 1이다.
- registration은 ProductCode당 분당 3회·burst 2와 IP당 분당 20회다.
- renewal은 ProductCode당 분당 3회·burst 2와 IP당 분당 20회다.
- `/pki/ca`는 IP당 분당 10회·burst 2, `/pki/crl`은 IP당 분당 30회·burst 5다.

시간 기반 `429`에는 정수 초 `Retry-After`를 보낸다. concurrency나 state cap처럼 해제 시각을 계산할 수 없으면 생략한다.

### 5.3 응답 envelope과 오류

XML endpoint는 다음 envelope을 사용한다.

```xml
<Response xmlns="urn:deepai:service-directory:external">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
</Response>
```

| Code | 이름 | 의미 |
|---:|---|---|
| 0 | `OK` | 성공 |
| 1000 | `BAD_REQUEST` | query·XML·필드 형식 오류 |
| 1001 | `NOT_FOUND` | 활성 서비스·인증서 ledger 항목 없음 |
| 1002 | `CONFLICT` | request ID 불일치, 등록 claim 경합 등 상태 충돌 |
| 1003 | `INVALID_API_KEY` | 일일 API 키 누락·중복·검증 실패 |
| 1004 | `LIMIT_EXCEEDED` | 요청 속도·동시 실행·크기 외 운영 제한 초과 |
| 1005 | `REGISTRATION_MODE_CLOSED` | 신규 등록에 필요한 1회성 등록 모드가 닫힘 |
| 1006 | `CERTIFICATE_REQUEST_INVALID` | CSR 서명·키·SAN·profile 검증 실패 |
| 1007 | `CERTIFICATE_NOT_RENEWABLE` | 현재 인증서 만료·폐기·ProductCode 변경 또는 현재 private key proof로 갱신 불가 |
| 3000 | `INTERNAL` | 내부 오류. 상세 예외 비노출 |

| HTTP | envelope | 사용처 |
|---:|---|---|
| 200 | `0` | 정상 성공과 idempotent 성공 재시도 |
| 400 | `1000`, `1006` | 입력·CSR 검증 실패 |
| 401 | `1003` 또는 body 없음 | 일일 키 실패는 safe envelope, 인증서 proof 위조는 body 없음 |
| 403 | body 없음 | listener local endpoint·신뢰 경계 실패 |
| 404 | `1001` 또는 body 없음 | 리소스 없음 또는 미정의 경로 |
| 409 | `1002`, `1005`, `1007` | 등록 모드·idempotency·갱신 상태 충돌 |
| 413 | body 없음 | raw body 제한 초과 |
| 415 | body 없음 | 지원하지 않는 content type·encoding |
| 429 | `1004` | rate·concurrency 제한 |
| 500 | `3000` | 안전하게 일반화한 내부 오류 |

`/pki/crl`은 성공 시 XML envelope 없이 DER bytes를 반환한다. 신뢰가 성립하기 전 조기 TLS 실패에는 HTTP 응답이 없을 수 있다.

## 6. 데이터 계약

### 6.1 서비스 DTO

```xml
<Service xmlns="urn:deepai:service-directory:external">
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServiceHostName>vms-bridge.example.local</ServiceHostName>
  <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
  <Port>21500</Port>
  <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
</Service>
```

`Deleted`, `LogicalVersion`, `OriginInstanceId`, 인증서 serial·ledger와 CA private 상태는 노출하지 않는다. `ServiceHostName`과 `ServiceIpv4Address`는 분리할 수 없는 한 서비스 identity이며 둘 다 같은 HTTPS listener와 인증서를 가리킨다.

### 6.2 공통 입력

| 필드 | 규칙 |
|---|---|
| `Name` | trim 후 1~128 Unicode scalar, UTF-8 최대 512바이트, 제어문자·XML 금지 문자 거부 |
| `ProductCode` | trim·대문자 정규화한 `[A-Z0-9]{4}` 정확히 4바이트 ASCII |
| `ServiceHostName` | 필수 1개. trim·소문자 정규화한 최대 253자 ASCII DNS hostname 또는 FQDN. scheme·path·query·port·wildcard·마지막 점 금지 |
| `ServiceIpv4Address` | 필수 1개. 서비스가 실제 bind하는 canonical dotted-decimal IPv4. loopback·`0.0.0.0`·APIPA·multicast·limited broadcast `255.255.255.255`·선행 0 금지. subnet mask가 필요한 directed broadcast 판정은 앱의 실제 NIC·listener 검증 책임 |
| `Port` | `1..65535` |
| request ID | canonical 소문자 `D` GUID, 클라이언트 CSPRNG 기반 UUID 사용 |
| CSR | PKCS#10 DER의 Base64. PEM marker·공백·복수 CSR 금지 |

외부 앱은 최초 등록 전에 `ServiceHostName`과 `ServiceIpv4Address`를 사용자가 선택하게 하고 자기 제한 ACL 설정에 한 쌍으로 영속화한다. ProductCode는 앱 내장값이므로 이 선택 UI에 포함하지 않는다. 앱은 요청 직전에 다음을 확인한다.

- 선택 IPv4가 자기 활성 service NIC에 할당돼 있고 해당 listener가 사용할 주소인지
- 자기 호스트의 이름 해석 결과에 선택 IPv4가 포함되는지
- 저장값과 CSR SAN의 DNS·IP 값이 정확히 같은지

Directory는 이 두 필드를 요청의 TCP source IP, `RemoteEndPoint`, DNS 역조회 또는 관찰한 routing 주소로 생성·수정·보완하지 않는다. Directory 관점의 DNS 결과도 소유권 증명이나 자동 교정에 사용하지 않는다.

## 7. PKI endpoint

### 7.1 `GET /pki/ca`

요청 body와 query는 없다. 성공 응답:

```xml
<Response xmlns="urn:deepai:service-directory:external">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <TrustInfo>
    <SiteId>3d8ff138-4e9a-4e52-b108-e3af248b1787</SiteId>
    <CaCertificate>MIIC...</CaCertificate>
    <CaSpkiSha256>base64-sha256-value</CaSpkiSha256>
    <CrlUri>/pki/crl</CrlUri>
  </TrustInfo>
</Response>
```

- `CaCertificate`는 단일 DER X.509 CA certificate의 whitespace 없는 Base64다.
- `CaSpkiSha256`는 SHA-256 32바이트의 padding 포함 Base64다.
- `CrlUri`는 API 응답에서만 사용하는 exact `/pki/crl` relative path다. 호출자는 현재 SAN·pin 검증을 통과한 Directory base URL에 이 path를 결합하며 응답 값을 인증서의 CRL Distribution Point URI와 혼동하지 않는다.
- 제품·build·API version과 CA private key 정보를 반환하지 않는다.

### 7.2 `GET /pki/crl`

- 성공: HTTP 200, `Content-Type: application/pkix-crl`, DER X.509 CRL
- `ETag`를 제공할 수 있으며 클라이언트는 `If-None-Match`를 사용할 수 있다.
- CRL signature를 저장한 CA로 검증한 뒤에만 cache를 교체한다.
- 이전 cache보다 낮은 `CRLNumber`, 잘못된 signature, 미래 `thisUpdate`, 지난 `nextUpdate`를 거부한다.
- Directory는 `thisUpdate < nextUpdate`이고 두 시각이 issuer CA 유효기간 안에 있으며 모든 entry의 revocation 시각이 `thisUpdate` 이하인 CRL만 publish한다. 이 조건을 벗어난 CRL은 signature가 맞아도 사용하지 않는다.
- fetch 실패 시 §11.2의 제한 fallback을 적용한다.

## 8. 조회 endpoint

### 8.1 `GET /api/health`

```xml
<Response xmlns="urn:deepai:service-directory:external">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <UtcNow>2026-07-17T02:00:00Z</UtcNow>
</Response>
```

외부 앱은 자기 ProductCode로 일일 키를 만든다. 와치독은 내부 계약에 따라 `WDOG`를 사용한다. health는 제품·build·patch·API version을 노출하지 않는다.

### 8.2 `GET /api/services?productCode={code}`

- query ProductCode와 일일 키에서 복원한 ProductCode가 같아야 한다.
- 활성 등록 한 건만 반환한다.
- 삭제·미등록은 `404`·`1001 NOT_FOUND`다.
- 승인 대기 개념은 없으며 수정 전 값을 별도로 유지하지 않는다.

```xml
<Response xmlns="urn:deepai:service-directory:external">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Service>
    <Name>VMS Bridge</Name>
    <ProductCode>ABCD</ProductCode>
    <ServiceHostName>vms-bridge.example.local</ServiceHostName>
    <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
    <Port>21500</Port>
    <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
  </Service>
</Response>
```

## 9. 즉시 등록과 인증서 발급

### 9.1 CSR 생성

등록 서버 앱은 자기 호스트에서 keypair를 생성하고 private key를 서비스 account만 읽을 수 있게 보호한다. private key와 PFX를 Directory로 보내지 않는다.

등록 서버 앱은 최초 설정에서 서비스에 사용할 DNS hostname/FQDN 한 개와 IPv4 한 개를 사용자가 선택하게 하고, 등록 전부터 자기 제한 ACL 설정에 한 쌍으로 영속화한다. 요청 때마다 임의 NIC를 열거해 값을 바꾸지 않으며 TCP source IP를 등록값으로 사용하지 않는다.

CSR 요구:

- PKCS#10 자체 signature 유효
- RSA 2048 이상이며 public exponent는 exact 65537, 또는 named curve ECDSA P-256 exact
- RSA CSR signature는 SHA-256/384/512 with RSA PKCS#1 v1.5, P-256 CSR signature는 ECDSA with SHA-256/384/512만 허용. RSA-PSS·SHA-1·다른 key/curve는 거부
- CSR은 canonical DER이고 DER 크기는 최대 48 KiB
- CSR requested SAN은 필수이며 정확히 `dNSName=ServiceHostName` 한 개와 `iPAddress=ServiceIpv4Address` 한 개만 포함한다. CSR signature가 두 선택값과 공개키의 proof-of-possession을 함께 제공한다.
- CSR attribute는 `extensionRequest` 정확히 한 개만 허용하고 그 value도 정확히 하나여야 하며 requested extension은 SAN 하나만 허용한다. 중복 `extensionRequest`, challengePassword를 포함한 다른 CSR attribute, 복수 value와 critical·non-critical 여부와 무관한 다른 extension은 거부한다.

발급 leaf profile:

- EKU `Server Authentication`
- Key Usage `digitalSignature`
- SAN은 Directory가 요청·CSR에서 동일성을 확인한 `ServiceHostName` DNS와 `ServiceIpv4Address` IPv4를 각각 한 개씩 포함하며 CN fallback 없음
- Subject CN은 canonical `ServiceHostName`을 넣지만 표시·도구 호환용이며 identity 검증에 사용하지 않는다.
- `notBefore`는 발급 시각보다 5분 이르게, `notAfter`는 발급 시각 기준 1년으로 하되 CA 만료를 넘기지 않는다.
- serial은 CSPRNG 16바이트 big-endian positive integer다. 첫 바이트는 `0x01..0x7f`, 나머지는 임의값으로 생성하고 CA ledger 충돌이면 재생성한다. wire 표기는 leading zero를 보존한 정확히 32자리 uppercase hex다.
- [RFC 5280 §4.2.1.6](https://www.rfc-editor.org/rfc/rfc5280#section-4.2.1.6)의 URI형 GeneralName 규칙에 따라 CRL Distribution Point의 상대 URI를 금지한다. 하나의 fullName distribution point에 `https://{DirectoryHostName}:21000/pki/crl`과 `https://{DirectoryIpv4Address}:21000/pki/crl` 두 absolute HTTPS URI를 정확히 넣어 DNS 또는 IPv4로 검증 연결한 클라이언트가 같은 DER CRL을 받을 수 있게 한다. 외부 앱은 현재 연결 target과 같은 authority의 URI를 우선 사용하되 어느 URI를 사용해도 저장 site CA signature·CRLNumber·시각 검증을 생략하지 않는다.

### 9.2 `POST /api/registration`

```xml
<RegistrationRequest xmlns="urn:deepai:service-directory:external">
  <RegistrationRequestId>7f35b4b8-854d-4ca1-90bc-da196772f49f</RegistrationRequestId>
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServiceHostName>vms-bridge.example.local</ServiceHostName>
  <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
  <Port>21500</Port>
  <CertificateSigningRequest>MIIC...</CertificateSigningRequest>
</RegistrationRequest>
```

처리 순서:

1. HTTPS·saved pin·exact local endpoint를 검증한다.
2. 일일 키와 payload ProductCode를 검증한다.
3. rate·concurrency·raw body·strict XML·도메인 값을 검증한다.
4. request ID가 이미 있으면 CSR SHA-256과 전체 정규화 payload를 비교한다. 정확한 성공 재시도면 기존 공개 인증서 결과를 반환하고 등록 모드를 요구하지 않는다. 다르면 `1002 CONFLICT`다.
5. 신규 요청이면 등록 모드가 `OPEN`인지 확인한다. 닫혔으면 `1005`다.
6. CSR 자체 signature·key strength와 requested SAN이 정확히 `ServiceHostName` DNS 한 개·`ServiceIpv4Address` IPv4 한 개인지 검증한다. source IP와의 일치 여부는 검사하지 않는다. 실패는 창을 소비하지 않는다.
7. 공용 mutation gate에서 `OPEN -> CLAIMED`를 원자적으로 수행한다. 다른 요청이 claim했으면 `1002`다.
8. 기존 같은 ProductCode가 있으면 active serial을 폐기 대상으로 포함한다.
9. unique serial을 예약하고 leaf를 서명한다.
10. directory record, certificate ledger, idempotency record, 이전 serial 폐기와 CRL을 복구 가능한 transaction으로 commit한다.
11. 성공 뒤 등록 모드를 닫고 결과를 반환한다. commit 결과가 불확실해도 창은 닫는다.

성공 응답:

```xml
<Response xmlns="urn:deepai:service-directory:external">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Status>REGISTERED</Status>
  <RegistrationRequestId>7f35b4b8-854d-4ca1-90bc-da196772f49f</RegistrationRequestId>
  <Service>
    <Name>VMS Bridge</Name>
    <ProductCode>ABCD</ProductCode>
    <ServiceHostName>vms-bridge.example.local</ServiceHostName>
    <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
    <Port>21500</Port>
    <LastModifiedUtc>2026-07-19T02:00:00Z</LastModifiedUtc>
  </Service>
  <Certificate>
    <LeafCertificate>MIIB...</LeafCertificate>
    <IssuerCertificate>MIIC...</IssuerCertificate>
    <SerialNumber>01A4...</SerialNumber>
    <NotBeforeUtc>2026-07-19T02:00:00Z</NotBeforeUtc>
    <NotAfterUtc>2027-07-19T02:00:00Z</NotAfterUtc>
    <CrlUri>/pki/crl</CrlUri>
  </Certificate>
</Response>
```

`Status`:

| 값 | 의미 |
|---|---|
| `REGISTERED` | 이전 active service가 없고 새 등록·발급 완료 |
| `REREGISTERED` | 기존 active 인증서를 폐기하고 새 등록·발급 완료 |
| `REPLAYED` | 이전 성공 요청과 정확히 같은 idempotent 결과 재반환 |

클라이언트는 수령 뒤 다음을 확인한다.

- leaf 공개키가 제출한 CSR 공개키와 같음
- leaf SAN이 정규화한 `ServiceHostName` DNS와 `ServiceIpv4Address` IPv4를 모두 포함하고 다른 SAN이 없음
- ProductCode·ServiceHostName·ServiceIpv4Address가 응답 Service와 같음
- 저장한 site CA로 chain이 끝나고 pin이 같음
- leaf profile·유효기간·serial과 현재 CRL 정상

검증과 안전한 private-key binding 저장이 끝나기 전에는 새 인증서를 서비스 listener에 사용하지 않는다. 외부 앱은 성공한 인증서·선택 hostname·선택 IPv4·port를 하나의 설정 commit으로 영속화하며 어느 한 값만 갱신하지 않는다.

## 10. 인증서 갱신

### 10.1 proof canonicalization

`ServiceIdentitySha256`은 아래 UTF-8 line sequence의 SHA-256이다. 각 값은 §6.2와 §9.1의 정규화를 먼저 적용하고 마지막 LF를 포함한다.

```text
DPAI-SD-SERVICE-IDENTITY
{Name}
{ProductCode}
{ServiceHostName lowercase ASCII}
{ServiceIpv4Address canonical dotted decimal}
{Port invariant decimal}
```

갱신은 현재 유효하고 폐기되지 않은 leaf의 private key로 아래 UTF-8 bytes를 서명한다. 줄 구분은 단일 LF이며 마지막 LF를 포함한다.

```text
DPAI-SD-CERTIFICATE-RENEW
{ProductCode}
{CurrentSerialNumber uppercase 32-character hex}
{RenewalRequestId lowercase D GUID}
{TimestampUtc yyyy-MM-ddTHH:mm:ss.fffZ}
{Nonce Base64 of 16 random bytes}
{Base64(SHA-256(new CSR DER))}
{ServiceIdentitySha256 Base64}
```

- RSA leaf: RSASSA-PKCS1-v1_5 with SHA-256
- ECDSA P-256 leaf: ECDSA with SHA-256, ASN.1 DER encoded signature
- nonce는 요청마다 CSPRNG 16바이트
- timestamp freshness는 서버 UTC 기준 ±60초
- `(serial, nonce)` replay는 최소 10분 차단

### 10.2 `POST /api/certificates/renew`

```xml
<CertificateRenewalRequest xmlns="urn:deepai:service-directory:external">
  <RenewalRequestId>1be2b548-ad43-44ac-b97f-75e038175d53</RenewalRequestId>
  <ProductCode>ABCD</ProductCode>
  <CurrentSerialNumber>01A4...</CurrentSerialNumber>
  <TimestampUtc>2026-06-19T02:00:00.000Z</TimestampUtc>
  <Nonce>base64-16-byte-value</Nonce>
  <Name>VMS Bridge</Name>
  <ServiceHostName>vms-bridge.example.local</ServiceHostName>
  <ServiceIpv4Address>10.0.1.5</ServiceIpv4Address>
  <Port>21500</Port>
  <CertificateSigningRequest>MIIC...</CertificateSigningRequest>
  <ServiceIdentitySha256>base64-sha256</ServiceIdentitySha256>
  <ProofSignature>base64-signature</ProofSignature>
</CertificateRenewalRequest>
```

규칙:

- 현재 serial이 ledger에서 같은 ProductCode의 active·unrevoked leaf여야 한다.
- 현재 인증서 validity와 proof signature를 검증한다.
- 서버가 request의 정규화된 service identity로 `ServiceIdentitySha256`을 다시 계산하고 proof에 결합된 값과 비교한다.
- 등록 앱이 자기 설정에서 hostname 또는 IPv4를 명시적으로 변경하고 §6.2·§9.1의 로컬 확인을 통과하면 갱신 요청에 변경 뒤의 완전한 hostname·IPv4 pair를 다시 제출한다. 두 필드 중 하나를 생략하는 부분 갱신은 없으며, 실제로 두 값이 모두 달라져야 한다는 뜻은 아니다.
- 새 CSR SAN은 새 `ServiceHostName` DNS와 `ServiceIpv4Address` IPv4를 정확히 포함해야 하며 Directory는 source IP로 어느 값도 대체하지 않는다.
- ProductCode 변경은 `1007 CERTIFICATE_NOT_RENEWABLE`이다. private key 분실, 현재 leaf 만료·폐기는 관리자가 등록 모드를 열어 재등록한다.
- 성공 응답의 Certificate 형식은 등록과 같다. `Status=RENEWED`를 사용한다.
- 같은 renewal request ID·CSR의 재시도는 같은 결과를 반환한다.
- 새 인증서 수령·검증 전 old leaf를 즉시 폐기하지 않는다. SAN이 같으면 overlap은 최대 7일, SAN이 바뀌면 최대 24시간이며 그 뒤 old serial을 `Superseded` reason으로 CRL에 추가한다. old certificate 원래 만료가 더 빠르면 그 시각을 넘기지 않는다. `Unspecified`·임시 `CertificateHold`·`RemoveFromCRL`은 이 계약에서 사용하지 않는다.
- 현재 인증서가 이미 만료·폐기됐거나 private key를 잃었으면 등록 모드를 통한 재등록만 허용한다.

서버 앱은 만료 30일 전 자동 갱신을 시작한다. 운영자가 저장 hostname·IPv4 pair를 변경했거나 선택 IPv4가 더 이상 로컬 NIC·listener에 유효하지 않으면 새 pair를 선택·저장하는 설정 흐름을 먼저 거친 뒤 즉시 자동 재발급을 요청한다. 성공할 때까지 bounded backoff를 사용하고 실패 시 이전 pair·인증서로 원자 rollback한다.

## 11. 조회한 대상 서비스 연결

### 11.1 필수 검증

외부 앱은 `GET /api/services`가 반환한 `ServiceHostName`, `ServiceIpv4Address`, `Port`를 모두 보존한다. 현장·제품 설정에 따라 DNS hostname 또는 IPv4를 접속 target으로 선택하고 HTTPS로 직접 연결한다.

- hostname mode는 `https://{ServiceHostName}:{Port}`, IPv4 mode는 `https://{ServiceIpv4Address}:{Port}`를 사용한다.
- 자동 fallback을 제공한다면 DNS 해석·TCP 연결 실패에서만 다른 target을 시도할 수 있다. TLS 인증서·pin·SAN·CRL 검증 실패를 다른 target으로 우회하지 않는다.
- 요청의 source IP, Directory가 관찰한 registration source 또는 DNS 역조회 결과를 접속 target으로 사용하지 않는다.

각 연결에서 다음을 모두 검증한다.

1. chain이 해당 Milestone server identity에 저장한 Directory site CA로 끝난다.
2. chain CA의 SPKI pin이 저장 pin과 같다.
3. leaf SAN에 Directory가 반환한 `ServiceHostName` DNS와 `ServiceIpv4Address` IPv4가 모두 있고, 실제 선택한 접속 target도 그 SAN과 일치한다.
4. leaf와 CA가 현재 유효하다.
5. leaf EKU가 `Server Authentication`, Key Usage가 `digitalSignature`다.
6. CA constraints와 path length가 정책에 맞는다.
7. signature·key algorithm 강도가 §9.1 이상이다.
8. 현재 유효하고 signature가 검증된 CRL에 leaf serial이 없다.

CN fallback, name mismatch 무시, OS trust store의 다른 root fallback과 `accept any certificate`를 금지한다.

### 11.2 CRL 불가 fallback

유효한 CRL을 받을 수 없거나 cache의 `nextUpdate`가 지났으면 fail closed가 기본이다. 단, 가이드의 제한 예외로 다음 조건을 모두 만족하는 재연결만 허용한다.

- 같은 canonical endpoint
- 이전 유효 CRL과 전체 certificate 검증에 성공한 적이 있음
- leaf DER SHA-256 fingerprint가 이전과 정확히 같음
- leaf 자체 validity가 남아 있음
- CA와 pin이 그대로임

새 leaf, 갱신·교체된 leaf, 처음 보는 endpoint는 유효한 CRL을 받을 때까지 차단한다. fallback 사용과 CRL 복구는 보안 진단에 기록하고 사용자에게 degraded 상태를 표시한다.

## 12. timeout·재시도·권장 흐름

### 12.1 timeout

- 연결 timeout: 3초
- health·service 조회·`/pki/ca`: 서버 전체 5초
- registration·renewal: 서버 전체 15초
- CRL: 연결 3초, 전체 10초

### 12.2 재시도

- GET은 최초 뒤 최대 2회, 1초·3초 backoff와 작은 jitter를 사용한다.
- registration·renewal은 같은 request ID, 같은 CSR DER, 같은 정규화 payload로만 재시도한다.
- 매 시도 일일 API 키 IV와 renewal nonce·timestamp·proof는 새로 만들 수 있다. idempotency identity는 request ID와 CSR hash·semantic payload다.
- response timeout 뒤 registration mode를 다시 열도록 즉시 요구하지 말고 exact replay를 먼저 시도한다.
- `1005 REGISTRATION_MODE_CLOSED`이면 자동 반복하지 않고 관리자에게 등록 모드 시작을 요청한다.
- pin mismatch·CSR 공개키 mismatch·invalid CRL·proof 실패는 자동 우회하거나 재신뢰하지 않는다.

### 12.3 전체 앱 흐름

조회 클라이언트:

1. Milestone Management Server 연결 성공
2. 같은 서버의 `DirectoryHostName`·`DirectoryIpv4Address`로 Directory base 구성
3. 최초 TOFU 또는 저장 pin 검증
4. CA/CRL cache 검증
5. health와 service 조회
6. 조회한 서버에 HTTPS 연결하고 site CA·pin·SAN·CRL 검증

등록 서버 앱:

1. 위 trust 절차 완료
2. 자기 서비스의 `ServiceHostName`·`ServiceIpv4Address`를 사용자가 선택하고 로컬 검증 뒤 앱 설정에 한 쌍으로 영속화
3. 관리자가 Directory 설정 UI의 등록 서비스 화면에서 1시간 등록 모드 시작
4. 저장 service pair를 exact SAN으로 넣은 로컬 private key·CSR와 request ID 생성
5. 일일 키와 service pair를 포함한 registration 제출
6. 즉시 발급 결과의 service pair·두 SAN을 검증하고 인증서와 원자 저장
7. HTTPS server listener에 leaf 적용
8. 만료 30일 전 또는 저장 service hostname·IPv4 중 하나 이상 변경 시, 변경 뒤의 완전한 pair로 자동 갱신·재발급

## 13. 호환성과 구현 상태

현재 배포된 외부 소비자는 없으므로 기존 평문 HTTP·CSR 없는 `RegistrationRequest`·`PENDING_*`·`PendingId` 계약을 유지하지 않는다. 다음은 목표 계약에서 제거된다.

- `http://{ListenAddress}:21000` 원격 base
- TLS 미사용 예외
- 외부 승인 대기와 `PENDING_NEW`, `PENDING_MODIFY`, `PENDING_EXISTS`
- `PendingId` 기반 승인 대기 의미
- 등록 결과를 service polling으로만 추정하는 절차

PKI core 1차 소스와 단위 테스트 소스는 추가됐지만 `external.xsd`·DTO·handler·listener는 아직 위 목표 계약으로 변경되지 않았다. 후속 구현은 [인증서 전환 변경계획](./02-certificate-transition.md)에 따라 XSD·DTO·listener·installer·저장·복구·테스트를 함께 변경해야 한다. 실제 wire 연결과 검증 전까지 현재 바이너리가 이 명세를 제공한다고 표시하지 않는다.
