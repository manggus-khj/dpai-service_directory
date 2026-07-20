# 서비스 디렉토리 내부 API 명세

```text
최초 작성일: 2026-07-17
최종 변경일: 2026-07-19
revision: 1
```

> 문서 상태: 인증서 기반 목표 내부 계약 확정
> 구현 상태: PKI core 1차 소스만 부분 구현. 현재 코드는 pending 승인 3 endpoint와 평문 Peer HTTP를 사용하며 등록 모드·CA 저장·CRL publish·Peer HTTPS 목표 계약으로 전환 필요
> 대상 독자: 메인 서비스, 트레이 앱, 와치독, 피어 동기화 구현 개발자

이 문서는 서비스 디렉토리 구성요소 사이의 등록 모드·인증서 운영·관리·동기화·서비스 제어 계약을 정의한다. 다른 애플리케이션의 주소 구성, TOFU·pin, CSR 발급·갱신과 대상 서비스 인증서 검증은 [외부 애플리케이션 API 명세](./04-api-01-external-application.md)만을 따른다.

## 1. 내부 인터페이스 경계

| 호출자 → 수신자 | 인터페이스 | 용도 |
|---|---|---|
| 설정 UI → 메인 서비스 | `/admin/*` | 등록 모드, 등록 서비스·인증서 폐기, CA·동기화 관리 |
| 와치독 → 메인 서비스 | `GET /api/health` | 프로세스 외부 생존 확인 |
| 메인 서비스 → 피어 메인 서비스 | HTTPS `/api/sync/*` | TLS 뒤 핸드셰이크, 데이터 교환, 해제 |
| 트레이 → 와치독 | `\\.\pipe\SvcDirWatchdog` | 시작, 종료, 재시작, 상태 |

헬스체크 응답의 단일 원본은 [외부 명세의 `GET /api/health`](./04-api-01-external-application.md#81-get-apihealth)다.

## 2. 신뢰 경계와 인증 계약

### 2.1 Admin

- `/admin/*`는 `http://127.0.0.1:21000`의 loopback 인터페이스에만 바인딩하고 원격 요청을 거부한다. wildcard, `0.0.0.0` 또는 원격 인터페이스 바인딩은 금지한다.
- 모든 Admin 요청에서 실제 local endpoint가 정확히 `127.0.0.1:21000`이고 remote address도 OS가 판정한 loopback인지 확인한다. IP literal prefix나 `IsLocal` 하나만으로 이 경계를 대체하지 않으며 endpoint가 없거나 불일치하면 거부한다.
- loopback은 인증이 아니다. 같은 포트의 External·Peer 경로까지 Windows 인증으로 바뀌지 않도록 `AuthenticationSchemeSelectorDelegate` 또는 경로가 분리된 listener를 사용해 `/admin/*`에만 `Negotiate`를 선택한다. 기준 주소가 IP literal `127.0.0.1`이므로 Kerberos·SPN을 전제로 하지 않고 AD 도메인 사용자와 Workgroup 로컬 사용자 모두 NTLM을 허용한다. 추후 loopback으로만 해석되는 hostname과 올바른 SPN을 별도 검증한 배포에서는 Kerberos가 협상될 수 있지만 필수 계약이 아니다. Admin의 Basic·Anonymous 인증은 허용하지 않는다. External·Peer에서 HTTP.sys의 `Anonymous`를 선택하는 것은 각 명세의 일일 키·HMAC 애플리케이션 인증을 생략한다는 뜻이 아니다.
- `HttpListener.UnsafeConnectionNtlmAuthentication`은 `false`로 유지한다. 연결 단위 NTLM 인증 캐시를 켜서 다른 연결·요청의 호출자 identity를 재사용해서는 안 된다.
- 공용 host의 `AuthenticationSchemeSelectorDelegate`는 raw request-target의 exact `/admin/` prefix이면서 actual local endpoint `127.0.0.1:21000`·remote loopback을 모두 확인한 요청에만 `Negotiate`를 선택한다. encoded·case 변형, 원격 인터페이스와 endpoint 누락·불일치는 `Anonymous`를 선택한 뒤 host에서 body 없는 `404`로 닫아 원격에 Negotiate challenge를 내지 않으며 Admin adapter에 전달하지 않는다. 이는 정상 Admin 경계에서 Anonymous를 허용한다는 뜻이 아니다.
- Admin host의 bounded body read, application handler와 응답 완료를 합친 전체 서버 deadline은 endpoint 공통 **10초**다. 만료 시 연결을 닫고 늦게 끝난 mutation을 성공 응답으로 보고하지 않는다. application handler는 deadline 안에 완료하도록 구현해야 하며 강제 thread abort를 사용하지 않는다.
- 인증된 Windows identity가 설치 시 생성하는 로컬 그룹 `DEEPAi-ServiceDirectory-Operators`의 구성원일 때만 Admin API를 인가한다. 인증 실패는 `401`, 그룹 인가 실패는 `403`이다.
- 인가는 현재 요청의 `WindowsIdentity`와 `MACHINE\DEEPAi-ServiceDirectory-Operators`를 해석한 정확한 SID로 매번 수행한다. 현재 프로세스 identity, 내장 Administrators 또는 같은 이름의 도메인 그룹으로 fallback하지 않으며 그룹 SID 해석·token 검사가 실패하면 fail closed한다.
- 도메인 환경에서는 필요한 AD 사용자 또는 AD 그룹을, Workgroup 환경에서는 필요한 로컬 사용자를 각 서버의 `DEEPAi-ServiceDirectory-Operators` 로컬 그룹에 추가한다. 트레이 앱은 일반 권한으로 실행하며 UI 표시 여부를 인가로 사용하지 않는다.
- 그룹 구성 변경은 새 Windows logon token에 반영된 뒤 유효하다. 설치 프로그램과 운영 문서는 재로그인 또는 프로세스 재시작이 필요할 수 있음을 안내한다.

### 2.2 Peer

- 원격 동기화는 TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS를 사용한다. protocol·cipher suite를 앱 코드에 고정하지 않으며 현재 v1 코드의 평문 HTTP canonical endpoint는 목표 계약에서 제거한다.
- 같은 site의 두 Directory는 같은 site CA를 사용한다. CA 인증서 자체에는 endpoint SAN이 없다. 각 Directory leaf에는 해당 `DirectoryHostName` DNS SAN과 `DirectoryIpv4Address` IP SAN이 모두 있어야 하며 `DirectoryIpv4Address`는 그 인스턴스의 IPv4 `ListenAddress`와 같다. peer는 chain·CA SPKI pin·두 SAN·validity·profile을 검증한다. 이 Directory pair를 동기화하는 등록 서비스의 `ServiceHostName`·`ServiceIpv4Address`로 복사하지 않는다. TLS 검증 실패에서는 pairing·HMAC 단계로 진행하지 않는다.
- Peer용 Directory identity와 동기화 레코드의 `ServiceHostName`·`ServiceIpv4Address`는 서로 다른 값이다. 외부 앱이 등록한 service identity를 Directory leaf SAN이나 Peer endpoint로 복사하지 않는다.
- 최초 페어링과 이후 피어 인증은 AD에 의존하지 않고 §5.3의 ECDH P-256·SAS 계약과 §5.4의 HMAC 계약을 따른다. HTTP Basic, URL query key와 캡처 후 재사용 가능한 평문 bearer token은 금지한다.
- 제품 자체의 고정 CIDR allowlist는 두지 않는다. listener를 승인된 폐쇄망 인터페이스에만 바인딩하고 wildcard remote prefix·평문 HTTP 또는 비신뢰망 노출을 금지한다.
- 모든 Peer 요청의 실제 local endpoint가 설정한 `ListenAddress:21000`과 정확히 일치해야 한다. 이 검사는 실제 원격 endpoint·`InstanceId`·HMAC binding 검증을 대체하지 않으며 local endpoint 정보를 얻지 못하면 요청을 거부한다.
- Windows 방화벽 인바운드 규칙은 Domain·Private 프로필에서만 해당 포트와 프로그램을 허용하고 Public 프로필에서는 차단한다. 설치 프로그램과 제품 설정은 원격 CIDR·원격 주소 범위 제한을 만들거나 요구하지 않는다.
- 모든 Peer 요청에서 실제 원격 IP와 인증된 `InstanceId`가 페어링 때 저장한 단일 피어 endpoint의 IP·`InstanceId`와 모두 일치해야 한다. 이는 페어링 identity binding 검증이지 원격 CIDR·대역 allowlist가 아니다. TCP source port는 비교하지 않으며 주소 검증은 암호학적 피어 인증을 대신하지 않는다.
- 페어링 root가 없거나 DPAPI 복호화·ACL 검증에 실패하면 Peer API의 handshake·exchange·release·revoke를 닫고 재페어링을 요구한다.

### 2.3 Local IPC

- Named Pipe는 명령 허용 목록과 호출자 ACL을 모두 적용한다.
- 파이프 ACL은 `DEEPAi-ServiceDirectory-Operators` 로컬 그룹과 와치독 서비스 SID, `SYSTEM`, 로컬 `Administrators`에만 필요한 연결·읽기·쓰기 권한을 부여한다. `Everyone`, `Users`, `Authenticated Users`, Anonymous에 쓰기 권한을 부여하지 않는다.
- 트레이 사용자는 Admin API와 같은 방식으로 AD 사용자·그룹 또는 Workgroup 로컬 사용자를 로컬 운영자 그룹에 추가해 인가한다.

### 2.4 Health

- 외부 애플리케이션과 와치독 모두 `GET /api/health` 호출 전에 [외부 명세의 일일 API 키](./04-api-01-external-application.md#3-external-일일-api-키)를 제공한다.
- 와치독은 health 전용 구성요소 코드 `WDOG`와 시스템 로컬 날짜로 키를 생성한다. `WDOG`는 디렉토리 등록 ProductCode가 아니다.
- health는 키에서 복원한 ProductCode의 형식과 서버 로컬 날짜만 검증한다. 이 키는 Admin·Peer 접근 권한을 부여하지 않는다.
- 와치독 health 대상은 로컬 IPC 경계의 `http://127.0.0.1:21000/api/health`로 고정한다. External·Peer remote는 HTTPS를 사용하며 loopback prefix가 원격 노출을 넓히지 않는다.
- 와치독 health도 실제 local endpoint `127.0.0.1:21000`과 loopback remote address를 모두 확인하며, External health는 설정한 `ListenAddress:21000`의 local endpoint guard를 적용한다.
- 와치독 health의 일일 키 실패는 외부 health와 같은 safe `401`·`1003 INVALID_API_KEY` envelope과 `4101`, `Boundary=WATCHDOG_HEALTH`, `Operation=WATCHDOG_HEALTH` 보안 진단을 사용한다. local·remote endpoint 거부는 body 없는 `403`과 같은 boundary·operation의 `4106`을 사용한다.
- 와치독은 10초 간격으로 health를 호출하고 각 호출의 연결부터 전체 응답 완료까지 timeout을 3초로 제한한다.
- loopback health 수신 경계는 복원한 ProductCode가 형식과 서버 로컬 날짜를 통과하면 그 값을 조합 rate key로만 사용한다. 호출자가 `WDOG`로 키를 생성하는 계약과 별개로 서버가 복원값을 `WDOG` 문자열과 다시 비교하는 규칙은 만들지 않는다.
- 원격 External 어댑터와 loopback health 어댑터는 같은 서비스 인스턴스의 전체 External 동시 실행 limiter `32`개를 공유한다. loopback health의 `(ProductCode, remote loopback IP)` capacity `5`·분당 `30` token bucket과 key map은 원격 External 세 endpoint의 aggregate rate map과 분리한다.
- loopback에서는 exact raw `GET /api/health`만 열고 `/api/services`, `/api/registration`, method 불일치와 encoded path는 인증·공유 동시 실행 제한 뒤 body 없는 `404`로 닫는다. host는 `RawUrl`에서 decode·정규화하지 않은 path와 raw ASCII query를 전달한다.
- 비어 있지 않은 raw `Content-Encoding`은 body 없는 `415`, raw body 16 KiB 초과는 body 없는 `413`이며 query와 body는 모두 비어 있어야 한다. token 부족 `429`만 계산한 정수 초 `Retry-After`를 보내고 공유 concurrency 또는 rate key 수용 한계처럼 해제 시각을 알 수 없는 `429`에는 보내지 않는다.
- transport-neutral 경계와 공용 `HttpListener` host 소스가 이 순서를 구현하고 bounded synchronous body read·처리·응답 완료 전체에 외부 health와 같은 `5초` deadline을 적용한다. 만료 시 context를 abort하며 실제 Windows listener의 read 해제·deadline 경합은 아직 실행 검증하지 않았다.

### 2.5 Site CA와 issuer 역할

- 기본 site CA private key PKCS#8은 DPAPI `LocalMachine`으로 보호해 `secrets\ca.key`에 저장하고 상속을 차단한 exact ACL로 메인 서비스 SID·`SYSTEM`·로컬 `Administrators`만 허용한다. `ca.key.bak`과 평문 key file을 금지하며, DPAPI 평문은 서명·backup 처리 중 메모리에서만 사용하고 byte buffer를 즉시 지운다. 로컬 관리자는 이 위협 모델의 신뢰된 복구 주체다.
- 기본 site CA와 Directory leaf key는 RSA 3072다. CA·leaf·CRL은 SHA-256 with RSA PKCS#1 v1.5로 서명하고 CA는 `pathLen=0`·`keyCertSign`·`cRLSign`·SAN 없음 profile을 사용한다. 등록 서비스 CSR·leaf의 세부 profile은 [외부 명세 §9.1](./04-api-01-external-application.md#91-csr-생성)이 단일 원본이다.
- CA backup은 운영자가 지정한 암호로 암호화하고 평문 export를 금지한다. 서비스는 임의 client 경로를 받지 않고 제한 ACL의 `%ProgramData%\DEEPAi\ServiceDirectory\backups\ca\`에 생성한다. API에는 내부 절대 경로·암호·key를 반환하지 않고 canonical 파일명과 SHA-256만 반환한다. 일반 uninstall은 보존하고 명시적 전체 삭제에서만 제거한다.
- 같은 site의 Peer는 동일 CA trust anchor를 사용한다. 서로 다른 CA를 자동 병합하거나 pairing만으로 상대 CA를 무조건 신뢰하지 않는다.
- serial ledger와 CRL split-brain을 막기 전에는 active CA issuer를 하나만 허용한다. 보조 Directory는 조회·sync를 제공하되 신규 발급·폐기는 active issuer로 전달하거나 fail closed한다.
- issuer 승격, CA restore와 key rotation은 로컬 운영자 확인과 감사 로그를 요구하며 네트워크 partition에서 양쪽 자동 승격을 금지한다.
- 등록 모드 `OPEN/CLAIMED`는 로컬 process memory state로 peer와 sync하지 않는다. active issuer가 아닌 인스턴스에서는 open 요청을 거부한다.

Admin 호출자 인증·인가, CA 운영과 Peer 상호 인증의 계약은 이 문서에서 확정한다. 구현은 해당 계약과 실패 경로를 검증하기 전까지 운영 준비 완료로 처리하지 않는다.

## 3. 공통 HTTP·XML 규칙

- 포트: TCP `21000`
- 프로토콜 의미: HTTP/1.1
- API URL, XML payload와 협상에는 별도 API·protocol version 필드를 두지 않는다. 외부 소비자가 없는 현재 단계에서 미출시 HTTP·pending 계약은 이 목표 계약으로 대체한다. 목표 계약 최초 공개 뒤에는 경로와 필드 의미를 바꾸지 않고 응답 마지막의 `Extensions` 또는 별도 endpoint로만 호환 확장한다. 암호 목적 label의 `v1`은 API 버전이 아니라 고정 domain-separation 문자열이다.
- Admin XML의 고정 기본 namespace는 `urn:deepai:service-directory:admin`, Peer XML은 `urn:deepai:service-directory:peer`다. namespace에는 버전 suffix를 붙이지 않으며 namespace가 없거나 경계와 다른 namespace인 요청은 `400 BAD_REQUEST`로 거부한다.
- 규범 스키마는 Admin [`xsd/admin.xsd`](./04-api/admin.xsd), Peer [`xsd/peer.xsd`](./04-api/peer.xsd)다. 요청은 DTD·외부 엔터티·본문 크기·깊이 제한을 먼저 적용한 뒤 해당 스키마의 root, 순서, 필수·선택 cardinality와 값을 엄격히 검증한다. 운영 상한인 sync batch 항목 수는 아래의 인증 후 bounded streaming count에서 별도로 검증한다. 알 수 없는 요청 요소·속성, 중복 요소와 mixed content는 허용하지 않는다.
- Admin·Peer `NameType`의 XSD `maxLength=256`은 .NET Framework XML Schema validator가 supplementary Unicode scalar 하나를 UTF-16 code unit 두 개로 계산하는 동작을 수용하는 canonical wire envelope다. 의미상 제한을 256자로 늘리는 규칙이 아니며, 수신 측은 XSD 통과 뒤에도 trim된 이름이 1~128 Unicode scalar이고 UTF-8 최대 512바이트인지 공통 도메인 검증으로 다시 확인한다.
- 응답의 호환 확장은 `Response`의 마지막 선택 요소인 `Extensions` 자식에서만 허용한다. 클라이언트는 `Extensions` 안의 모르는 요소를 무시해야 하며 그 밖의 위치에 요소를 추가하거나 기존 필드 의미를 바꾸거나 새 필수 필드를 추가하지 않는다. 요청에는 `Extensions`를 허용하지 않는다.
- remote External·PKI·Peer scheme은 `https`이며 TLS 1.2 이상을 지원하는 OS 보안 기본값과 site CA certificate binding을 사용한다. protocol·cipher suite를 앱 코드에 고정하지 않고 지원 OS에서 구버전 비활성화를 검증한다. 평문 remote listener와 redirect fallback을 구성하지 않는다. Admin·와치독 loopback `http://127.0.0.1`은 로컬 IPC 경계로만 유지한다.
- External·Peer 원격 prefix는 설치 프로그램이 `config.xml`에 저장한 exact unicast IPv4 `ListenAddress`, Admin과 와치독 health prefix는 `127.0.0.1`이다. 메인 서비스는 `ListenAddress`가 현재 로컬 Domain·Private 인터페이스에 할당된 non-loopback canonical IPv4인지 기동 때 검증한다. IPv6, APIPA, multicast, loopback, `0.0.0.0`, broadcast와 선행 0 표기를 거부한다. 누락·미할당·Public 주소면 어떤 원격 prefix도 열지 않은 채 기동을 실패시킨다.
- [Microsoft HTTP Server API 문서](https://learn.microsoft.com/windows/win32/http/urlprefix-strings)에 따라 IP literal prefix는 IP-bound weak wildcard이므로 보안 강제 수단으로 의존하지 않는다. 수신 측은 매 요청 `HttpListenerRequest.LocalEndPoint`를 신뢰 경계의 설정 주소와 TCP `21000`에 다시 결합하고 loopback 경계에서는 `RemoteEndPoint.Address`도 loopback인지 확인한다.
- 본문: `application/xml; charset=utf-8`
- payload·프로토콜 시각: UTC ISO 8601. health 일일 API 키 날짜와 registration mode countdown 표시 계산의 wall-clock 기준만 명시된 로컬/UTC 규칙을 사용하고 만료 강제는 monotonic elapsed time을 병행한다.
- XML DTD와 외부 엔터티를 금지하고 루트 요소를 깊이 1로 계산한 최대 깊이를 16으로 제한한다.
- 모든 GET 요청은 body를 허용하지 않는다.
- `/api/health`, `/admin/*`, `/api/sync/pairing/*`, `/api/sync/handshake`, `/api/sync/release`, `/api/sync/revoke`의 요청과 응답 본문은 각각 UTF-8 원문 기준 최대 16 KiB다. Admin 목록은 `pageSize`보다 적게 반환하더라도 16 KiB를 넘기기 전에 `NextCursor`로 다음 페이지를 이어야 한다. `/api/sync/exchange` 요청과 응답 본문은 각각 최대 4 MiB다. `/api/sync/pki-state` 요청은 최대 16 KiB, 응답은 인증서 ledger와 CRL을 포함해 최대 4 MiB다.
- `GET /admin/services`는 opaque cursor 페이지네이션을 사용한다. `pageSize` 기본값은 100, 최댓값은 250이며 1 미만 또는 250 초과는 `1000 BAD_REQUEST`다. 마지막 페이지가 아니면 응답의 `NextCursor`를 그대로 다음 요청의 `cursor`로 전달한다.
- Admin 속도 제한은 인증된 identity와 서비스 인스턴스를 기준으로 이동 1분 창에서 읽기 `60회/분`·burst `15`, 변경 `10회/분`, `/admin/sync/now` `2회/분`이다. 읽기는 최근 60초 요청 queue와 capacity 15·초당 1 token의 burst bucket을 모두 통과해야 하며, 변경과 sync-now는 각각 최근 60초 queue에서 10개·2개를 허용한다. sync-now 한 건은 변경과 sync-now 양쪽에 모두 기록하고 제한되면 어느 쪽에도 부분 기록하지 않는다. 동시에 처리하는 Admin 요청은 서비스 인스턴스당 최대 8개다. identity rate 상태는 최대 2,048개를 추적하고 마지막 요청 뒤 10분이 지난 상태를 정리한다. 추적 cap·동시성 cap은 정확한 재시도 시각을 제공하지 않으므로 `429`에 `Retry-After`를 넣지 않는다.
- Peer 속도 제한은 설정된 원격 endpoint를 기준으로 handshake `12회/분`·burst 3, exchange batch `30회/분`, PKI state `6회/분`이다. 인증 성공 뒤에는 endpoint와 `InstanceId`를 함께 키로 사용한다. 피어별 활성 sync 세션은 1개만 허용한다.
- sync batch는 활성 레코드와 톰스톤을 합해 최대 1,000개이며 한 batch의 요청·응답은 위 4 MiB 한도를 모두 만족해야 한다. 모든 inbound Peer `SyncData`는 각 방향의 HMAC 검증에 성공한 뒤, DTD·외부 엔터티가 비활성화된 forward-only XML reader로 `Items/Service` 수를 materialization과 XSD 검증 전에 센다. Push request는 1,001번째 항목에서 즉시 중단해 signed `413`·`1004 LIMIT_EXCEEDED`를 반환한다. Pull success response에서 같은 초과를 발견한 호출자는 응답을 protocol-invalid로 거부하고 전체 sync를 중단하며 staging, 현재 snapshot, XML과 logical clock을 변경하지 않는다. 이 한도를 항상 같은 수신 경계에서 강제하기 위해 `peer.xsd`의 반복 구조 자체는 `maxOccurs="unbounded"`이고 실제 1,000개 상한은 이 bounded count가 강제한다.
- 브라우저 호출, CORS와 정적 파일 제공은 지원하지 않는다. 제품·프레임워크 식별 응답 헤더는 제거하거나 플랫폼이 허용하는 최소 정보로 제한한다.
- 디렉토리·등록 모드 claim·인증서 ledger·CRL·설정의 모든 변경 명령과 sync 최종 병합·게시는 서비스 인스턴스당 하나의 state mutation gate로 직렬화한다. 네트워크 송수신, TLS·CSR 검증과 XML parse는 gate 밖에서 수행하고, gate 안에서 현재 revision 재검증, 복구 저널·원자 저장과 immutable snapshot 교체를 완료한다. 조회는 현재 immutable snapshot을 사용해 gate를 점유하지 않는다.

공통 응답 envelope은 경계에 맞는 고정 namespace를 사용한다. 아래는 Admin 예이며 Peer 응답은 기본 namespace만 `urn:deepai:service-directory:peer`로 바꾼다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message></Message>
  <!-- 엔드포인트별 payload -->
</Response>
```

와치독 health의 성공·오류 envelope은 외부 namespace와 [외부 health 계약](./04-api-01-external-application.md#81-get-apihealth)을 그대로 사용하므로 아래 Admin·Peer 내부 오류 코드 표에 새 code를 추가하지 않는다. 와치독 health의 `400`·`401`·`429`·`500`은 외부 safe envelope을 사용하고 조기 거부 `403`·`404`·`413`·`415`는 body가 없다.

내부 오류 코드:

| Code | 이름 | 의미 |
|---|---|---|
| 0 | `OK` | 성공 |
| 1000 | `BAD_REQUEST` | XML, 경로 매개변수 또는 값 검증 실패 |
| 1001 | `NOT_FOUND` | 서비스 또는 등록 대상 없음 |
| 1002 | `CONFLICT` | 이미 처리됨, 현재 상태와 충돌 |
| 1004 | `LIMIT_EXCEEDED` | 활성 서비스·페이지·속도·동시 실행·PKI 크기 등 확정 제한 초과 |
| 2001 | `NOT_PEER` | 설정된 피어가 아닌 호출자 |
| 2002 | `PEER_MISMATCH` | 상대의 피어 설정이 수신자를 가리키지 않음 |
| 2003 | `CLOCK_SKEW` | Peer 인증 timestamp가 수신 시각 기준 ±60초 freshness 범위를 벗어남 |
| 2004 | `SYNC_DISABLED` | 수신 측 동기화가 비활성 |
| 2005 | `REVISION_COLLISION` | 같은 `LogicalVersion`·`OriginInstanceId`에 서로 다른 payload가 존재 |
| 2006 | `DIRECTORY_CAPACITY` | 병합 후보의 활성 서비스가 1,000개를 초과 |
| 2007 | `LOGICAL_CLOCK_EXHAUSTED` | `LogicalClock`이 unsigned 64-bit 최댓값에 도달해 새 변경 발급 불가 |
| 3000 | `INTERNAL` | 내부 오류. 상세 예외는 응답하지 않고 별도로 승인된 진단 대상에만 기록 |

위 목록은 [`xsd/admin.xsd`](./04-api/admin.xsd)와 [`xsd/peer.xsd`](./04-api/peer.xsd)의 닫힌 `CodeType`과 같은 계약이다. 목록에 없는 code는 스키마 위반이며 공개 뒤 새 값을 추가하거나 기존 code의 의미를 바꾸지 않는다. 새 endpoint도 공통 `Response`를 사용하면 현재 code만 사용한다.

인증 실패와 권한 부족은 각각 HTTP `401`·`403`, 요청 크기 초과는 `413`, 속도·동시성 제한 초과는 `429`를 사용한다. 아래 표가 body 없음으로 고정한 조기 거부에는 XML envelope을 만들지 않으며 상세 인증·제한 상태를 외부에 노출하지 않는다.

### 3.1 HTTP 상태 규칙

HTTP `200`은 `Code=0`인 성공 응답에만 사용한다. 클라이언트는 HTTP 상태와 envelope `Code`를 모두 확인하며 오류를 성공 HTTP 상태에 넣지 않는다.

| HTTP | envelope | 사용처 |
|---|---|---|
| `200` | `0 OK` | 정상 처리 |
| `400` | `1000 BAD_REQUEST` | 쿼리·경로 값, XML, namespace, 필드 또는 스키마 검증 실패. 단, 인증·HMAC 뒤 secure count에서 먼저 발견한 sync batch 항목 수 초과는 아래 `413` 계약 적용 |
| `401` | body 없음 또는 signed `2003 CLOCK_SKEW` | Admin 인증 실패와 Peer HMAC·identity·epoch·replay·session 검증 실패는 body 없음. 현재 피어의 HMAC 검증 뒤 freshness 실패만 signed `2003` |
| `403` | body 없음 또는 signed `2001 NOT_PEER` | Admin 운영자 그룹 인가 실패는 body 없음. 현재 피어의 HMAC 검증 뒤 remote endpoint·peer binding이 허용되지 않으면 signed `2001` |
| `404` | `1001 NOT_FOUND` 또는 body 없음 | 서비스·등록 대상 없음 또는 정의되지 않은 경로 |
| `409` | `1002`, `2002`, `2004`~`2007` | 상태 충돌, peer binding 불일치, sync 비활성, revision·용량·logical clock 충돌 |
| `413` | body 없음 또는 `1004 LIMIT_EXCEEDED` | raw byte 제한 초과는 body 없음. 인증·HMAC 검증 뒤 XSD보다 먼저 수행한 secure streaming count에서 Peer batch 1,000개 제한 초과 시 signed `1004` |
| `415` | body 없음 | 지원하지 않는 `Content-Type` |
| `429` | `1004 LIMIT_EXCEEDED` | 속도, burst, 동시 실행 또는 replay cache 포화 |
| `500` | `3000 INTERNAL` | 처리되지 않은 내부 오류. 상세 예외 비노출 |

Admin `401`·`403`은 항상 body 없이 반환한다. Peer는 현재 peer·epoch의 HMAC 검증 전 실패에 body나 서명된 상세 오류를 보내지 않는다. HMAC 검증 뒤 발견한 `2003 CLOCK_SKEW`에는 서명된 `401`을, remote endpoint·peer binding 거부에는 signed `403`·`2001 NOT_PEER`를, 인증·HMAC 뒤 secure streaming count에서 발견한 batch 항목 수 초과에는 signed `413`·`1004 LIMIT_EXCEEDED`를, 그 밖의 semantic Peer 오류에는 위 표의 서명된 오류 응답을 반환한다. raw byte 제한의 `413`, `415`와 정의되지 않은 경로의 `404`는 항상 body 없이 반환한다. 인증이 끝난 Peer의 성공·오류 응답은 모두 §5.4의 response MAC을 가져야 한다. 시간 기반 `429`에는 `Retry-After`를 보내며 해제 시각을 알 수 없는 제한에는 생략할 수 있다.

### 3.2 보안 진단 기록

- 인증·인가와 listener endpoint 경계 거부는 [개발계획 §9.5](./03-development.md#95-보안-진단-event-log)의 Windows `Application` Event Log 계약에 따라 기록한다. 9개 시스템 파일 이벤트에는 추가하지 않는다.
- 와치독 health 일일 키 실패는 `4101`과 `Boundary=WATCHDOG_HEALTH`·`Operation=WATCHDOG_HEALTH`, Admin 인증 실패는 `4102`, 운영자 그룹 인가 실패는 `4103`, Peer HMAC·identity·epoch·freshness·replay·session 실패는 `4104`, 연결 뒤 Named Pipe client token 인가 실패는 `4105`, HTTP local/remote endpoint 경계 위반은 `4106`을 사용한다.
- remote TLS trust·SAN·pin·CRL 검증 실패는 `4107`, CSR·인증서 발급·갱신 요청 검증 실패는 `4108`을 사용한다. 허용된 인증서 운영 이벤트에는 정규화한 serial만 기록하며 ProductCode, 인증서·CSR 원문과 private key는 기록하지 않는다.
- 응답에는 상세 인증 실패 단계를 노출하지 않으며 Event Log에도 API key·HMAC·signature·nonce·session·SAS·pair root·원문 요청·계정 이름을 기록하지 않는다.
- HTTP.sys 또는 OS Pipe ACL이 애플리케이션에 전달하기 전에 차단한 요청은 이 애플리케이션 로그에서 관찰할 수 없으며, 필요하면 Windows 감사정책·HTTP.sys 진단을 별도로 운영한다.

## 4. Admin API

Admin 요청은 loopback 제한과 별도의 호출자 인증·인가를 모두 통과해야 한다.

### 4.1 `GET /admin/services`

등록된 목록과 인증서 상태를 페이지 단위로 반환한다. `includeDeleted=true`이면 톰스톤을 포함하며 기본값은 `false`다. 요청 형식은 `GET /admin/services?includeDeleted=false&pageSize=100&cursor=...`이고 첫 요청에서는 `cursor`를 생략한다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Services>
    <Service>
      <Name>VMS Bridge</Name>
      <ProductCode>ABCD</ProductCode>
      <ServiceHostName>vms-bridge.example.local</ServiceHostName>
      <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
      <Port>21500</Port>
      <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
      <Deleted>false</Deleted>
      <CertificateStatus>VALID</CertificateStatus>
      <CertificateSerialNumber>01A4...</CertificateSerialNumber>
      <CertificateNotAfterUtc>2027-07-19T02:00:00Z</CertificateNotAfterUtc>
    </Service>
  </Services>
  <TotalCount>324</TotalCount>
  <NextCursor>opaque-server-value</NextCursor>
</Response>
```

정렬은 정규화한 `ProductCode`의 Ordinal 오름차순으로 고정한다. `TotalCount`는 해당 페이지의 cursor revision과 `includeDeleted` 조건에 일치하는 전체 항목 수이며 0 이상 정수다. 현재 페이지 항목 수는 `Services`의 `Service` 자식 수로 계산하고 별도 `Count` 필드를 두지 않는다. `TotalCount`는 빈 목록과 마지막 페이지를 포함한 모든 성공 페이지에 반드시 반환한다. cursor는 서버가 생성한 opaque 값이며 목록 revision과 `includeDeleted` 조건을 결합한다. cursor의 revision이 현재 목록과 달라지거나 위조·만료되면 `1002 CONFLICT`로 처음부터 다시 조회하게 한다. 마지막 페이지에서는 `NextCursor`를 생략한다.

Admin DTO는 운영 UI에 필요한 값만 반환한다. 동기화 전용 `LogicalVersion`과 `OriginInstanceId`는 §5.1의 Sync DTO에서만 사용하고 Admin·외부 DTO에는 노출하지 않는다. `DeletedUtc`는 `Deleted=true`일 때만 포함하고 활성 항목에서는 생략한다.

### 4.2 `GET /admin/registration-mode`

ProductCode를 저장하지 않는 로컬 전역 등록 모드의 현재 상태를 반환한다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <RegistrationMode>
    <State>OPEN</State>
    <OpenedUtc>2026-07-19T02:00:00Z</OpenedUtc>
    <ExpiresUtc>2026-07-19T03:00:00Z</ExpiresUtc>
    <RemainingSeconds>3471</RemainingSeconds>
  </RegistrationMode>
  <LastRegistration>
    <CompletedUtc>2026-07-18T08:30:00Z</CompletedUtc>
    <ProductCode>ABCD</ProductCode>
    <ServiceHostName>vms-bridge.example.local</ServiceHostName>
    <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
    <CertificateSerialNumber>01A4...</CertificateSerialNumber>
    <CertificateNotAfterUtc>2027-07-18T08:30:00Z</CertificateNotAfterUtc>
    <Outcome>REGISTERED</Outcome>
  </LastRegistration>
</Response>
```

- `State`는 `CLOSED`, `OPEN`, `CLAIMED` 중 하나다.
- `OpenedUtc`, `ExpiresUtc`, `RemainingSeconds`는 `OPEN`에서만 존재한다. `RemainingSeconds`는 `0..3600`이며 monotonic elapsed time 기준 남은 시간을 내림하지 않고 안전하게 계산한다.
- `CLAIMED`는 첫 valid request가 발급 transaction을 수행 중이라는 뜻이며 시작·종료 명령을 거부한다.
- `LastRegistration`은 mode claim 뒤 마지막 완료 결과가 있을 때만 반환한다. `Outcome`은 `REGISTERED`, `REREGISTERED`, `FAILED` 중 하나다.
- 성공 결과에는 ProductCode, 필수 service hostname·IPv4 pair, certificate serial·만료를 포함한다. `FAILED`에는 안전한 일반 사유만 포함하고 인증서·CSR 원문, private key, API key와 내부 경로는 포함하지 않는다. claim 전 입력 거부를 이 필드에 노출하지 않는다.
- 설정 UI는 창이 표시된 동안 최대 5초 주기로 조회하고 숨겨지면 polling을 중단한다.

### 4.3 `POST /admin/registration-mode/open`

request body와 query는 없다. 로컬 운영자의 명시적 동작으로 ProductCode 제한 없는 전역 1시간·1건 창을 연다.

- `CLOSED`이면 `OPEN`으로 전이하고 `OpenedUtc`, 고정 `ExpiresUtc=OpenedUtc+1시간`, `RemainingSeconds=3600`을 반환한다.
- 이미 `OPEN`이면 현재 창을 idempotently 반환하고 만료를 연장하지 않는다.
- `CLAIMED`이면 `409`·`1002 CONFLICT`다.
- active CA issuer가 아니거나 CA private key·ledger·CRL state가 정상이 아니면 fail closed하고 창을 열지 않는다.
- ProductCode를 body·query·header로 받지 않는다. 알 수 없는 body·query는 `400`이다.
- 성공·거부는 actor SID와 함께 보안 감사한다. API key·CA private 정보는 기록하지 않는다.

### 4.4 `POST /admin/registration-mode/close`

request body와 query는 없다.

- `OPEN`이면 즉시 `CLOSED`로 전이한다.
- 이미 `CLOSED`이면 idempotent 성공이다.
- `CLAIMED`이면 발급 transaction을 취소하거나 중간 상태로 만들지 않고 `409`·`1002 CONFLICT`를 반환한다.
- 서비스 stop·restart는 별도 Admin 호출 없이도 창을 닫으며 다음 기동에서 복원하지 않는다.
- 성공·거부는 actor SID와 함께 보안 감사한다.

### 4.5 `DELETE /admin/services/{productCode}`

- 활성 항목을 물리적으로 제거하지 않고 `Deleted=true`와 `DeletedUtc`를 기록한다.
- 로컬 `InstanceId`를 `OriginInstanceId`로 기록한다.
- 해당 ProductCode의 모든 active certificate serial을 `CessationOfOperation` reason으로 revoke하고 CRL number를 증가시켜 새 CRL을 publish한다. `Unspecified`·`CertificateHold`·`RemoveFromCRL`로 대체하지 않는다.
- directory tombstone, certificate ledger revoke와 CRL publish는 하나의 복구 가능한 transaction이다. 어느 하나만 성공한 부분 삭제를 반환하지 않는다.
- 응답에는 폐기한 serial 수와 새 CRL number를 포함한다. private key·CSR·CA 내부 경로는 포함하지 않는다.
- 성공 후 즉시 동기화 사이클을 예약한다.
- 없거나 이미 삭제된 항목은 `1001 NOT_FOUND`다.

### 4.6 `GET /admin/sync`

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <SyncStatus>
    <Enabled>true</Enabled>
    <PairingState>Enabled</PairingState>
    <PeerEndpoint>https://10.0.0.2:21000</PeerEndpoint>
    <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
    <KeyEpoch>1</KeyEpoch>
    <LastSyncUtc>2026-07-17T02:00:00Z</LastSyncUtc>
    <LastResult>OK</LastResult>
    <ClockSkewSeconds>2</ClockSkewSeconds>
    <LastPeerNotificationOperation>RELEASE</LastPeerNotificationOperation>
    <LastPeerNotificationResult>CONFIRMED</LastPeerNotificationResult>
    <LastPeerNotificationUtc>2026-07-17T01:50:00Z</LastPeerNotificationUtc>
  </SyncStatus>
</Response>
```

`PairingState`의 허용값과 조건부 필드는 다음과 같다. 표에서 명시하지 않은 상태에는 해당 요소를 보내지 않는다.

| `PairingState` | 필수·허용 요소 |
|---|---|
| `Unpaired` | `Enabled=false`. peer·pairing 요소 없음 |
| `PairingWindowOpen` | `Enabled=false`, `PeerEndpoint`, `PairingExpiresUtc`, `PairingRemainingSeconds` |
| `Negotiating` | 위 요소와 `PairingId`; hello에서 확인한 뒤 `PeerInstanceId` 추가 |
| `SasPending` | `PeerEndpoint`, `PeerInstanceId`, `PairingId`, `PairingExpiresUtc`, `PairingRemainingSeconds`, `LocalConfirmed`, `RemoteConfirmed`. 로컬 confirm 전까지만 `Sas` 포함 |
| `BothConfirmed` | 위 식별자와 만료 요소, `LocalConfirmed=true`, `RemoteConfirmed=true`. `Sas`는 생략 |
| `PairedPendingCommit` | `PeerEndpoint`, `PeerInstanceId`, `KeyEpoch`, `PairingId`, `CommitExpiresUtc`, `LocalCommitConfirmed`, `RemoteCommitConfirmed` |
| `PairedDisabled` | `Enabled=false`, `PeerEndpoint`, `PeerInstanceId`, `KeyEpoch` |
| `Enabled` | `Enabled=true`, `PeerEndpoint`, `PeerInstanceId`, `KeyEpoch` |

- `Enabled`는 `PairingState=Enabled`일 때만 `true`다.
- `PairingId`, `PeerInstanceId`는 소문자 `D` GUID다. `KeyEpoch`는 durable `PairedPendingCommit`을 만든 뒤부터 `1..18446744073709551615`다.
- `Sas`는 정확히 8자리 십진수이며 `SasPending`에서 로컬 운영자가 확인하기 전까지만 반환한다. confirm 처리 직후 메모리와 다음 응답에서 제거하고 저장·로그·Peer 전송을 금지한다.
- `PairingExpiresUtc`는 UI 표시용 UTC 시각이고 실제 5분 timeout은 monotonic elapsed time으로 판정한다. `PairingRemainingSeconds`는 응답 생성 시점의 monotonic 잔여 초를 내림한 `0..300` 정수이며 countdown 판단의 단일 원본이다.
- `CommitExpiresUtc`는 durable `PairedPendingCommit`의 24시간 복구 기한이다.
- `LastResult`에는 성공한 마지막 사이클의 `OK`, 마지막 오류 코드 이름 또는 한 번도 sync를 시도하지 않은 `NOT_RUN`을 기록한다. `NOT_RUN`일 때 `LastSyncUtc`와 `ClockSkewSeconds`를 모두 생략한다. 그 밖의 성공·오류 결과에는 마지막 시도 시각인 `LastSyncUtc`를 반드시 포함한다. `ClockSkewSeconds`는 handshake에서 실제 시계 편차를 관찰한 경우에만 포함하며 연결·인증 전에 실패해 편차를 관찰하지 못한 경우에는 생략한다.
- `LastPeerNotificationOperation`은 `NONE`, `RELEASE`, `REVOKE`, `LastPeerNotificationResult`는 `NOT_RUN`, `CONFIRMED`, `UNCONFIRMED`, `NOT_REQUIRED` 중 하나다. 초기값은 `NONE`·`NOT_RUN`이고 이때 `LastPeerNotificationUtc`를 생략한다. 그 밖에는 마지막 로컬 처리 시각을 UTC로 포함한다. 이 세 값은 peer secret이 아니며 peer credential 삭제 뒤에도 남도록 `config.xml` 또는 동등한 durable 설정에 저장한다.
- 설정 UI는 동기화 화면이 보일 때 이 endpoint를 최대 5초 주기로 polling하고 숨겨지면 중단한다. 이전 요청이 끝나기 전에 다음 요청을 시작하지 않는다. `429`이면 해당 화면의 모든 polling을 멈추고 응답의 `Retry-After` 뒤 한 번만 재개하며 값이 없으면 5초 뒤 재시도한다.

페어링 중 예:

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <SyncStatus>
    <Enabled>false</Enabled>
    <PairingState>SasPending</PairingState>
    <PeerEndpoint>https://10.0.0.2:21000</PeerEndpoint>
    <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
    <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
    <PairingExpiresUtc>2026-07-17T02:05:00Z</PairingExpiresUtc>
    <PairingRemainingSeconds>247</PairingRemainingSeconds>
    <Sas>00427193</Sas>
    <LocalConfirmed>false</LocalConfirmed>
    <RemoteConfirmed>false</RemoteConfirmed>
    <LastResult>NOT_RUN</LastResult>
    <LastPeerNotificationOperation>NONE</LastPeerNotificationOperation>
    <LastPeerNotificationResult>NOT_RUN</LastPeerNotificationResult>
  </SyncStatus>
</Response>
```

### 4.7 `POST /admin/sync/enable`

요청:

```xml
<EnableSync xmlns="urn:deepai:service-directory:admin">
  <PeerEndpoint>https://10.0.0.2:21000</PeerEndpoint>
  <RePair>false</RePair>
</EnableSync>
```

- `Unpaired`에서 호출하면 지정한 endpoint만 허용하는 5분 페어링 창을 열고 `PairingWindowOpen`으로 전이한다. 운영자는 상대 서버에서도 해당 endpoint를 반대로 지정해 독립적으로 호출해야 한다.
- `PeerEndpoint`는 scheme `https`, canonical IPv4 literal host와 고정 포트 `21000`만 허용하며 IPv6, userinfo, DNS hostname, path, query, fragment를 금지한다. 요청 문자열 자체가 선후행 공백 없는 `https://{a.b.c.d}:21000` 형식이어야 하고 IPv4 octet에는 선행 0을 허용하지 않는다. 상대 leaf의 필수 DNS hostname SAN과 이 endpoint IPv4 SAN, 같은 site CA·pin 검증을 모두 통과해야 한다. leaf CRL Distribution Point에는 상대 path가 아니라 상대 Directory DNS·IPv4의 두 absolute HTTPS `/pki/crl` URI가 있어야 하며 현재 PeerEndpoint와 같은 IPv4 authority의 URI에서 받은 CRL을 signature·CRLNumber·시각까지 검증한다.
- `RePair=true`는 기존 관계를 먼저 비활성화하고 새 페어링을 시작한다. `config.xml`에 남긴 마지막 발급 epoch보다 1 큰 epoch를 예약하며 실패·취소·timeout 시 상태는 `Unpaired`가 되고 이전 root를 다시 사용하지 않는다.
- 성공적인 페어링 commit 뒤 양쪽은 `PairedDisabled`다. 이 상태에서 같은 endpoint로 다시 호출하면 로컬을 `Enabled`로 전이한다. 양쪽이 `Enabled`일 때만 일반 handshake와 exchange가 성공한다.
- 이미 `Enabled`인데 `RePair=false`인 요청, 상태와 endpoint가 맞지 않는 요청은 `1002 CONFLICT`다.

#### 4.7.1 `POST /admin/sync/pairing/confirm`

```xml
<PairingConfirmation xmlns="urn:deepai:service-directory:admin">
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
  <Confirmed>true</Confirmed>
</PairingConfirmation>
```

현재 `SasPending`의 `PairingId`와 정확히 일치하고 `Confirmed=true`인 요청만 허용한다. SAS 자체는 전송하지 않는다. 각 서버의 운영자가 두 화면의 8자리 SAS와 PairingId가 같은지 독립적으로 확인한 뒤 각 로컬 Admin endpoint를 호출한다. 성공 뒤 SAS를 즉시 제거하고 같은 PairingId의 동일 confirm 재요청은 멱등 성공으로 처리한다. 다른 ID, `Confirmed=false` 또는 충돌하는 상태는 `409 CONFLICT`다.

#### 4.7.2 `POST /admin/sync/pairing/cancel`

```xml
<PairingCancellation xmlns="urn:deepai:service-directory:admin">
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
</PairingCancellation>
```

현재 진행 중인 PairingId와 일치해야 한다. `BothConfirmed` 전에는 메모리 비밀을 지우고 `Unpaired`로 전이한다. `PairedPendingCommit`에서는 durable pending commit과 새 epoch를 재사용하지 않고 명시적 재페어링이 필요하도록 폐기하되 `LastPeerKeyEpoch`는 유지한다. 다른 ID나 취소할 상태가 아니면 `409 CONFLICT`다. 자세한 상태 전이와 암호 계약은 §5.3을 따른다.

### 4.8 `POST /admin/sync/disable`

요청 body는 항상 보낸다.

```xml
<DisableSync xmlns="urn:deepai:service-directory:admin">
  <ForgetPeer>false</ForgetPeer>
</DisableSync>
```

- 로컬 상태가 `Enabled`이면 아직 로컬을 비활성화하지 않은 채 기존 유효 session을 재사용하거나 새 인증 handshake로 session을 확보한 뒤 `POST /api/sync/release`를 통지한다. session 확보·release가 실패해도 네트워크 재시도를 무한 대기하지 않고 로컬 비활성화를 계속한다. 이미 `PairedDisabled`이면 release를 생략한다.
- 기본 요청은 pair root를 유지한 채 로컬을 `PairedDisabled`로 영속화한다. 피어는 release를 받으면 자신도 `PairedDisabled`로 전이한다.
- `ForgetPeer=true`는 가능한 경우 `POST /api/sync/revoke`를 상대에 보낸 뒤 로컬 `secrets/peer.dat`을 제거하고 `Unpaired`로 전이한다. revoke 실패 여부와 관계없이 로컬 폐기는 완료하되 원격 미확인을 상태 화면에 표시한다. `RePair=true`도 기존 root에 대해 같은 폐기 절차를 먼저 수행한다.
- `ForgetPeer=false`의 operation은 `RELEASE`, `true`는 `REVOKE`다. 인증된 성공 응답까지 확인하면 `CONFIRMED`, 통지가 필요했지만 timeout·연결·인증·응답 검증 중 실패하면 `UNCONFIRMED`, 이미 `PairedDisabled`라 release가 필요하지 않으면 `NOT_REQUIRED`다. `UNCONFIRMED`는 원격 실패를 뜻하지 않고 원격 상태를 확인하지 못했다는 뜻이다.
- 로컬 durable 상태 변경이 성공하면 원격 통지 결과와 무관하게 HTTP `200`, `Code=0`을 반환한다. 로컬 저장이 실패하면 현재 상태를 유지하고 `500`, `3000 INTERNAL`이다.

응답:

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <SyncDisableResult>
    <LocalPairingState>PairedDisabled</LocalPairingState>
    <PeerNotificationOperation>RELEASE</PeerNotificationOperation>
    <PeerNotificationResult>CONFIRMED</PeerNotificationResult>
    <PeerNotificationUtc>2026-07-17T02:00:00Z</PeerNotificationUtc>
  </SyncDisableResult>
</Response>
```

`LocalPairingState`는 성공 뒤 `PairedDisabled` 또는 `Unpaired`다. 응답의 notification 값은 같은 작업에서 §4.6의 `LastPeerNotification*` 값으로 내구적으로 저장한다.

### 4.9 `POST /admin/sync/now`

활성 상태에서 핸드셰이크부터 전체 사이클을 한 번 수행한다. 피어별 활성 sync 세션은 하나뿐이며 이미 자동·수동 sync가 실행 중이면 병행하거나 기존 작업을 공유하지 않고 `1002 CONFLICT`를 반환한다. 이 엔드포인트에는 공통 변경 제한과 별도로 `2회/분` 제한을 적용한다.

### 4.10 `GET /admin/settings/logging`

현재 시스템 파일 로그 보존기간을 일 단위로 반환한다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <LoggingSettings>
    <LogRetentionDays>30</LogRetentionDays>
  </LoggingSettings>
</Response>
```

설치 기본값은 `30`일이며 예제도 기본 설정값을 나타낸다.

### 4.11 `PUT /admin/settings/logging`

요청:

```xml
<LoggingSettings xmlns="urn:deepai:service-directory:admin">
  <LogRetentionDays>30</LogRetentionDays>
</LoggingSettings>
```

처리 규칙:

- `LogRetentionDays`는 `1..1095` 범위의 정수여야 한다. `1095`일을 최대 3년으로 정의한다.
- 범위 밖 값, 정수가 아닌 값과 overflow는 `1000 BAD_REQUEST`다.
- 유효한 값은 `config.xml`에 원자적으로 영속화한 뒤 즉시 보존 정리를 실행한다.
- 오늘을 포함해 최근 `LogRetentionDays`개의 시스템 로컬 날짜 파일을 보존한다.
- 정리 대상은 `%ProgramData%\DEEPAi\ServiceDirectory\logs\system\` 바로 아래에서 `dpai-sd_yyyy-MM-dd.log`와 정확히 일치하는 파일뿐이다.
- 설정 저장 뒤 정리가 실패하면 설정값은 유지하고 `3000 INTERNAL`을 반환한다. 같은 PUT을 재시도하면 저장된 값으로 정리를 다시 시도할 수 있다.
- 성공 응답은 GET과 같은 `LoggingSettings` payload를 반환한다.

파일명과 레코드 시각, 이벤트 목록의 단일 원본은 [개발계획의 시스템 로그 정책](./03-development.md#9-시스템-로그-정책)이다.

### 4.12 `GET /admin/ca/status`

현재 site CA와 issuer 상태를 반환한다. query와 body는 없다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <CaStatus>
    <State>READY</State>
    <Role>ACTIVE_ISSUER</Role>
    <SiteId>4ed36c2a-84d0-4fdb-94ef-8e25a8ee0da1</SiteId>
    <IssuerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</IssuerInstanceId>
    <CaSerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</CaSerialNumber>
    <CaSpkiSha256>base64-sha256</CaSpkiSha256>
    <NotBeforeUtc>2026-07-19T02:00:00Z</NotBeforeUtc>
    <NotAfterUtc>2046-07-19T02:05:00Z</NotAfterUtc>
    <PkiRevision>43</PkiRevision>
    <CrlNumber>19</CrlNumber>
    <LastBackupUtc>2026-07-19T03:00:00Z</LastBackupUtc>
  </CaStatus>
</Response>
```

- `State`는 `NOT_PROVISIONED`, `BACKUP_REQUIRED`, `READY`다. `NOT_PROVISIONED`는 PKI를 한 번도 만든 적 없는 설치 전환 경계에서만 사용한다. CA certificate·DPAPI key·ledger·CRL 중 하나라도 일부 존재하거나 backup artifact가 있는데 primary가 없거나 검증되지 않으면 상태 API로 축소해 계속 기동하지 않고 서비스 기동 자체를 fail closed한다.
- `Role`은 `ACTIVE_ISSUER`, `STANDBY`다. `STANDBY`는 조회·CRL 제공만 가능하고 발급·폐기·등록 모드 open을 거부한다.
- `CaSpkiSha256`은 32바이트 SHA-256의 canonical base64다. CA certificate·private key·내부 경로는 반환하지 않는다.
- 최초 승인 backup 완료 전에는 `BACKUP_REQUIRED`이며 등록 모드 open·발급·폐기를 거부한다. backup 성공과 상태 영속화가 모두 완료된 뒤 `READY`다.

### 4.13 `POST /admin/ca/backup`

운영자 암호로 전체 복구에 필요한 CA certificate·private key, PKI metadata, full certificate ledger와 현재 signed CRL을 하나의 encrypted backup으로 만든다. query는 없다.

```xml
<CreateCaBackup xmlns="urn:deepai:service-directory:admin">
  <Password>operator-supplied-passphrase</Password>
</CreateCaBackup>
```

- `Password`는 trim하거나 정규화하지 않는 12..128 Unicode scalar, strict UTF-8 최대 512바이트다. NUL과 XML control character를 금지하고 로그·응답·설정·명령행에 기록하지 않는다.
- backup container는 PBKDF2-HMAC-SHA256의 무작위 16바이트 salt와 600,000회 반복으로 서로 다른 AES-256-CBC encryption key와 HMAC-SHA256 authentication key를 파생한다. 무작위 16바이트 IV, PKCS#7 padding과 encrypt-then-MAC을 사용하고 header·salt·IV·ciphertext 전체를 MAC에 포함한다. 복원은 MAC을 고정 시간 비교로 검증하기 전 plaintext를 해석하지 않는다.
- 서비스는 `%ProgramData%\DEEPAi\ServiceDirectory\backups\ca\` 밖의 caller path를 받지 않는다. 임시 파일에도 plaintext를 쓰지 않고 encrypted bytes만 write-through 원자 생성한다.
- backup snapshot은 같은 mutation gate 안에서 일관된 PKI revision·CRL number를 고정하되, 오래 걸리는 KDF·암호화·파일 쓰기는 snapshot 복사 뒤 gate 밖에서 수행한다. 마지막 상태 commit에서 snapshot이 현재 state와 같은지 다시 확인하고 `LastBackupUtc`를 영속화한다. 달라졌으면 생성 파일을 승인 backup으로 표시하지 않고 `409 CONFLICT`로 재시도한다.
- 성공 응답은 다음과 같으며 절대 경로를 포함하지 않는다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <CaBackup>
    <FileName>site-ca-4ed36c2a-84d0-4fdb-94ef-8e25a8ee0da1-20260719T030000000Z.dpca</FileName>
    <CreatedUtc>2026-07-19T03:00:00Z</CreatedUtc>
    <Sha256>base64-sha256</Sha256>
  </CaBackup>
</Response>
```

### 4.14 `GET /admin/certificates`

full certificate ledger를 serial의 Ordinal 오름차순으로 조회한다. `GET /admin/certificates?pageSize=100&cursor=...` 형식을 사용하며 공통 pageSize·opaque cursor·16 KiB 응답 제한을 적용한다.

```xml
<Response xmlns="urn:deepai:service-directory:admin">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Certificates>
    <Certificate>
      <SerialNumber>01A4B5C6D7E8F90123456789ABCDEF01</SerialNumber>
      <ProductCode>ABCD</ProductCode>
      <IssuanceKind>REGISTRATION</IssuanceKind>
      <ServiceHostName>vms-bridge.example.local</ServiceHostName>
      <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
      <Status>CURRENT</Status>
      <IssuedUtc>2026-07-19T02:00:00Z</IssuedUtc>
      <NotBeforeUtc>2026-07-19T01:55:00Z</NotBeforeUtc>
      <NotAfterUtc>2027-07-19T02:00:00Z</NotAfterUtc>
      <LeafSha256>base64-sha256</LeafSha256>
    </Certificate>
  </Certificates>
  <TotalCount>324</TotalCount>
  <NextCursor>opaque-server-value</NextCursor>
</Response>
```

- `IssuanceKind`는 `REGISTRATION`, `RENEWAL`, `Status`는 `CURRENT`, `RETIRING`, `REVOKED`다.
- `ScheduledRevocationUtc`는 `RETIRING` 또는 갱신 overlap을 거쳐 폐기된 항목에만, `RevokedUtc`·`RevocationReason`은 `REVOKED`에만 포함한다.
- `RevocationReason`은 `KEY_COMPROMISE`, `CA_COMPROMISE`, `AFFILIATION_CHANGED`, `SUPERSEDED`, `CESSATION_OF_OPERATION`, `PRIVILEGE_WITHDRAWN`, `AA_COMPROMISE`다. `UNSPECIFIED`, `CERTIFICATE_HOLD`, `REMOVE_FROM_CRL`은 반환하거나 저장하지 않는다.
- CSR·request payload·private key와 내부 저장 경로는 반환하지 않는다.

### 4.15 `POST /admin/certificates/{serial}/revoke`

한 certificate serial을 운영자가 명시적으로 폐기한다. `{serial}`은 정확히 32자 uppercase hexadecimal canonical positive serial이다. query는 없다.

```xml
<RevokeCertificate xmlns="urn:deepai:service-directory:admin">
  <Reason>KEY_COMPROMISE</Reason>
</RevokeCertificate>
```

- 허용 reason은 §4.14의 값 중 `SUPERSEDED`, `CESSATION_OF_OPERATION`을 제외한 운영자 사유다. 이 두 값은 갱신·재등록과 서비스 삭제의 자동 처리만 사용한다.
- 현재 `CURRENT` 또는 `RETIRING` 항목만 폐기할 수 있다. 없는 serial은 `404 NOT_FOUND`, 이미 `REVOKED`이면 같은 reason의 exact retry만 현재 결과를 반환하고 다른 reason은 `409 CONFLICT`다.
- active issuer와 `READY` 상태에서만 수행한다. ledger entry revoke, `PkiRevision+1`, `CrlNumber+1`과 새 signed DER CRL publish를 하나의 복구 transaction으로 commit한다. unsigned wrap, CA key·DPAPI·ACL·CRL 검증 또는 저장 실패에서는 부분 상태를 게시하지 않는다.
- 명시적 serial 폐기는 service directory record를 자동 삭제하지 않는다. 폐기한 인증서가 해당 ProductCode의 `CURRENT`이면 서비스 조회는 인증서가 폐기된 상태임을 표시하고 운영자가 재등록하거나 서비스를 삭제해야 한다.
- 성공 응답은 폐기 serial, `RevokedUtc`, reason과 새 `PkiRevision`·`CrlNumber`를 반환한다.

### 4.16 CA 복원과 rotation 경계

- CA restore Admin endpoint는 없다. installer repair만 메인·와치독 서비스를 중지하고 operator가 선택한 `.dpca`와 암호를 maintenance process의 표준 입력으로 전달한다. 암호를 installer parameter·process command line·환경 변수·setup log·임시 파일에 넣지 않는다.
- repair는 container MAC, CA profile·private key 일치, SiteId, issuer identity, full ledger, CRL signature와 backup 내부 `PkiRevision`·`CrlNumber` 일관성을 모두 검증한다. 설치 state가 정상 판독되면 현재 high-water보다 낮은 backup을 거부한다. 설치 state가 손상되어 판독할 수 없으면 operator가 명시적으로 선택한 인증된 backup을 복구 기준으로 사용하되, 읽을 수 있는 모든 기존 target bytes를 journal before image로 먼저 고정한다. 그 뒤 CA key·certificate·metadata·ledger·CRL을 한 recovery journal transaction으로 복원한다. restore transaction 자체가 실패하면 journal rollback으로 기존 bytes를 보존하고 서비스를 중지 상태로 둔다. restore commit 뒤 별도 서비스 재기동이 실패한 경우에는 인증이 끝난 복원 상태를 다시 이전 손상 상태로 되돌리지 않고 서비스를 중지 상태로 유지해 운영자가 원인을 확인하게 한다.
- CA key rotation·dual-pin 배포와 관련 Admin endpoint는 이번 릴리스 범위가 아니다. restore는 backup에 있던 같은 CA state를 복구하는 작업이며 새 CA 생성이나 rotation으로 사용하지 않는다.

## 5. 피어 동기화 데이터

### 5.1 내부 동기화 레코드

```xml
<Service xmlns="urn:deepai:service-directory:peer">
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServiceHostName>vms-bridge.example.local</ServiceHostName>
  <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
  <Port>21500</Port>
  <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
  <Deleted>false</Deleted>
  <LogicalVersion>42</LogicalVersion>
  <OriginInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</OriginInstanceId>
</Service>
```

| 필드 | 규칙 |
|---|---|
| `Name` | trim 후 1~128 Unicode scalar, UTF-8 최대 512바이트, 제어문자와 XML 1.0에서 기록할 수 없는 `U+FFFE`·`U+FFFF` 금지 |
| `ProductCode` | trim 후 `ToUpperInvariant()`, `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII, `OrdinalIgnoreCase` 유일 키 |
| `ServiceHostName` | 필수 canonical lowercase ASCII DNS hostname/FQDN 한 개. 상세 문법은 [개발계획 §5.1](./03-development.md#51-도메인-레코드) 적용 |
| `ServiceIpv4Address` | 필수 canonical dotted-decimal IPv4 한 개. IPv6와 source-IP 대체 금지. 상세 문법은 개발계획 §5.1 적용 |
| `Port` | 정수 `1..65535` |
| `LastModifiedUtc` | 활성 변경의 UTC 표시·감사 시각. 병합 비교에는 사용하지 않음 |
| `DeletedUtc` | 톰스톤 삭제의 UTC 표시·감사 시각. `Deleted=true`일 때 필수이고 `false`일 때 요소를 생략 |
| `LogicalVersion` | `1..18446744073709551615`의 unsigned 64-bit 정수. 병합 revision의 첫 번째 비교값 |
| `OriginInstanceId` | 이 값을 마지막으로 생성·수정·삭제한 설치 인스턴스 |

Peer `Service`는 외부 등록 원문이 아니라 정규화가 끝난 내부 snapshot의 canonical wire 값이다. `Name`, `ProductCode`, `ServiceHostName`, `ServiceIpv4Address` 원문은 공통 도메인 검증 결과와 `Ordinal`로 정확히 같아야 하며 trim·대소문자화 등으로 값이 달라지는 비정규 payload는 수신 중 조용히 고치지 않고 `1000 BAD_REQUEST`로 거부한다.

이 두 service 필드는 등록 외부 앱이 선택·영속화한 값이다. 피어 송신자의 Directory hostname·IPv4, TCP source IP와 DNS 역조회로 누락값을 채우거나 수신값을 바꾸지 않으며 둘 중 하나라도 누락되면 전체 payload를 거부한다.

레코드에 논리 버전과 변경 출처를 저장하지 않고 현재 송신자의 `InstanceId`를 대신 사용하면 재전송 경로에 따라 결과가 바뀔 수 있으므로 금지한다.

### 5.2 동기화 실행 시점

1. 신규 등록·재등록·삭제 또는 인증서 폐기 commit 직후
2. 10분 주기
3. 서비스 기동 시 1회
4. 관리자의 수동 실행

모든 사이클은 핸드셰이크부터 시작한다. 즉시 동기화 실패는 10분 주기와 수동 실행으로 보정한다.

### 5.3 최초 페어링과 key epoch

최초 페어링은 HTTPS server certificate로 사이트 전송 경계를 검증하되 그 인증서나 AD identity만으로 상대 Directory 운영자를 확정하지 않는다. ECDH와 양쪽 운영자의 SAS 확인을 추가로 사용하는 다음 상태 머신을 따른다.

```text
Unpaired → PairingWindowOpen → Negotiating → SasPending → BothConfirmed
         → PairedPendingCommit → PairedDisabled → Enabled
```

- `POST /admin/sync/enable`로 양쪽 서버에 각각 5분 페어링 창을 연다. 창은 시스템 로컬 벽시계 변경에 영향받지 않는 monotonic elapsed time으로 만료시키며 지정한 상대 endpoint에서 온 요청만 받는다.
- `POST /api/sync/pairing/hello`의 initiator가 CSPRNG로 128-bit `PairingId`, 256-bit nonce와 일회성 ECDH P-256 키 쌍을 만들고, responder도 별도의 256-bit nonce와 일회성 키 쌍을 만든다. 양쪽은 자기 `LastPeerKeyEpoch`도 hello에 포함한다. 공개키 wire 형식은 Windows CNG `BCRYPT_ECCPUBLIC_BLOB`인 `CngKeyBlobFormat.EccPublicBlob`로 고정한다. P-256은 8바이트 header와 32바이트 X·Y 좌표를 합한 정확히 72바이트여야 하며 ECDH P-256 public magic, key length와 곡선 위 점을 검증한다.
- 양쪽이 동시에 hello를 시작하면 정규화한 `InstanceId`의 Ordinal 값이 작은 쪽만 initiator를 유지하고 큰 쪽은 자기 outbound 시도를 취소한 뒤 responder가 된다. 두 `InstanceId`가 같으면 복제된 설치로 보고 페어링을 거부한다. 서버별 진행 중인 pairing은 하나만 허용하고 열린 5분 창에서 hello는 최대 3회만 받는다.
- 새 `KeyEpoch` 후보는 `max(initiator LastPeerKeyEpoch, responder LastPeerKeyEpoch) + 1`이다. 어느 쪽 값이 unsigned 64-bit 최댓값이면 페어링을 거부한다. transcript는 아래 값을 정확한 순서로 놓고 각 값을 `UInt32BE(byteLength) || value`로 연결한 뒤 전체를 `SHA-256`으로 해시한다.

  1. 알고리즘 식별자 ASCII `DPAI-SD-ECDH-P256-HMAC-SHA256-v1`
  2. `PairingId`의 소문자 `D` GUID ASCII
  3. initiator `InstanceId`의 소문자 `D` GUID ASCII
  4. responder `InstanceId`의 소문자 `D` GUID ASCII
  5. initiator canonical endpoint ASCII
  6. responder canonical endpoint ASCII
  7. initiator nonce raw 32바이트
  8. responder nonce raw 32바이트
  9. initiator CNG public blob raw 72바이트
  10. responder CNG public blob raw 72바이트
  11. initiator `LastPeerKeyEpoch`의 앞자리 0 없는 unsigned decimal ASCII
  12. responder `LastPeerKeyEpoch`의 앞자리 0 없는 unsigned decimal ASCII
  13. 새 `KeyEpoch`의 앞자리 0 없는 unsigned decimal ASCII

  GUID를 .NET `Guid.ToByteArray()`의 mixed-endian 16바이트로 넣거나 epoch를 machine-endian 정수로 넣지 않는다. `UInt32BE`는 4바이트 unsigned big-endian이고 빈 값은 허용하지 않는다.
- transcript의 endpoint는 `https://{canonical-ipv4}:21000` ASCII 형식으로 고정하고 trailing slash를 넣지 않는다. IPv4는 선행 0 없는 canonical dotted decimal이며 IPv6와 DNS 이름은 허용하지 않는다.
- 양쪽은 `ECDiffieHellmanCng`의 hash KDF를 SHA-256으로 고정하고 `SecretPrepend=ASCII("DPAI-SD-PAIR-K0-v1")`, `SecretAppend=TranscriptHash`로 설정한 `DeriveKeyMaterial(peerPublicKey)` 32바이트 결과를 `K0`로 사용한다. `HMAC-SHA256(K0, purpose-label || TranscriptHash)`로 `pair-confirm-initiator-v1`, `pair-confirm-responder-v1`, `pair-sas-v1`, `pair-root-v1` 목적별 키를 분리하며 한 목적의 MAC이나 키를 다른 목적으로 재사용하지 않는다.
- 양쪽은 `POST /api/sync/pairing/key-confirm`에서 각 방향 confirmation key로 transcript hash를 MAC해 실제로 같은 ECDH secret을 가진 것을 확인한다. MAC은 고정 시간으로 비교하며 확인이 끝나기 전에는 SAS를 신뢰하거나 표시하지 않는다.
- SAS는 정확히 8자리 십진수다. `K_sas=HMAC-SHA256(K0, ASCII("pair-sas-v1") || TranscriptHash)`로 만들고, counter 0부터 `HMAC-SHA256(K_sas, ASCII("sas-digits-v1") || TranscriptHash || UInt32BE(counter))`의 첫 4바이트를 unsigned big-endian 정수로 읽는다. 값이 `4,200,000,000` 이상이면 counter를 증가시켜 다시 계산하고, 미만인 첫 값을 `100,000,000`으로 나눈 나머지를 선행 0 포함 8자리로 표시한다. 두 서버가 로컬에서 독립적으로 계산하며 SAS 값을 네트워크로 보내지 않는다. 양쪽 운영자는 두 화면의 SAS와 PairingId가 같은지 직접 확인하고 각 서버의 `POST /admin/sync/pairing/confirm`을 별도로 실행한다.
- 로컬·원격 확인 결정은 `POST /api/sync/pairing/decision`으로 교환하고 두 확인을 모두 검증한 뒤에만 `BothConfirmed`로 전이한다. §5.3.3의 별도 방향별 decision key와 canonical MAC을 사용하며 key-confirm key를 재사용하지 않는다.
- 양쪽 SAS 확인이 끝나면 pair root와 `PairingId`, transcript hash, peer binding, 새 key epoch를 DPAPI로 보호한 `PairedPendingCommit` 레코드로 먼저 원자 저장하고 `config.xml`의 `LastPeerKeyEpoch`도 같은 복구 작업에서 해당 epoch로 증가시킨다. 이 시점 뒤 실패·취소가 나도 epoch를 되돌리거나 재사용하지 않는다. 그 뒤 `POST /api/sync/pairing/commit`을 §5.3.4의 pair root 기반 방향별 commit key로 인증한 멱등 요청·응답으로 교환한다. 양쪽 commit 확인을 저장한 뒤에만 `PairedDisabled`로 전이하며 어느 단계에서도 sync를 허용하지 않는다.
- `BothConfirmed` 전의 취소, 검증 실패, 재시작 또는 5분 timeout은 비밀 메모리를 지우고 `Unpaired`로 돌아간다. `PairedPendingCommit`은 재시작 뒤 같은 PairingId로 commit/status만 재시도할 수 있도록 최대 24시간 보존하고, 한쪽만 완료된 상태에서는 sync를 계속 금지한다. 24시간 안에 양쪽 commit을 확인하지 못하면 운영자에게 불일치 상태를 표시하고 명시적 취소·재페어링을 요구한다. 가능한 경우 인증된 abort를 상대에게 통지한다.
- `KeyEpoch`는 `1..18446744073709551615` 범위의 unsigned 64-bit 정수다. 양쪽이 합의한 후보는 §5.3의 `PairedPendingCommit` 원자 저장 시 발급한 것으로 확정한다. 페어링 자격 증명을 폐기해도 `LastPeerKeyEpoch`는 삭제하거나 감소시키지 않는다. 최댓값에 도달하면 새 페어링을 거부하고 새 설치 `InstanceId`를 만드는 명시적 복구 절차를 요구한다. 새 commit 뒤 이전 root·epoch로 서명한 요청은 모두 거부한다.
- 저장된 peer endpoint·`InstanceId`가 변경됐거나, 설치 복제·pair root 유출이 의심되거나, DPAPI 복호화·ACL 검증이 실패하면 기존 관계로 자동 fallback하지 않고 명시적 폐기와 재페어링을 요구한다.

#### 5.3.1 `POST /api/sync/pairing/hello`

공유 키가 생기기 전 허용되는 유일한 unsigned Peer 요청이다. 요청:

```xml
<PairingHello xmlns="urn:deepai:service-directory:peer">
  <Algorithm>DPAI-SD-ECDH-P256-HMAC-SHA256-v1</Algorithm>
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
  <InitiatorInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InitiatorInstanceId>
  <InitiatorEndpoint>https://10.0.0.1:21000</InitiatorEndpoint>
  <InitiatorNonce>n5aWEXmXWhyvzF3dZRo1LNznLt7ejVqVXacfIZ4NOXU=</InitiatorNonce>
  <InitiatorPublicKey>RUNLMSAAAABloK4ciP/vr+gXtzQEXMzQMgBHfkLjiUjjQm6GAvx3g0ENy+mRPUqArh8G9gdeMcF61lPU8RT5kg4QRRR0JBqE</InitiatorPublicKey>
  <InitiatorLastPeerKeyEpoch>0</InitiatorLastPeerKeyEpoch>
</PairingHello>
```

성공 응답:

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <PairingHelloResult>
    <Algorithm>DPAI-SD-ECDH-P256-HMAC-SHA256-v1</Algorithm>
    <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
    <ResponderInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</ResponderInstanceId>
    <ResponderEndpoint>https://10.0.0.2:21000</ResponderEndpoint>
    <ResponderNonce>ckug0OyiP9CPW2jyqsX/HqP+riS7PLztr9FzXo5jQho=</ResponderNonce>
    <ResponderPublicKey>RUNLMSAAAABvkt9l4qYtS/z18CZhZJe8+hghxECZTXSFzkgMBqt3JKykJ2tOI2NyVkkWpmxKD5gRp25CcZF3huKwSbf35Gda</ResponderPublicKey>
    <ResponderLastPeerKeyEpoch>0</ResponderLastPeerKeyEpoch>
    <KeyEpoch>1</KeyEpoch>
  </PairingHelloResult>
</Response>
```

Base64 nonce는 디코딩 후 정확히 32바이트, public key는 정확히 72바이트여야 한다. `Algorithm`은 위 고정 문자열만 허용하며 알고리즘 협상이나 fallback을 하지 않는다. 열린 5분 창, 지정 endpoint, 동시 hello 규칙을 먼저 확인하고 응답 필드까지 확보한 뒤 §5.3 transcript를 만든다.

#### 5.3.2 `POST /api/sync/pairing/key-confirm`

요청과 성공 응답은 각각 송신자 역할의 confirmation key로 `TranscriptHash` raw 32바이트를 HMAC한 값을 포함한다.

```xml
<PairingKeyConfirm xmlns="urn:deepai:service-directory:peer">
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
  <KeyEpoch>1</KeyEpoch>
  <SenderRole>initiator</SenderRole>
  <SenderInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</SenderInstanceId>
  <ReceiverInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</ReceiverInstanceId>
  <TranscriptHash>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TranscriptHash>
  <ConfirmationMac>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</ConfirmationMac>
</PairingKeyConfirm>
```

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <PairingKeyConfirmResult>
    <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
    <KeyEpoch>1</KeyEpoch>
    <SenderRole>responder</SenderRole>
    <SenderInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</SenderInstanceId>
    <ReceiverInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</ReceiverInstanceId>
    <TranscriptHash>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TranscriptHash>
    <ConfirmationMac>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</ConfirmationMac>
  </PairingKeyConfirmResult>
</Response>
```

역할은 소문자 `initiator` 또는 `responder`이고 identity·PairingId·epoch·transcript가 현재 메모리 상태와 정확히 일치해야 한다. confirmation MAC은 §5.3에 정의한 역할별 key로 transcript hash만 HMAC한다. 양쪽 값을 고정 시간으로 검증하기 전에는 SAS를 계산·표시하지 않는다.

#### 5.3.3 `POST /api/sync/pairing/decision`

```xml
<PairingDecision xmlns="urn:deepai:service-directory:peer">
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
  <KeyEpoch>1</KeyEpoch>
  <SenderRole>initiator</SenderRole>
  <SenderInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</SenderInstanceId>
  <ReceiverInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</ReceiverInstanceId>
  <TranscriptHash>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TranscriptHash>
  <Decision>CONFIRMED</Decision>
</PairingDecision>
```

요청과 응답에는 정확히 하나의 `X-DPAI-Pairing-MAC` 헤더를 보내며 값은 32바이트 HMAC-SHA256의 padding 포함 44자 Base64다. initiator request·response 송신 key는 `HMAC-SHA256(K0, ASCII("pair-decision-initiator-v1") || TranscriptHash)`, responder key는 label `pair-decision-responder-v1`을 사용한다.

decision request MAC 입력은 다음 값을 §5.3과 같은 4바이트 length-prefix 방식으로 연결한다.

1. ASCII `DPAI-SD-PAIR-DECISION-REQUEST-v1`
2. transcript hash raw 32바이트
3. PairingId 소문자 `D` GUID ASCII
4. KeyEpoch unsigned decimal ASCII
5. sender role ASCII `initiator` 또는 `responder`
6. sender InstanceId 소문자 `D` GUID ASCII
7. receiver InstanceId 소문자 `D` GUID ASCII
8. decision ASCII `CONFIRMED` 또는 `CANCELLED`

decision response MAC 입력 순서는 다음과 같다.

1. ASCII `DPAI-SD-PAIR-DECISION-RESPONSE-v1`
2. transcript hash raw 32바이트
3. PairingId ASCII
4. KeyEpoch decimal ASCII
5. 응답 sender role ASCII
6. 응답 sender InstanceId ASCII
7. 응답 receiver InstanceId ASCII
8. 디코딩한 request MAC 32바이트의 SHA-256 raw 32바이트
9. HTTP status의 앞자리 0 없는 decimal ASCII
10. response `Result` ASCII
11. response `Code`의 앞자리 0 없는 decimal ASCII
12. `X-DPAI-Pairing-MAC`을 제외한 raw response body의 SHA-256 raw 32바이트

초기 pairing decision에는 벽시계 timestamp를 사용하지 않는다. CSPRNG 128-bit PairingId, 정확한 peer·transcript·epoch binding, monotonic 5분 window와 역할별 단 하나의 terminal decision이 freshness·replay 경계다. 같은 역할의 동일 request bytes와 MAC 재전송은 같은 서명 응답으로 멱등 처리하고, 다른 두 번째 decision이나 만료·상태 불일치는 전체 pairing을 거부·취소한다.

#### 5.3.4 `POST /api/sync/pairing/commit`

```xml
<PairingCommit xmlns="urn:deepai:service-directory:peer">
  <PairingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PairingId>
  <KeyEpoch>1</KeyEpoch>
  <SenderRole>initiator</SenderRole>
  <SenderInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</SenderInstanceId>
  <ReceiverInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</ReceiverInstanceId>
  <TranscriptHash>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</TranscriptHash>
  <Commit>COMMIT</Commit>
</PairingCommit>
```

`X-DPAI-Pairing-MAC` 형식은 decision과 같다. initiator 송신 key는 `HMAC-SHA256(pairRoot, ASCII("pair-commit-initiator-v1") || TranscriptHash)`, responder key는 label `pair-commit-responder-v1`을 사용한다. commit request MAC 입력은 decision request의 fixed 문자열을 ASCII `DPAI-SD-PAIR-COMMIT-REQUEST-v1`으로, 마지막 값을 ASCII `COMMIT`으로 바꾼 나머지 동일 순서다. commit response MAC도 decision response와 동일 순서에서 fixed 문자열만 ASCII `DPAI-SD-PAIR-COMMIT-RESPONSE-v1`으로 바꾼다.

durable `PairedPendingCommit`의 24시간 deadline, PairingId, transcript, epoch와 local·remote commit flags가 freshness·replay 경계다. local·remote flag와 해당 성공 response bytes를 pair root·binding과 같은 복구 단위에 원자 저장한다. 같은 request의 재전송은 재시작 뒤에도 저장한 signed response로 멱등 처리한다. 다른 transcript·epoch·identity·marker 또는 기한 만료 요청은 거부하고 이전 root·epoch로 fallback하지 않는다.

pair root, `KeyEpoch`, transcript hash, 양쪽 `InstanceId`·endpoint binding, pairing state와 local·remote commit 확인 여부는 `%ProgramData%\DEEPAi\ServiceDirectory\secrets\peer.dat`에 DPAPI `LocalMachine` 범위로 암호화해 원자적으로 저장한다. 파일 ACL은 메인 서비스 SID, `SYSTEM`, 로컬 `Administrators`만 읽고 쓸 수 있게 한다. `LocalMachine` 보호는 파일을 읽을 수 있는 로컬 고권한 주체까지 막아 주지 않으므로 이 ACL을 완화하지 않는다. ECDH private key, 공유값, `K0`, 목적별 임시 키와 SAS는 영속화하지 않고 해당 단계가 끝나거나 실패하면 메모리에서 제거한다. SAS 암호 primitive는 clear 가능한 8자 버퍼를 반환하며 호출자는 표시 직후 지운다. WPF 등 관리 UI가 내부적으로 복사한 문자열은 확정적 zeroing이 보장되지 않으므로 표시 수명을 최소화하고 control·binding 참조를 즉시 해제하며 저장·로그·네트워크 전송을 금지한다. pair root 원문은 API·UI·로그에 노출하지 않는다.

### 5.4 Peer 메시지 인증과 sync 세션

`/api/sync/pairing/hello`는 공유 키가 생기기 전 허용되는 유일한 unsigned Peer 메시지이며 열린 5분 창과 정확한 endpoint로 제한한다. ECDH 이후의 pairing 메시지는 §5.3의 목적별 MAC을 사용하고, `/api/sync/handshake`, `/api/sync/exchange`, `/api/sync/release`, `/api/sync/revoke`의 모든 요청과 성공·오류 응답은 HMAC-SHA256으로 인증한다.

일반 sync 요청은 다음 헤더를 가진다.

| 헤더 | 규칙 |
|---|---|
| `X-DPAI-Instance-Id` | 정규화된 송신자 GUID |
| `X-DPAI-Key-Epoch` | 저장된 양의 정수 epoch |
| `X-DPAI-Session-Id` | handshake·revoke 요청에서는 헤더를 생략하고 exchange·release에서는 발급된 session ID |
| `X-DPAI-Timestamp` | UTC `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` 시각 |
| `X-DPAI-Nonce` | 요청마다 새 CSPRNG 128-bit 값의 Base64 |
| `X-DPAI-Signature` | 아래 canonical bytes의 HMAC-SHA256 Base64 |

canonical request bytes는 다음 값을 고정 순서로 놓고 각각 `4-byte big-endian length || value`로 인코딩한다.

1. 방향 문자열 `request`
2. sender `InstanceId`
3. receiver `InstanceId`
4. key epoch
5. session ID 헤더와 동일한 canonical Base64 ASCII. 디코딩한 raw 16바이트가 아니다. handshake·revoke 요청은 길이 0
6. 대문자 HTTP method
7. 정규화된 path와 query
8. 소문자로 정규화한 content type
9. 수신한 raw body bytes의 SHA-256 32바이트
10. timestamp
11. nonce 16바이트

query는 UTF-8 RFC 3986 percent-encoding을 사용해 이름·값 순으로 정렬하고 중복 값도 보존한다. GUID는 소문자 `D`, key epoch는 앞자리 0이 없는 invariant decimal, 시각은 위 고정 형식으로 canonicalize한다. `Content-Encoding`은 지원하지 않으며 body hash는 XML 재직렬화 결과가 아니라 전송된 원문 바이트를 대상으로 한다.

canonical response bytes도 같은 length-prefix 방식을 사용하며 순서는 다음과 같다.

1. 방향 문자열 `response`
2. 응답 sender `InstanceId`
3. 응답 receiver `InstanceId`
4. key epoch
5. 발급되었거나 요청에서 검증한 session ID 헤더와 동일한 canonical Base64 ASCII. 디코딩한 raw 16바이트가 아니다. revoke 응답은 길이 0
6. 원 요청의 대문자 HTTP method
7. 원 요청의 정규화된 path와 query
8. HTTP status의 앞자리 0 없는 decimal
9. 소문자로 정규화한 응답 content type
10. raw response body SHA-256 32바이트
11. response timestamp
12. 새 CSPRNG response nonce 16바이트
13. 원 요청 nonce 16바이트

응답도 대응하는 `X-DPAI-*` 헤더에 responder identity, epoch, session, timestamp, response nonce와 signature를 보낸다.

pair root로부터 `HMAC-SHA256`을 사용해 다음 여섯 키를 각각 파생한다. label은 표의 exact ASCII이고 대소문자·구두점·suffix를 바꾸지 않는다.

| 용도 | purpose label |
|---|---|
| handshake request | `peer-handshake-request-v1` |
| handshake response | `peer-handshake-response-v1` |
| session request | `peer-session-request-v1` |
| session response | `peer-session-response-v1` |
| revoke request | `peer-revoke-request-v1` |
| revoke response | `peer-revoke-response-v1` |

공통 KDF 입력은 아래 값을 순서대로 놓고 각 값을 §5.3과 같은 `UInt32BE(byteLength) || value`로 연결한다. 두 인스턴스 ID는 로컬·원격 역할이 아니라 소문자 `D` 문자열의 `Ordinal` 오름차순인 `FirstInstanceId`, `SecondInstanceId`로 고정해 양쪽이 같은 입력을 만든다.

1. 위 purpose label ASCII
2. `KeyEpoch`의 앞자리 0 없는 unsigned decimal ASCII
3. `FirstInstanceId` 소문자 `D` GUID ASCII
4. `SecondInstanceId` 소문자 `D` GUID ASCII

handshake·revoke 키는 `HMAC-SHA256(pairRoot, CommonInput)`으로 끝난다. session request·response 키에는 공통 입력 뒤에 handshake request nonce raw 32바이트, handshake response nonce raw 32바이트, 발급된 session ID raw 16바이트를 이 순서로 각각 length-prefix해 추가한 뒤 HMAC한다. nonce는 송신자의 로컬·원격 관점이 아니라 handshake 요청·응답 wire 역할로 고정한다. `KeyEpoch`는 `1..UInt64.MaxValue`, 두 InstanceId는 서로 다른 non-empty canonical GUID여야 한다. revoke 키는 session 없이 현재 peer binding과 key epoch에만 묶는다. 방향·목적별 키를 서로 재사용하지 않는다.

상호 운용 고정 벡터는 `pairRoot=00 01 ... 1f`, `KeyEpoch=42`, InstanceId `11111111-1111-1111-1111-111111111111`·`eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee`, handshake request nonce `20 21 ... 3f`, response nonce `40 41 ... 5f`, session ID `60 61 ... 6f`를 사용한다. 결과 32바이트의 canonical Base64는 다음과 같다.

| purpose label | derived key Base64 |
|---|---|
| `peer-handshake-request-v1` | `9eMpt5w0txG4uK1XusGsHSVIRt36ow/YfKGJ8mOAN30=` |
| `peer-handshake-response-v1` | `wxz21DWJInmWxxj9aFHY4Kgi4qsCD1AP/fZuyxHT4YI=` |
| `peer-session-request-v1` | `as76V2zzG1kia6KNw5T9w5Oddvdd7A66DTD/k8pYWvs=` |
| `peer-session-response-v1` | `11/2F9NgF7qMZIpo8hNiK/jULF+FCKTh1XzOF6QB+Lc=` |
| `peer-revoke-request-v1` | `NFgDZiO3xoNAPDE+kINAAy/sVL+EBQaCXLUHvhoOwXw=` |
| `peer-revoke-response-v1` | `4oqaxg5AmxEA8NMqE6v1rIL7uHQVmKulNhICCQtWSbI=` |

검증 순서는 다음과 같다.

1. listener·endpoint, 본문 크기, 필수 헤더 형식, 설정된 peer `InstanceId`와 key epoch를 확인한다.
2. raw body hash와 canonical bytes를 만들고 HMAC을 고정 시간으로 비교한다. XML은 아직 parse하지 않는다.
3. timestamp가 수신 시각 기준 ±60초인지 확인한다.
4. 서명이 유효한 요청 nonce를 해당 peer·epoch·session의 replay cache에 원자적으로 선등록한다. 이미 존재하면 거부한다.
5. 그 뒤에만 XML을 안전한 설정으로 parse·검증하고 상태를 변경한다.

exchange·release의 session 요청·응답은 현재 요청 헤더에서 session 신뢰 상태를 조립하지 않는다. 수신자가 이미 보유한 immutable active-session 상태의 정확한 local·peer `InstanceId`, key epoch, 16바이트 session ID, 만료 시각과 수신 방향별 request·response key에 모두 일치해야 한다. session 응답은 해당 로컬 미완료 요청이 보존한 원 요청 nonce와 canonical response의 원 요청 nonce도 고정 시간으로 비교한 뒤에만 성공으로 처리한다. handshake·revoke의 session 없는 인증과 새 session ID를 발급하는 handshake 응답 인증은 active-session 인증 경로와 분리한다.

handshake·revoke 인증도 현재 요청·응답 헤더에서 신뢰 상태나 key를 조립하지 않는다. 수신자가 내구적으로 확정한 immutable peer-pair 인증 상태의 정확한 local·peer `InstanceId`, 양의 key epoch와 handshake request·response, revoke request·response의 네 방향별 key를 사용한다. sender는 저장된 peer, receiver는 local, epoch와 method·target은 해당 메시지 용도와 일치하는지 HMAC과 replay 등록 전에 fail closed로 확인한다. handshake 응답은 session 발급 단계에 따라 session 헤더가 없거나 있을 수 있지만 저장된 peer binding과 원 요청 target·nonce에는 항상 결합한다. revoke 요청·응답에는 session 헤더를 허용하지 않는다.

handshake·revoke nonce replay 항목은 수신 후 최소 10분, session nonce 항목은 적어도 해당 10분 session 만료까지 유지한다. 프로세스 전체 replay cache의 최대 live 항목 수는 정확히 `1,024`개다. 이는 단일 피어, 10분 session, handshake 12회/분·exchange 30회/분 제한에서 정상 요청과 짧은 burst를 수용하면서 메모리를 고정하기 위한 값이다. 관계 폐기로 revoke cache가 사라져도 해당 root의 요청을 다시 허용하지 않는다. cache 포화 시 기존 live 항목을 조기 제거하지 않고 새 요청을 `429`로 거부하며, 항목별 남은 만료 시각을 외부에 노출하지 않으므로 이 응답에는 `Retry-After`를 넣지 않는다. 인증 전에 발생한 크기·형식 오류와 유효한 키가 없는 요청에는 서명된 상세 오류를 반환하지 않는다.

#### 5.4.1 `POST /api/sync/handshake`

요청:

```xml
<Handshake xmlns="urn:deepai:service-directory:peer">
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
  <KeyEpoch>1</KeyEpoch>
  <HandshakeNonce>iiqX+gRr+RBJWGeLgbIbi2pTxeTLJK3QIBzM7D6d46g=</HandshakeNonce>
  <UtcNow>2026-07-17T02:00:00Z</UtcNow>
  <SyncEnabled>true</SyncEnabled>
</Handshake>
```

수신자는 양쪽 상태가 `Enabled`인지, HMAC identity·실제 원격 endpoint·본문의 `InstanceId`와 저장된 peer binding이 모두 일치하는지, 요청 timestamp가 수신 시각 기준 ±60초 범위인지 확인한다. 별도 API·protocol version 협상은 하지 않는다.

성공 응답은 responder의 별도 256-bit nonce, CSPRNG 128-bit `SessionId`와 정확히 10분 뒤의 `ExpiresUtc`를 포함한다. 새 session은 성공 응답이 인증된 뒤 피어별 유일한 활성 session이 되며 이전 session은 폐기한다.

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Handshake>
    <InstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</InstanceId>
    <KeyEpoch>1</KeyEpoch>
    <HandshakeNonce>mfXAtQJYh99RIT2LZgOj4DynMQaNfKxDSEKS4YnPZ/8=</HandshakeNonce>
    <SessionId>2nAsCG8Y8SKuEv7/c/vNGA==</SessionId>
    <ExpiresUtc>2026-07-17T02:10:01Z</ExpiresUtc>
    <UtcNow>2026-07-17T02:00:01Z</UtcNow>
    <SyncEnabled>true</SyncEnabled>
  </Handshake>
</Response>
```

### 5.5 `POST /api/sync/exchange`

exchange는 유효한 10분 session ID가 필수다. 발신자의 service snapshot은 활성 레코드와 톰스톤을 포함하고 process-local 등록 모드는 포함하지 않는다. certificate public ledger·revocation과 CRL high-water 동기화는 §5.9의 별도 PKI state 계약을 따른다. 한 service batch는 최대 1,000개이면서 요청·응답 각각 4 MiB 이하여야 한다.

요청의 `Mode=Push`는 `SyncData` 자식과, `Mode=Pull`은 `PullRequest` 자식과 정확히 짝을 이뤄야 한다. 성공 응답의 `Exchange` payload는 `Mode=Pull`과 `SyncData` 조합만 허용하며 Push 성공은 `ExchangeAck`를 사용한다. XSD 1.0의 공통 `ExchangeType`만으로는 attribute와 선택 자식의 이 상관관계를 표현하지 못하므로 수신자는 스키마 검증 뒤 이 조합을 별도로 검사하고 불일치는 `400`·`1000 BAD_REQUEST`로 거부한다.

```xml
<Exchange xmlns="urn:deepai:service-directory:peer" Mode="Push">
  <SyncData>
    <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
    <SnapshotId>6f248a04-cc3e-409a-b499-cb571e6d30b7</SnapshotId>
    <LogicalClock>84</LogicalClock>
    <BatchIndex>0</BatchIndex>
    <TotalCount>1250</TotalCount>
    <IsLastBatch>false</IsLastBatch>
    <Items>
      <Service>
        <Name>Old App</Name>
        <ProductCode>WXYZ</ProductCode>
        <ServiceHostName>old-app.example.local</ServiceHostName>
        <ServiceIpv4Address>10.0.0.7</ServiceIpv4Address>
        <Port>22000</Port>
        <LastModifiedUtc>2026-07-01T00:00:00Z</LastModifiedUtc>
        <Deleted>true</Deleted>
        <DeletedUtc>2026-07-15T09:30:00Z</DeletedUtc>
        <LogicalVersion>82</LogicalVersion>
        <OriginInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</OriginInstanceId>
      </Service>
    </Items>
  </SyncData>
</Exchange>
```

- `SnapshotId`는 사이클마다 새 GUID이며 그 사이클의 불변 스냅샷을 식별한다. `LogicalClock`은 snapshot high-water이고 모든 batch에서 같아야 하며 각 `LogicalVersion` 이상이어야 한다. `BatchIndex`는 0부터 연속 증가하고 `TotalCount`는 모든 batch의 전체 항목 수이며 `IsLastBatch`는 마지막 batch에서만 `true`다.
- 전체 snapshot의 `Service` 항목은 정규화된 대문자 `ProductCode`의 `Ordinal` 오름차순으로 한 번 정렬한 뒤 그 순서를 유지해 batch로 나눈다. 수신자는 각 batch 내부뿐 아니라 직전 batch의 마지막 항목과 다음 batch의 첫 항목 사이도 strict ascending인지 검사하며, 역순·동일 key 재등장·정렬 기준 변경이 있으면 전체 staging을 폐기한다.
- 1,000개 또는 4 MiB를 넘는 스냅샷은 반드시 여러 batch로 나눈다. 톰스톤도 항목 수와 크기에 포함한다. 한 항목만으로 4 MiB를 넘으면 손상 데이터로 처리하고 sync를 중단한다.
- `Mode=Push` 응답은 검증한 batch의 `SnapshotId`와 `BatchIndex` ACK를 반환한다. 수신자는 같은 session의 batch를 staging하고 ID, 위 ProductCode 정렬, 연속 index, 중복, `TotalCount`, 마지막 표식과 각 레코드를 모두 검증한다.
- 마지막 batch까지 모두 검증하기 전에는 어떤 수신 항목이나 원격 `LogicalClock`도 메모리 현재 스냅샷이나 XML에 게시하지 않는다. 누락·중복·불일치·서명 실패·session 만료가 있으면 전체 staging snapshot을 폐기한다.
- 전체 Push를 검증한 뒤 §5.8에 따라 한 번 병합·원자 영속화하고, 그 결과의 불변 서버 스냅샷 ID를 ACK에 반환한다. 호출자는 `Mode=Pull`과 그 snapshot ID·다음 `BatchIndex`를 보내고 동일한 메타데이터를 가진 응답 batch를 순서대로 받는다.
- 호출자도 모든 Pull 응답 batch를 인증·검증·staging한 뒤에만 한 번 병합·원자 게시한다. 양쪽의 `IsLastBatch=true` 처리와 최종 게시가 끝나기 전에 sync 성공으로 기록하지 않는다.
- 최종 게시 시에는 공통 state mutation gate 안에서 최신 로컬 immutable snapshot과 검증된 원격 staging snapshot을 병합하고 `max(local LogicalClock, remote LogicalClock)`을 같은 복구 단위로 영속화한다. 네트워크 교환 중 등록·재등록·삭제된 로컬 변경을 과거 송신 snapshot으로 덮어쓰지 않으며, 상대가 아직 받지 못한 로컬 변경은 다음 sync 사이클에서 전파한다.

한 batch인 경우에도 `SnapshotId`, `BatchIndex=0`, `TotalCount`, `IsLastBatch=true`를 반드시 포함한다. 사이클 중 새 관리 변경이 생기면 현재 immutable snapshot에는 섞지 않고 다음 사이클에서 수렴시킨다.

Push batch ACK는 다음 형식이다. `ServerSnapshotId`는 마지막 Push batch를 전체 검증·병합·영속화한 성공 ACK에서만 포함하며 Pull 동안 유지할 수신자 측 불변 snapshot을 식별한다.

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <ExchangeAck>
    <Mode>Push</Mode>
    <SnapshotId>6f248a04-cc3e-409a-b499-cb571e6d30b7</SnapshotId>
    <BatchIndex>1</BatchIndex>
    <ServerSnapshotId>83c69c5b-6464-4ce6-a3ce-48e68b541bc2</ServerSnapshotId>
  </ExchangeAck>
</Response>
```

Pull 요청은 마지막 Push ACK의 `ServerSnapshotId`와 0부터 연속 증가하는 batch index를 사용한다. 같은 authenticated session에서 응답 유실 때문에 이미 성공한 index를 다시 요청하면 서버는 이후 로컬 변경을 섞지 않고 보관한 불변 snapshot의 같은 batch 데이터를 멱등 반환한다. 아직 제공하지 않은 index를 건너뛴 요청, 다른 snapshot ID와 batch 범위를 벗어난 index는 거부하며 다음 index 상태를 전진시키지 않는다. 마지막 batch를 제공한 뒤에도 session 만료 전까지는 이미 제공한 batch 재요청만 허용한다.

```xml
<Exchange xmlns="urn:deepai:service-directory:peer" Mode="Pull">
  <PullRequest>
    <SnapshotId>83c69c5b-6464-4ce6-a3ce-48e68b541bc2</SnapshotId>
    <BatchIndex>0</BatchIndex>
  </PullRequest>
</Exchange>
```

Pull 성공 응답은 `Mode=Pull`인 `Exchange` payload와 하나의 `SyncData` batch를 반환한다. 그 `SnapshotId`는 요청값과 같아야 한다. `Items`를 포함한 나머지 필드와 마지막 batch 규칙은 Push와 동일하다.

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Exchange Mode="Pull">
    <SyncData>
      <InstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</InstanceId>
      <SnapshotId>83c69c5b-6464-4ce6-a3ce-48e68b541bc2</SnapshotId>
      <LogicalClock>85</LogicalClock>
      <BatchIndex>0</BatchIndex>
      <TotalCount>1</TotalCount>
      <IsLastBatch>true</IsLastBatch>
      <Items>
        <Service>
          <Name>VMS Bridge</Name>
          <ProductCode>ABCD</ProductCode>
          <ServiceHostName>vms-bridge.example.local</ServiceHostName>
          <ServiceIpv4Address>10.0.0.5</ServiceIpv4Address>
          <Port>21500</Port>
          <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
          <Deleted>false</Deleted>
          <LogicalVersion>85</LogicalVersion>
          <OriginInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</OriginInstanceId>
        </Service>
      </Items>
    </SyncData>
  </Exchange>
</Response>
```

### 5.6 `POST /api/sync/release`

요청:

```xml
<Release xmlns="urn:deepai:service-directory:peer">
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <SessionId>2nAsCG8Y8SKuEv7/c/vNGA==</SessionId>
</Release>
```

유효한 10분 session과 HMAC을 가진 현재 피어 요청만 허용한다. 수신자는 동기화 비활성 상태를 영속화한 뒤 서명된 성공 응답을 반환하고 이후 exchange를 거부한다. session이 없거나 만료됐거나 다른 peer·epoch에서 발급됐으면 XML 처리 전에 거부한다.

### 5.7 `POST /api/sync/revoke`

이 엔드포인트는 sync session이 없거나 이미 비활성인 상태에서도 양쪽의 페어링 관계를 폐기할 수 있게 한다. 현재 pair root에서 파생한 전용 revoke request·response key를 사용하며 일반 session key나 unsigned 요청을 허용하지 않는다.

```xml
<Revoke xmlns="urn:deepai:service-directory:peer">
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
  <KeyEpoch>1</KeyEpoch>
  <RevokeId>94e02957-59bc-44f8-87db-e71ee91ebded</RevokeId>
</Revoke>
```

- `RevokeId`는 호출마다 새 CSPRNG 기반 GUID다. 수신자는 실제 원격 endpoint, 양쪽 `InstanceId`, 현재 `KeyEpoch`, timestamp, nonce와 revoke HMAC을 XML parse 전에 검증한다.
- 유효한 요청을 받으면 수신자는 서명된 성공 응답 bytes를 먼저 만들고 페어링 상태와 `secrets/peer.dat` 제거를 하나의 복구 가능한 작업으로 영속화한 뒤 응답한다. `config.xml`의 `LastPeerKeyEpoch`는 유지한다. 응답 전송 뒤 메모리의 이전 root와 파생 키를 지우고 이후 이전 epoch 요청을 모두 거부한다.
- 응답이 유실되면 호출자는 원격 폐기 여부를 확인할 수 없으므로 자동으로 이전 root를 다시 사용하거나 revoke를 성공으로 단정하지 않는다. 로컬 폐기는 계속 완료하고 Admin 상태에 원격 미확인을 표시한다. 다시 연결하려면 양쪽에서 명시적으로 새 페어링을 수행한다.

### 5.8 병합 규칙

ProductCode별 revision은 `(LogicalVersion, OriginInstanceId)`다. `LastModifiedUtc`와 `DeletedUtc`는 표시·감사 정보이며 병합 승자 판정에 사용하지 않는다.

| 로컬 | 원격 | 결과 |
|---|---|---|
| 없음 | 있음 | 원격 채택 |
| 있음 | 없음 | 로컬 유지 후 응답으로 전파 |
| 둘 다 있음 | `LogicalVersion`이 다름 | 큰 버전의 레코드 채택 |
| 둘 다 있음 | 버전이 같고 `OriginInstanceId`가 다름 | 정규화한 `OriginInstanceId`의 Ordinal 문자열 비교에서 큰 쪽 채택 |
| 둘 다 있음 | 버전·출처·정규화된 전체 내용이 같음 | 그대로 유지 |
| 둘 다 있음 | 버전·출처가 같고 내용이 다름 | `2005 REVISION_COLLISION`, 전체 exchange 중단 |

- `LogicalVersion`은 `1..18446744073709551615`의 unsigned 64-bit 정수다. GUID는 소문자 `D` 형식으로 정규화한 뒤 Ordinal 비교한다.
- revision collision은 정상 동시 변경이 아니라 손상 또는 잘못된 구현으로 취급한다. 수신 스냅샷을 게시·저장하지 않고 오류를 기록한다.
- 삭제와 수정 충돌도 같은 규칙을 적용한다.
- process-local 등록 모드와 CA private key·backup 암호는 동기화하지 않는다.
- 톰스톤은 시간 경과로 정리하지 않는다. 열린 등록 모드의 같은 ProductCode 신규 등록 commit에서만 새 활성 레코드로 대체한다.
- 전체 병합 후보에서 `Deleted=false` 활성 서비스가 1,000개를 넘으면 `2006 DIRECTORY_CAPACITY`로 exchange 전체를 중단하고 staging snapshot을 폐기한다. 현재 메모리·XML은 바꾸지 않으며 운영자가 한쪽에서 충분한 활성 서비스를 삭제한 뒤 새 session으로 다시 sync한다.
- 로컬 등록·재등록·삭제는 mutation gate 안에서 현재 durable `LogicalClock+1`을 record의 새 `LogicalVersion`으로 발급하고 clock·record·certificate ledger·필요한 CRL을 복구 일관성 있게 영속화한다. 등록 모드 open/close와 조회는 clock을 증가시키지 않는다.
- 인증되고 모든 batch가 유효한 원격 snapshot은 병합 성공 commit에서 `max(local LogicalClock, remote LogicalClock)`으로 관찰한다. 인증·batch·collision·용량·저장 실패에서는 clock도 변경하지 않는다.
- `LogicalClock` 최댓값에서는 wrap·reset하지 않고 `2007 LOGICAL_CLOCK_EXHAUSTED`로 로컬 변경을 실패시키며 현재 상태를 유지한다. backup·journal 복구가 마지막 발급값을 증명하지 못하면 낮은 값으로 재개하지 않는다.
- Peer timestamp ±60초 검사는 HMAC freshness·replay 방지의 인증 조건으로만 유지하고 병합 순서에는 사용하지 않는다.

병합 구현은 교환법칙, 결합법칙, 멱등성과 결정성을 속성 테스트로 검증한다.

### 5.9 `POST /api/sync/pki-state`

PKI state는 일반 service revision처럼 다중 writer 병합하지 않는다. 명시된 active issuer 한 인스턴스만 `PkiRevision`을 증가시키며 peer는 인증된 issuer의 더 높은 revision만 채택한다.

요청:

```xml
<PkiStateRequest xmlns="urn:deepai:service-directory:peer">
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <KnownIssuerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</KnownIssuerInstanceId>
  <KnownPkiRevision>42</KnownPkiRevision>
  <KnownCrlNumber>18</KnownCrlNumber>
</PkiStateRequest>
```

성공 응답:

```xml
<Response xmlns="urn:deepai:service-directory:peer">
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <PkiState>
    <IssuerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</IssuerInstanceId>
    <PkiRevision>43</PkiRevision>
    <CrlNumber>19</CrlNumber>
    <CrlSha256>base64-sha256</CrlSha256>
    <Crl>base64-der-crl</Crl>
    <ActiveCertificates>
      <Certificate>
        <ProductCode>ABCD</ProductCode>
        <SerialNumber>01A4...</SerialNumber>
        <LeafSha256>base64-sha256</LeafSha256>
        <NotAfterUtc>2027-07-19T02:00:00Z</NotAfterUtc>
      </Certificate>
    </ActiveCertificates>
  </PkiState>
</Response>
```

- 일반 Peer session·request/response HMAC, HTTPS site CA·pin과 exact endpoint 검증을 모두 적용한다.
- `PkiRevision`, `CrlNumber`는 unsigned monotonic high-water이며 rollback을 거부한다.
- `ActiveCertificates`에는 ProductCode별 ledger `CURRENT` 한 건만 넣는다. renewal overlap의 `RETIRING`과 CRL에 반영된 `REVOKED` history는 이 mapping에 넣지 않으며, 유효 여부는 current mapping과 별도로 signed CRL·ledger high-water의 일관성으로 검증한다.
- peer는 CRL signature를 site CA로 검증하고 `CrlSha256`·number·issuer와 active certificate mapping을 검증한 뒤 원자적으로 cache를 교체한다.
- 응답은 4 MiB 이하이며 active certificate는 최대 1,000개다. 전체 ledger history와 CA private key·backup·암호는 전송하지 않는다.
- 같은 revision의 다른 bytes, 다른 active issuer의 state, 낮은 revision·CRL number는 split-brain 또는 손상으로 fail closed한다.
- 보조 인스턴스는 이 read-only state로 `/pki/crl`과 조회 진단을 제공할 수 있지만 발급·폐기하지 않는다.
- issuer 장애 전환은 운영자가 최신 encrypted CA backup과 full ledger를 복원하고 peer에서 관찰한 high-water 이상임을 확인한 뒤 명시적으로 승격한다. 자동 승격은 금지한다.

## 6. Named Pipe 서비스 제어 계약

- 파이프: `\\.\pipe\SvcDirWatchdog`
- 연결당 한 줄 요청과 한 줄 응답 후 종료
- 요청과 응답은 BOM 없는 UTF-8 한 줄이다. 줄 끝은 LF 또는 CRLF이며 줄 끝을 포함한 인코딩 결과가 최대 256바이트여야 한다. BOM, 잘못된 UTF-8, 두 번째 줄, NUL과 256바이트 초과는 거부한다.
- 트레이의 연결 timeout은 3초이고 요청 전송 완료 뒤 전체 응답 수신 timeout도 3초다. 서버도 연결 뒤 3초 안에 완전한 요청 줄을 받지 못하면 연결을 닫는다.
- 원격 pipe client를 거부하고 각 연결에서 OS가 제공한 client token을 확인한다. 요청 문자열의 사용자 정보는 신뢰하지 않는다.
- 명령은 정확히 다음 네 개만 허용한다.

| 요청 | 의미 |
|---|---|
| `START` | 메인 서비스 시작 |
| `STOP` | 메인 서비스 종료 |
| `RESTART` | 메인 서비스 재시작 |
| `STATUS` | 서비스 상태 조회 |

현재 실행체의 SCM 서비스 이름은 메인 `DEEPAi.ServiceDirectory`, 와치독 `DEEPAi.ServiceDirectory.Watchdog`로 고정한다. 성공한 수동 `STOP`은 메인 서비스가 의도적으로 중지된 상태로 기록해 자동 재시작을 억제하고, 성공한 수동 `START` 또는 `RESTART`만 이 억제와 `SUPPRESSED` latch 및 기존 rolling restart 시도를 해제한다. 자동 재시작 예산은 실제 서비스가 다시 Running이 됐는지가 아니라 제한된 restart 호출을 시작한 시점에 한 번 소비한다. 따라서 제어 경로 자체가 계속 실패해도 10초마다 무한 재시도하지 않는다.

요청 줄을 완전히 받은 뒤 명령 처리부터 응답 완료까지 하나의 monotonic 3초 deadline을 사용한다. operation gate 대기와 `ServiceController` 제어에는 이 잔여 시간만 주고, 실제 START·STOP·RESTART 제어 예산은 최대 2.5초이며 응답 쓰기 시간을 별도로 남긴다. 동기 `ServiceController` 상태 조회 자체는 취소할 수 없으므로 deadline을 넘긴 응답 연결은 닫되 지연된 SCM 호출을 성공으로 보고하지 않는다.

응답:

```text
OK
```

`START`, `STOP`, `RESTART`의 성공은 위 응답을 사용한다. `STATUS` 성공은 다음 단일 행 형식으로 실제 `ServiceControllerStatus`와 와치독 진단값을 반환한다.

```text
OK: RUNNING;HEALTH=OK;FAILURES=0;RESTARTS_10M=0;AUTO_RESTART=ENABLED;LAST_HEALTH_UTC=2026-07-18T04:00:00.000Z
```

첫 값은 하위 호환 prefix `OK: {MAIN_STATUS}`다. `MAIN_STATUS` 허용값은 `STOPPED`, `START_PENDING`, `STOP_PENDING`, `RUNNING`, `CONTINUE_PENDING`, `PAUSE_PENDING`, `PAUSED`다. 그 뒤에는 세미콜론으로 구분한 `KEY=VALUE`를 다음 순서로 보낸다.

| key | 값과 의미 |
|---|---|
| `HEALTH` | `NOT_RUN`, `OK`, `FAILED`. 와치독 기동 뒤 health 완료 전, 마지막 health 성공, 마지막 health 실패 |
| `FAILURES` | 현재 연속 health 실패 횟수인 0 이상 invariant decimal |
| `RESTARTS_10M` | 응답 시점 직전 rolling 10분의 자동 재시작 횟수인 `0..3` invariant decimal |
| `AUTO_RESTART` | `ENABLED` 또는 10분 안에 3회 재시작해 자동 재시작을 멈춘 `SUPPRESSED` |
| `LAST_HEALTH_UTC` | 마지막 완료 health의 UTC `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`. `HEALTH=NOT_RUN`일 때만 생략 |

필수 key는 `HEALTH`, `FAILURES`, `RESTARTS_10M`, `AUTO_RESTART`이고 각 key는 한 번만 존재해야 한다. `HEALTH`가 `NOT_RUN`이 아니면 `LAST_HEALTH_UTC`도 필수다. 중복 key, 필수 key 누락, 잘못된 정수·시각·enum은 전체 응답 오류로 처리한다. `SUPPRESSED`는 운영자가 해제할 때까지 유지하는 latch이므로 `RESTARTS_10M`은 rolling window가 지난 뒤 `0..3`일 수 있다. `AUTO_RESTART=ENABLED`인데 `RESTARTS_10M=3`인 응답은 상태 모순으로 거부한다.

각 진단 field는 정확히 `;KEY=VALUE` 형식이며 세미콜론 앞뒤와 `=` 앞뒤에 공백을 두지 않는다. `KEY`는 1~32바이트 ASCII이고 `[A-Z][A-Z0-9_]*` 형식이다. `VALUE`는 1~96바이트의 printable ASCII `!`부터 `~`까지지만 `;`는 금지하며 공백·Unicode를 허용하지 않는다. known·unknown을 합쳐 같은 key가 두 번 나오면 거부한다. 미래 호환 key는 마지막 known field 뒤에만 추가하며 클라이언트는 문법에 맞는 모르는 key를 무시한다. 기존 첫 `OK: {MAIN_STATUS}` 의미와 필수 known key 의미는 변경하지 않는다. 전체 UTF-8 행은 기존 256바이트 제한을 만족해야 한다.

성공적인 pipe 왕복 자체가 와치독 프로세스 응답 가능 상태임을 나타낸다. 메인 서비스 상태를 확인할 수 없으면 성공이나 `UNKNOWN`으로 숨기지 않고 기존처럼 `ERROR:`를 반환한다.

오류:

```text
ERROR: 사용자에게 노출 가능한 일반 사유
```

- 알 수 없는 명령과 인수는 거부한다.
- pipe ACL은 `DEEPAi-ServiceDirectory-Operators`, 와치독 서비스 SID, `SYSTEM`, 로컬 `Administrators`에만 필요한 연결·읽기·쓰기 권한을 부여한다. 서버는 연결 뒤 호출자 identity가 이 ACL과 운영자 그룹 정책을 충족하는지 다시 확인한다.
- `Everyone`, `Users`, `Authenticated Users`, Anonymous에 연결 또는 쓰기 권한을 주지 않는다. 일반 사용자 트레이는 해당 사용자 또는 그 AD 그룹을 로컬 운영자 그룹에 추가해 허용한다.
- 요청 원문, OS 내부 경로, 스택 또는 시크릿을 응답하지 않는다.
- 와치독은 전용 Windows 가상 서비스 계정과 서비스 SID로 실행하고 메인 서비스 제어에 필요한 권한만 가진다. 메인 서비스도 별도 가상 서비스 계정을 사용하며 두 서비스를 `LocalSystem`으로 설치하지 않는다. 대상 Windows에서 가상 계정을 사용할 수 없는 예외가 확인되면 구현 전에 별도 계정과 보완 통제를 승인 기록한다.

## 7. 계약 상태와 남은 구현 의존성

### 7.1 목표 계약 상태

사이트 CA·Directory/service leaf·CSR 검증·serial·CRL primitive에 더해 DPAPI `LocalMachine` CA key, canonical metadata·full ledger·signed CRL 저장, 공용 recovery journal, 암호화 backup, CA 상태·원장 조회·serial 폐기 Admin API/XSD와 설정 UI, installer repair의 표준 입력 복원 진입점 소스를 연결했다. 첫 서비스 기동에서 PKI가 전혀 없고 backup 흔적도 없는 전환 설치만 새 CA를 만들고 `BACKUP_REQUIRED`로 시작하며, backup 뒤 `READY`가 된다. 현재 작업 트리의 빌드·테스트·실제 DPAPI/ACL·설치 repair 실행은 검증하지 않았다. 등록 모드·실제 인증서 발급 연결·Peer HTTPS·PKI state wire 계약은 아직 연결하지 않았으며 후속 구현은 적어도 다음 항목을 함께 완료해야 한다.

- `admin.xsd`: 등록 모드 조회·열기·닫기와 서비스 등록 모드 결과. CA 상태·backup·원장·serial 폐기는 반영됨
- `peer.xsd`: PKI state ledger·CRL high-water 교환
- Admin handler와 설정 UI: pending 3 endpoint·승인 대기 화면 제거, 등록 서비스 화면의 모드·countdown·마지막 결과. CA 상태·backup·원장·serial 폐기는 반영됨
- Peer host·client: HTTPS binding·site CA/pin 검증과 session-authenticated PKI state 교환
- 저장·복구: 발급·재등록·삭제에서 directory와 certificate ledger·CRL을 함께 바꾸는 transaction. CA 단독 상태·폐기·backup marker와 기존 target을 아우르는 journal 확장은 반영됨
- 설치·운영: HTTPS binding과 single active issuer 장애·승격 절차. CA backup 생성과 중지 repair 복원 진입점은 반영됐으나 실제 설치 실행 검증은 남음

### 7.2 변경 전 구현 기준선

Admin의 exact `127.0.0.1:21000` local endpoint·loopback remote endpoint 확인, 현재 요청 Windows identity 인가, raw path·query 기반 route, identity별 rate·동시성 admission, 16 KiB body·media type 제한, strict request DTO parse와 안전한 HTTP 오류 mapping을 순서대로 적용하는 adapter와 경계 테스트 소스를 구현했다. `HttpListenerRequest.RawUrl`의 origin-form ASCII만 받아 첫 `?`에서 exact path/query를 나누고 decode·normalize하지 않는 공용 handoff parser, `/admin/*`에만 `Negotiate`를 선택하고 `UnsafeConnectionNtlmAuthentication=false`를 유지하는 실제 메인 `HttpListener` host, application 상태·영속화·로그·sync handler와 Event Log source 설치 helper도 소스로 연결했다. 현재 작업 트리의 빌드·테스트와 실제 Windows Negotiate/NTLM·Event Log 실행은 검증하지 않았다.

Pairing transcript·decision·commit, HTTP 상태, 고정 namespace와 XSD·확장 정책은 이 문서에서 확정했다. 일반 Peer request·response header strict codec, length-prefix canonical bytes, immutable peer-pair·active-session 인증 context, 목적별 HMAC-SHA256 key 파생, ±60초 freshness와 bounded nonce replay cache primitive를 구현했다. pairing hello·key-confirm·decision·commit의 상태 머신과 terminal MAC, 완료된 동일 decision의 exact retry 응답, outbound pairing, DPAPI PeerSecret·config 복합 영속화, handshake·release·revoke·Exchange HTTP handler도 controller에 연결했다. 현재 작업 트리의 빌드·테스트와 실제 두 장비 페어링·DPAPI·장애 복구 실행은 검증하지 않았다.

일반 sync의 handshake request·response, session request·response, revoke request·response key 파생에 사용할 여섯 exact purpose label과 length-prefix KDF 입력은 §5.4에 확정했다. pair root·epoch·canonical peer pair와 session의 양쪽 handshake nonce·session ID를 목적별로 결합하는 32바이트 HMAC-SHA256 파생 primitive와 고정 벡터 테스트 소스, 실제 inbound·outbound handshake와 session-authenticated Push/Pull·release·revoke orchestration 소스를 추가했다. 현재 작업 트리에서는 이를 빌드·테스트하거나 실제 HTTP 양방향 교환으로 검증하지 않았다.

replay cache는 live 항목을 조기 제거하지 않고 포화 시 새 요청을 `429`로 거부하며, 운영 생성자는 이 절의 고정 상한 `1,024`개를 사용한다. 작은 capacity와 가상 monotonic clock을 주입하는 생성자는 경계 조건 검증을 위한 internal 테스트 전용이다. process-wide cache와 인증 실패·clock skew·endpoint binding·rate-limit 응답 mapping은 Peer HTTP handler에 연결했지만 현재 작업 트리에서 빌드·실행하지 않았다.

일반 Peer inbound handshake·exchange·release·revoke 요청에는 인증·admission coordinator와 순수 단위 테스트 소스를 추가했다. coordinator는 각 `X-DPAI-*` header가 정확히 한 값인지와 session header의 endpoint별 필수·금지 형태를 먼저 검사하고, header가 아니라 내구 peer-pair context 또는 기존 active-session context의 local·peer identity·epoch·session·방향별 key로 request를 조립한다. 그 뒤 §5.4의 순서대로 HMAC 고정 시간 검증, ±60초 freshness, 유효 서명 nonce 선등록을 적용한다. 인증 실패는 nonce cache를 소비하지 않으며 보안 진단 handoff는 operation·reason의 닫힌 enum만 포함해 signature·nonce·session·key·body를 전달할 수 없게 했다. 인증된 `(canonical peer endpoint, InstanceId)` binding의 handshake `12회/분`·burst `3`, exchange `30회/분`과 controller의 handshake·sync single-flight는 소스에 연결했다. release·revoke·pairing의 별도 수치 제한과 인증 전 endpoint-only rate 경계는 현재 명세에 확정 수치·상세 전이가 없으므로 추측해 추가하지 않았다.

운영 coordinator들은 process-wide `1,024`개 in-memory replay cache를 공유하지만 process restart에서는 그 이력이 사라진다. 현재 session owner는 active session을 영속화하지 않고 서비스 시작 때 새 session이 없는 상태로 시작하며, 중지·해제·재페어링에서 기존 session key를 폐기하므로 재시작 전 session을 복원·재사용하지 않는다. 이 fail-closed 수명주기와 restart 경계는 소스에 반영했지만 현재 작업 트리에서 빌드·실행·강제 종료 검증하지 않았다.

API wire 밖의 `LogicalClock` high-water를 포함하는 다중 파일 journal·backup 복구 형식은 [개발계획 §5.4](./03-development.md#54-schema-migration과-다중-파일-복구-저널)에 확정했다. 변경 전 구현은 순수 sync 병합, 다중 Push batch staging과 마지막 batch에서만 병합·영속화하는 processor, session에 묶인 불변 outbound Pull snapshot·순차 batch lease와 수명주기 owner, directory·pending·config·DPAPI PeerSecret 복합 transaction을 실제 controller와 공용 mutation gate에 연결했다. active journal이 없는 standalone `.bak`은 high-water 회귀를 증명할 수 없어 자동 복원하지 않고 fail closed한다. 목표 구현에서는 pending을 제거하고 ledger·CRL·CA state를 같은 복구 원칙으로 대체해야 한다. 현재 작업 트리의 빌드·테스트, 실제 process termination과 양방향 장애 복구 검증 전에는 등록·동기화 commit을 운영상 내구적 완료로 처리하지 않는다.

와치독은 별도 x64 Windows Service 실행체, `WDOG` 일일 키의 loopback health client, 전체 응답 3초 deadline, 10초 감시 주기, 3회 연속 실패와 10분 3회 restart latch, 최소 ServiceController 경계, ACL과 연결 후 Windows token을 모두 검사하는 Named Pipe server 소스를 구현했다. installer에는 메인·와치독 가상 서비스 계정과 서비스 SID, 파일·서비스 제어 DACL, URL ACL·방화벽, Event Source 구성 helper 소스가 있다. 자동 재시작 실패를 승인된 9개 시스템 파일 이벤트에 새 코드로 추가하지 않는다. 현재 작업 트리의 빌드·서비스 설치·실제 SCM/pipe/Event Log/AD·Workgroup 검증은 남아 있다.
