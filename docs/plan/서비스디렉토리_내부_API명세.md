# 서비스 디렉토리 내부 API 명세

> 문서 상태: 1.0 초안
> 구현 상태: 미구현
> 대상 독자: 메인 서비스, 트레이 앱, 와치독, 피어 동기화 구현 개발자
> 최종 정리일: 2026-07-17

이 문서는 서비스 디렉토리 구성요소 사이의 관리·동기화·서비스 제어 계약을 정의한다. 다른 애플리케이션이 사용하는 계약은 [외부 애플리케이션 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md)만을 따른다.

## 1. 내부 인터페이스 경계

| 호출자 → 수신자 | 인터페이스 | 용도 |
|---|---|---|
| 트레이 → 메인 서비스 | `/admin/*` | 목록, 승인·거절·삭제, 동기화 관리 |
| 와치독 → 메인 서비스 | `GET /api/health` | 프로세스 외부 생존 확인 |
| 메인 서비스 → 피어 메인 서비스 | `/api/sync/*` | 핸드셰이크, 데이터 교환, 해제 |
| 트레이 → 와치독 | `\\.\pipe\SvcDirWatchdog` | 시작, 종료, 재시작, 상태 |

헬스체크 응답의 단일 원본은 [외부 명세의 `GET /api/health`](./서비스디렉토리_외부애플리케이션_API명세.md#41-get-apihealth)다.

## 2. 신뢰 경계와 인증 계약

### 2.1 Admin

- `/admin/*`는 `http://127.0.0.1:21000`의 loopback 인터페이스에만 바인딩하고 원격 요청을 거부한다. wildcard, `0.0.0.0` 또는 원격 인터페이스 바인딩은 금지한다.
- loopback은 인증이 아니다. 같은 포트의 External·Peer 경로까지 Windows 인증으로 바뀌지 않도록 `AuthenticationSchemeSelectorDelegate` 또는 경로가 분리된 listener를 사용해 `/admin/*`에만 `Negotiate`를 선택한다. 기준 주소가 IP literal `127.0.0.1`이므로 Kerberos·SPN을 전제로 하지 않고 AD 도메인 사용자와 Workgroup 로컬 사용자 모두 NTLM을 허용한다. 추후 loopback으로만 해석되는 hostname과 올바른 SPN을 별도 검증한 배포에서는 Kerberos가 협상될 수 있지만 필수 계약이 아니다. Admin의 Basic·Anonymous 인증은 허용하지 않는다. External·Peer에서 HTTP.sys의 `Anonymous`를 선택하는 것은 각 명세의 일일 키·HMAC 애플리케이션 인증을 생략한다는 뜻이 아니다.
- `HttpListener.UnsafeConnectionNtlmAuthentication`은 `false`로 유지한다. 연결 단위 NTLM 인증 캐시를 켜서 다른 연결·요청의 호출자 identity를 재사용해서는 안 된다.
- 인증된 Windows identity가 설치 시 생성하는 로컬 그룹 `DEEPAi-ServiceDirectory-Operators`의 구성원일 때만 Admin API를 인가한다. 인증 실패는 `401`, 그룹 인가 실패는 `403`이다.
- 도메인 환경에서는 필요한 AD 사용자 또는 AD 그룹을, Workgroup 환경에서는 필요한 로컬 사용자를 각 서버의 `DEEPAi-ServiceDirectory-Operators` 로컬 그룹에 추가한다. 트레이 앱은 일반 권한으로 실행하며 UI 표시 여부를 인가로 사용하지 않는다.
- 그룹 구성 변경은 새 Windows logon token에 반영된 뒤 유효하다. 설치 프로그램과 운영 문서는 재로그인 또는 프로세스 재시작이 필요할 수 있음을 안내한다.

### 2.2 Peer

- 원격 동기화는 승인된 폐쇄망에서 HTTP/1.1을 사용하며 TLS/HTTPS와 X.509 인증서를 구성하지 않는다. 이는 [개발계획 §8.2](./서비스디렉토리_개발계획.md#82-tls-미사용-예외-기록)의 승인된 전송 암호화 예외다.
- 최초 페어링과 이후 피어 인증은 AD에 의존하지 않고 §5.3의 ECDH P-256·SAS 계약과 §5.4의 HMAC 계약을 따른다. HTTP Basic, URL query key와 캡처 후 재사용 가능한 평문 bearer token은 금지한다.
- 제품 자체의 고정 CIDR allowlist는 두지 않는다. 대신 listener를 승인된 폐쇄망의 명시적 로컬 인터페이스에만 바인딩하고 wildcard `http://+:21000/`, `0.0.0.0` 또는 비신뢰망 노출을 금지한다.
- Windows 방화벽 인바운드 규칙은 Domain·Private 프로필에서만 해당 포트와 프로그램을 허용하고 Public 프로필에서는 차단한다. 설치 프로그램과 제품 설정은 원격 CIDR·원격 주소 범위 제한을 만들거나 요구하지 않는다.
- 모든 Peer 요청에서 실제 원격 IP와 인증된 `InstanceId`가 페어링 때 저장한 단일 피어 endpoint의 IP·`InstanceId`와 모두 일치해야 한다. 이는 페어링 identity binding 검증이지 원격 CIDR·대역 allowlist가 아니다. TCP source port는 비교하지 않으며 주소 검증은 암호학적 피어 인증을 대신하지 않는다.
- 페어링 root가 없거나 DPAPI 복호화·ACL 검증에 실패하면 Peer API의 handshake·exchange·release·revoke를 닫고 재페어링을 요구한다.

### 2.3 Local IPC

- Named Pipe는 명령 허용 목록과 호출자 ACL을 모두 적용한다.
- 파이프 ACL은 `DEEPAi-ServiceDirectory-Operators` 로컬 그룹과 와치독 서비스 SID, `SYSTEM`, 로컬 `Administrators`에만 필요한 연결·읽기·쓰기 권한을 부여한다. `Everyone`, `Users`, `Authenticated Users`, Anonymous에 쓰기 권한을 부여하지 않는다.
- 트레이 사용자는 Admin API와 같은 방식으로 AD 사용자·그룹 또는 Workgroup 로컬 사용자를 로컬 운영자 그룹에 추가해 인가한다.

### 2.4 Health

- 외부 애플리케이션과 와치독 모두 `GET /api/health` 호출 전에 [외부 명세의 일일 API 키](./서비스디렉토리_외부애플리케이션_API명세.md#23-일일-api-키-생성-계약)를 제공한다.
- 와치독은 health 전용 구성요소 코드 `WDOG`와 시스템 로컬 날짜로 키를 생성한다. `WDOG`는 디렉토리 등록 ProductCode가 아니다.
- health는 키에서 복원한 ProductCode의 형식과 서버 로컬 날짜만 검증한다. 이 키는 Admin·Peer 접근 권한을 부여하지 않는다.
- 와치독 health 대상은 `http://127.0.0.1:21000/api/health`로 고정한다. External·Peer는 `config.xml`의 단일 IP literal `ListenAddress`를 사용하며 loopback health prefix가 원격 노출 범위를 넓히지 않는다.
- 와치독은 10초 간격으로 health를 호출하고 각 호출의 연결부터 전체 응답 완료까지 timeout을 3초로 제한한다.

Admin 호출자 인증·인가와 Peer 상호 인증의 계약은 이 문서에서 확정한다. 구현은 해당 계약과 실패 경로를 검증하기 전까지 운영 준비 완료로 처리하지 않는다.

## 3. 공통 HTTP·XML 규칙

- 포트: TCP `21000`
- 프로토콜 의미: HTTP/1.1
- scheme: `http`. HTTPS listener, 인증서 바인딩, HTTP→HTTPS redirect와 HSTS는 구성하지 않음
- External·Peer prefix는 설치 프로그램이 `config.xml`에 저장한 exact `ListenAddress`, Admin과 와치독 health prefix는 `127.0.0.1`이다. 메인 서비스는 `ListenAddress`가 현재 로컬 Domain·Private 인터페이스에 할당된 non-loopback IPv4·IPv6 literal인지 기동 때 검증하고, 누락·미할당·wildcard·Public 주소면 어떤 원격 prefix도 열지 않은 채 기동을 실패시킨다.
- 본문: `application/xml; charset=utf-8`
- payload·프로토콜 시각: UTC ISO 8601. health 일일 API 키 날짜만 외부 명세에 따라 시스템 로컬 `yyyyMMdd` 사용
- XML DTD와 외부 엔터티를 금지하고 루트 요소를 깊이 1로 계산한 최대 깊이를 16으로 제한한다.
- 모든 GET 요청은 body를 허용하지 않는다.
- `/admin/*`, `/api/sync/pairing/*`, `/api/sync/handshake`, `/api/sync/release`, `/api/sync/revoke`의 요청과 응답 본문은 각각 UTF-8 원문 기준 최대 16 KiB다. Admin 목록은 `pageSize`보다 적게 반환하더라도 16 KiB를 넘기기 전에 `NextCursor`로 다음 페이지를 이어야 한다. `/api/sync/exchange` 요청과 응답 본문은 각각 최대 4 MiB다.
- `GET /admin/services`와 `GET /admin/pending`은 opaque cursor 페이지네이션을 사용한다. `pageSize` 기본값은 100, 최댓값은 250이며 1 미만 또는 250 초과는 `1000 BAD_REQUEST`다. 마지막 페이지가 아니면 응답의 `NextCursor`를 그대로 다음 요청의 `cursor`로 전달한다.
- Admin 속도 제한은 인증된 identity와 서비스 인스턴스를 기준으로 이동 1분 창에서 읽기 `30회/분`, 변경 `10회/분`, `/admin/sync/now` `2회/분`이다. 동시에 처리하는 Admin 요청은 서비스 인스턴스당 최대 8개다.
- Peer 속도 제한은 설정된 원격 endpoint를 기준으로 handshake `12회/분`·burst 3, exchange batch `30회/분`이다. 인증 성공 뒤에는 endpoint와 `InstanceId`를 함께 키로 사용한다. 피어별 활성 sync 세션은 1개만 허용한다.
- sync batch는 활성 레코드와 톰스톤을 합해 최대 1,000개이며 한 batch의 요청·응답은 위 4 MiB 한도를 모두 만족해야 한다.
- 브라우저 호출, CORS와 정적 파일 제공은 지원하지 않는다. 제품·프레임워크 식별 응답 헤더는 제거하거나 플랫폼이 허용하는 최소 정보로 제한한다.
- 디렉토리·대기·설정의 모든 변경 명령과 sync 최종 병합·게시는 서비스 인스턴스당 하나의 state mutation gate로 직렬화한다. 네트워크 송수신과 XML parse는 gate 밖에서 수행하고, gate 안에서 현재 revision 재검증, 복구 저널·원자 저장과 immutable snapshot 교체를 완료한다. 조회는 현재 immutable snapshot을 사용해 gate를 점유하지 않는다.

공통 응답 envelope:

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message></Message>
  <!-- 엔드포인트별 payload -->
</Response>
```

내부 오류 코드:

| Code | 이름 | 의미 |
|---|---|---|
| 0 | `OK` | 성공 |
| 1000 | `BAD_REQUEST` | XML, 경로 매개변수 또는 값 검증 실패 |
| 1001 | `NOT_FOUND` | 서비스 또는 대기 요청 없음 |
| 1002 | `CONFLICT` | 이미 처리됨, 현재 상태와 충돌 |
| 1004 | `LIMIT_EXCEEDED` | 활성 서비스·승인 대기·페이지·속도·동시 실행 등 확정 제한 초과 |
| 2001 | `NOT_PEER` | 설정된 피어가 아닌 호출자 |
| 2002 | `PEER_MISMATCH` | 상대의 피어 설정이 수신자를 가리키지 않음 |
| 2003 | `CLOCK_SKEW` | 서버 간 시계 편차가 60초를 초과 |
| 2004 | `SYNC_DISABLED` | 수신 측 동기화가 비활성 |
| 2005 | `REVISION_COLLISION` | 같은 비교 시각·변경 출처에 서로 다른 payload가 존재 |
| 2006 | `DIRECTORY_CAPACITY` | 병합 후보의 활성 서비스가 1,000개를 초과 |
| 3000 | `INTERNAL` | 내부 오류. 상세 예외는 응답하지 않고 별도로 승인된 진단 대상에만 기록 |

인증 실패와 권한 부족은 각각 HTTP `401`·`403`, 요청 크기 초과는 `413`, 속도·동시성 제한 초과는 `429`를 사용한다. 이 네 경우 XML envelope은 전송하지 않아도 되며 상세 인증·제한 상태를 외부에 노출하지 않는다.

### 3.1 현재 HTTP 상태 규칙

| HTTP | 사용처 |
|---|---|
| 200 | 정상 처리와 현재 정의된 논리 오류. envelope `Code`로 구분 |
| 401 | 인증되지 않은 호출. 플랫폼 인증 단계에서는 XML envelope이 없을 수 있음 |
| 403 | 인증됐지만 권한 또는 Admin·Peer 접근 등급이 맞지 않음 |
| 404 | 정의되지 않은 경로 |
| 413 | 요청 본문 또는 sync 항목 수 제한 초과 |
| 415 | 지원하지 않는 `Content-Type` |
| 429 | 속도 또는 동시 실행 제한 초과 |
| 500 | 처리되지 않은 내부 오류 |

1.0 확정 전에 논리 오류를 표준 `400`·`404`·`409`로 옮길지 외부 계약과 함께 결정한다. 클라이언트는 성공 HTTP 상태에서도 envelope `Code`를 확인한다.

## 4. Admin API

Admin 요청은 loopback 제한과 별도의 호출자 인증·인가를 모두 통과해야 한다.

### 4.1 `GET /admin/services`

승인된 목록을 페이지 단위로 반환한다. `includeDeleted=true`이면 톰스톤을 포함하며 기본값은 `false`다. 요청 형식은 `GET /admin/services?includeDeleted=false&pageSize=100&cursor=...`이고 첫 요청에서는 `cursor`를 생략한다.

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Services>
    <Service>
      <Name>VMS Bridge</Name>
      <ProductCode>ABCD</ProductCode>
      <ServerAddress>10.0.0.5</ServerAddress>
      <Port>21500</Port>
      <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
      <Deleted>false</Deleted>
    </Service>
  </Services>
  <NextCursor>opaque-server-value</NextCursor>
</Response>
```

정렬은 정규화한 `ProductCode`의 Ordinal 오름차순으로 고정한다. cursor는 서버가 생성한 opaque 값이며 목록 revision과 `includeDeleted` 조건을 결합한다. cursor의 revision이 현재 목록과 달라지거나 위조·만료되면 `1002 CONFLICT`로 처음부터 다시 조회하게 한다. 마지막 페이지에서는 `NextCursor`를 생략한다.

Admin DTO는 운영 UI에 필요한 값만 반환한다. 동기화 전용 `OriginInstanceId`는 §5.1의 Sync DTO에서만 사용하고 Admin·외부 DTO에는 노출하지 않는다. `DeletedUtc`는 `Deleted=true`일 때만 포함하고 활성 항목에서는 생략한다.

### 4.2 `GET /admin/pending`

New와 Modify 승인 대기 목록을 페이지 단위로 반환한다. 요청 형식은 `GET /admin/pending?pageSize=100&cursor=...`이고 첫 요청에서는 `cursor`를 생략한다.

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <PendingItems>
    <PendingItem>
      <Id>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</Id>
      <Type>Modify</Type>
      <RequestedUtc>2026-07-17T02:00:00Z</RequestedUtc>
      <SourceIP>10.0.0.5</SourceIP>
      <Requested>
        <Name>VMS Bridge</Name>
        <ProductCode>ABCD</ProductCode>
        <ServerAddress>10.0.0.9</ServerAddress>
        <Port>21500</Port>
      </Requested>
      <Current>
        <Name>VMS Bridge</Name>
        <ProductCode>ABCD</ProductCode>
        <ServerAddress>10.0.0.5</ServerAddress>
        <Port>21500</Port>
      </Current>
    </PendingItem>
  </PendingItems>
  <NextCursor>opaque-server-value</NextCursor>
</Response>
```

- `Current`는 Modify일 때만 존재한다.
- `SourceIP`는 감사 보조 정보일 뿐 인증된 요청자 ID를 대신하지 않는다.
- 정렬은 `RequestedUtc`, 요청 ID 순의 오름차순으로 고정한다. cursor는 대기 목록 revision을 결합하며 변경·위조·만료 시 `1002 CONFLICT`로 처음부터 다시 조회하게 한다. 마지막 페이지에서는 `NextCursor`를 생략한다.
- 서버는 응답 표시 여부와 관계없이 대기 생성 시 ProductCode의 전체 상태를 식별하는 불변 `BaseRevision` 또는 동등한 스냅샷을 보존한다. 미등록, 톰스톤, 활성 상태를 서로 다르게 식별하고 payload·비교 시각·변경 출처의 변화를 감지해야 한다.

### 4.3 `POST /admin/pending/{id}/approve`

- 상태 변경 락 안에서 현재 revision을 대기의 base revision과 다시 비교한다.
- revision이 같으면 New는 톰스톤을 대체해 새 활성 항목을 만들고, Modify는 현재 활성 항목을 요청값으로 갱신한다.
- revision이 다르지만 현재 활성값이 요청값과 완전히 같으면 요청이 이미 충족된 것으로 대기만 제거하고 새 변경 시각이나 sync를 만들지 않는다.
- revision이 다르고 요청값과도 다르면 `1002 CONFLICT`를 반환하고 대기를 유지한다. 자동 덮어쓰기 또는 New·Modify 재분류는 하지 않는다.
- New 승인으로 `Deleted=false` 활성 서비스가 1,000개를 넘으면 `1004 LIMIT_EXCEEDED`를 반환하고 대기를 유지한다. 기존 활성 서비스의 Modify와 삭제는 이 용량 제한 때문에 막지 않는다.
- 변경 시각은 §5.8의 단조 증가 규칙으로 생성한다.
- 로컬 `InstanceId`를 `OriginInstanceId`로 기록한다.
- 디렉토리 반영과 대기 항목 제거를 하나의 복구 가능한 작업으로 처리한다.
- 새 도메인 변경을 적용한 경우에만 즉시 동기화 사이클을 예약한다. 동기화 실패가 승인 자체를 되돌리지는 않는다.
- 없는 ID는 `1001 NOT_FOUND`, 이미 처리된 상태가 식별 가능하면 `1002 CONFLICT`다.

### 4.4 `POST /admin/pending/{id}/reject`

- 대기 요청만 제거하고 활성 디렉토리와 톰스톤은 변경하지 않는다.
- 동기화 푸시는 하지 않는다.
- 외부 호출자가 거절 결과를 확인하는 계약과 결과 보존 기간은 아직 미정이다.

### 4.5 `DELETE /admin/services/{productCode}`

- 활성 항목을 물리적으로 제거하지 않고 `Deleted=true`와 `DeletedUtc`를 기록한다.
- 로컬 `InstanceId`를 `OriginInstanceId`로 기록한다.
- 성공 후 즉시 동기화 사이클을 예약한다.
- 없거나 이미 삭제된 항목은 `1001 NOT_FOUND`다.

### 4.6 `GET /admin/sync`

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <SyncStatus>
    <Enabled>true</Enabled>
    <PairingState>Enabled</PairingState>
    <PeerEndpoint>http://10.0.0.2:21000</PeerEndpoint>
    <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
    <KeyEpoch>1</KeyEpoch>
    <LastSyncUtc>2026-07-17T02:00:00Z</LastSyncUtc>
    <LastResult>OK</LastResult>
    <ClockSkewSeconds>2</ClockSkewSeconds>
  </SyncStatus>
</Response>
```

`LastResult`에는 성공한 마지막 사이클의 `OK`, 마지막 오류 코드 이름 또는 한 번도 sync를 시도하지 않은 `NOT_RUN`을 기록한다. `NOT_RUN`일 때 `LastSyncUtc`와 `ClockSkewSeconds` 요소는 생략한다.

### 4.7 `POST /admin/sync/enable`

요청 초안:

```xml
<EnableSync>
  <PeerEndpoint>http://10.0.0.2:21000</PeerEndpoint>
  <RePair>false</RePair>
</EnableSync>
```

- `Unpaired`에서 호출하면 지정한 endpoint만 허용하는 5분 페어링 창을 열고 `PairingWindowOpen`으로 전이한다. 운영자는 상대 서버에서도 해당 endpoint를 반대로 지정해 독립적으로 호출해야 한다.
- `PeerEndpoint`는 scheme `http`, IPv4·IPv6 literal host와 고정 포트 `21000`만 허용하며 userinfo·DNS hostname·path·query·fragment를 금지한다. IPv6 URI는 대괄호 표기를 사용하고 파싱 후 canonical IP로 저장한다.
- `RePair=true`는 기존 관계를 먼저 비활성화하고 새 페어링을 시작한다. `config.xml`에 남긴 마지막 발급 epoch보다 1 큰 epoch를 예약하며 실패·취소·timeout 시 상태는 `Unpaired`가 되고 이전 root를 다시 사용하지 않는다.
- 성공적인 페어링 commit 뒤 양쪽은 `PairedDisabled`다. 이 상태에서 같은 endpoint로 다시 호출하면 로컬을 `Enabled`로 전이한다. 양쪽이 `Enabled`일 때만 일반 handshake와 exchange가 성공한다.
- 이미 `Enabled`인데 `RePair=false`인 요청, 상태와 endpoint가 맞지 않는 요청은 `1002 CONFLICT`다.

SAS 확인은 `POST /admin/sync/pairing/confirm`, 취소는 `POST /admin/sync/pairing/cancel`을 사용한다. confirm 요청은 현재 화면에 표시된 `PairingId`와 `<Confirmed>true</Confirmed>`만 보내며 SAS 자체를 전송하지 않는다. 각 서버의 운영자가 로컬 화면에서 정확히 8자리인 SAS가 상대 화면과 같은지 독립적으로 확인한 뒤 호출한다. `/admin/sync`는 페어링 중에만 메모리의 `PairingId`, SAS, 만료 시각을 운영자에게 반환하며 영속화하지 않는다. 자세한 상태 전이와 암호 계약은 §5.3을 따른다.

### 4.8 `POST /admin/sync/disable`

- 로컬 상태가 `Enabled`이면 아직 로컬을 비활성화하지 않은 채 기존 유효 session을 재사용하거나 새 인증 handshake로 session을 확보한 뒤 `POST /api/sync/release`를 통지한다. session 확보·release가 실패해도 네트워크 재시도를 무한 대기하지 않고 로컬 비활성화를 계속한다. 이미 `PairedDisabled`이면 release를 생략한다.
- 기본 요청은 pair root를 유지한 채 로컬을 `PairedDisabled`로 영속화한다. 피어는 release를 받으면 자신도 `PairedDisabled`로 전이한다.
- 선택 요청 `<DisableSync><ForgetPeer>true</ForgetPeer></DisableSync>`는 가능한 경우 `POST /api/sync/revoke`를 상대에 보낸 뒤 로컬 `secrets/peer.dat`을 제거하고 `Unpaired`로 전이한다. revoke 실패 여부와 관계없이 로컬 폐기는 완료하되 원격 미확인을 상태 화면에 표시한다. `RePair=true`도 기존 root에 대해 같은 폐기 절차를 먼저 수행한다.
- 로컬 처리 성공과 원격 통지 실패를 상태 화면에서 구분한다.

### 4.9 `POST /admin/sync/now`

활성 상태에서 핸드셰이크부터 전체 사이클을 한 번 수행한다. 피어별 활성 sync 세션은 하나뿐이며 이미 자동·수동 sync가 실행 중이면 병행하거나 기존 작업을 공유하지 않고 `1002 CONFLICT`를 반환한다. 이 엔드포인트에는 공통 변경 제한과 별도로 `2회/분` 제한을 적용한다.

### 4.10 `GET /admin/settings/logging`

현재 시스템 파일 로그 보존기간을 일 단위로 반환한다.

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <LoggingSettings>
    <LogRetentionDays>30</LogRetentionDays>
  </LoggingSettings>
</Response>
```

예제의 `30`은 현재 설정값의 예시이며 설치 기본값을 뜻하지 않는다.

### 4.11 `PUT /admin/settings/logging`

요청:

```xml
<LoggingSettings>
  <LogRetentionDays>30</LogRetentionDays>
</LoggingSettings>
```

처리 규칙:

- `LogRetentionDays`는 1 이상의 정수여야 한다.
- 0, 음수, 정수가 아닌 값과 overflow는 `1000 BAD_REQUEST`다.
- 유효한 값은 `config.xml`에 원자적으로 영속화한 뒤 즉시 보존 정리를 실행한다.
- 오늘을 포함해 최근 `LogRetentionDays`개의 시스템 로컬 날짜 파일을 보존한다.
- 정리 대상은 `%ProgramData%\DEEPAi\ServiceDirectory\logs\system\` 바로 아래에서 `dpai-sd_yyyy-MM-dd.log`와 정확히 일치하는 파일뿐이다.
- 설정 저장 뒤 정리가 실패하면 설정값은 유지하고 `3000 INTERNAL`을 반환한다. 같은 PUT을 재시도하면 저장된 값으로 정리를 다시 시도할 수 있다.
- 성공 응답은 GET과 같은 `LoggingSettings` payload를 반환한다.
- 설치 기본값과 최대 허용 일수는 아직 미정이다.

파일명과 레코드 시각, 이벤트 목록의 단일 원본은 [개발계획의 시스템 로그 정책](./서비스디렉토리_개발계획.md#9-시스템-로그-정책)이다.

## 5. 피어 동기화 데이터

### 5.1 내부 동기화 레코드

```xml
<Service>
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServerAddress>10.0.0.5</ServerAddress>
  <Port>21500</Port>
  <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
  <Deleted>false</Deleted>
  <OriginInstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</OriginInstanceId>
</Service>
```

| 필드 | 규칙 |
|---|---|
| `Name` | trim 후 1~128 Unicode scalar, UTF-8 최대 512바이트, 제어문자 금지 |
| `ProductCode` | trim 후 `ToUpperInvariant()`, `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII, `OrdinalIgnoreCase` 유일 키 |
| `ServerAddress` | IPv4·IPv6 literal 또는 최대 253자 ASCII DNS hostname. scheme·path·query·port 포함 금지 |
| `Port` | 정수 `1..65535` |
| `LastModifiedUtc` | 활성 레코드 비교 시각 |
| `DeletedUtc` | 톰스톤 비교 시각. `Deleted=true`일 때 필수이고 `false`일 때 요소를 생략 |
| `OriginInstanceId` | 이 값을 마지막으로 생성·수정·삭제한 설치 인스턴스 |

레코드에 변경 출처를 저장하지 않고 현재 송신자의 `InstanceId`만으로 동시각을 판정하면 재전송 경로에 따라 결과가 바뀔 수 있으므로 금지한다.

### 5.2 동기화 실행 시점

1. 등록 또는 수정 승인 직후
2. 삭제 직후
3. 10분 주기
4. 서비스 기동 시 1회
5. 관리자의 수동 실행

모든 사이클은 핸드셰이크부터 시작한다. 즉시 동기화 실패는 10분 주기와 수동 실행으로 보정한다.

### 5.3 최초 페어링과 key epoch

최초 페어링은 AD나 인증서에 의존하지 않는 다음 상태 머신을 사용한다.

```text
Unpaired → PairingWindowOpen → Negotiating → SasPending → BothConfirmed
         → PairedPendingCommit → PairedDisabled → Enabled
```

- `POST /admin/sync/enable`로 양쪽 서버에 각각 5분 페어링 창을 연다. 창은 시스템 로컬 벽시계 변경에 영향받지 않는 monotonic elapsed time으로 만료시키며 지정한 상대 endpoint에서 온 요청만 받는다.
- `POST /api/sync/pairing/hello`의 initiator가 CSPRNG로 128-bit `PairingId`, 256-bit nonce와 일회성 ECDH P-256 키 쌍을 만들고, responder도 별도의 256-bit nonce와 일회성 키 쌍을 만든다. 양쪽은 자기 `LastPeerKeyEpoch`도 hello에 포함한다. 공개키 wire 형식은 Windows CNG `BCRYPT_ECCPUBLIC_BLOB`인 `CngKeyBlobFormat.EccPublicBlob`로 고정한다. P-256은 8바이트 header와 32바이트 X·Y 좌표를 합한 정확히 72바이트여야 하며 ECDH P-256 public magic, key length와 곡선 위 점을 검증한다.
- 양쪽이 동시에 hello를 시작하면 정규화한 `InstanceId`의 Ordinal 값이 작은 쪽만 initiator를 유지하고 큰 쪽은 자기 outbound 시도를 취소한 뒤 responder가 된다. 두 `InstanceId`가 같으면 복제된 설치로 보고 페어링을 거부한다. 서버별 진행 중인 pairing은 하나만 허용하고 열린 5분 창에서 hello는 최대 3회만 받는다.
- 새 `KeyEpoch` 후보는 `max(initiator LastPeerKeyEpoch, responder LastPeerKeyEpoch) + 1`이다. 어느 쪽 값이 unsigned 64-bit 최댓값이면 페어링을 거부한다. transcript는 `PairingId`, 양쪽 nonce, 양쪽 정규화된 `InstanceId`, 양쪽 명시적 endpoint, 양쪽 공개키, 양쪽 `LastPeerKeyEpoch`, 새 `KeyEpoch`, 알고리즘 식별자 `DPAI-SD-ECDH-P256-HMAC-SHA256-v1`을 initiator/responder 순서로 포함한다. 각 값은 4바이트 big-endian 길이와 값의 UTF-8 또는 원시 바이트를 이어 붙이는 length-prefix 형식이며 양쪽이 동일한 `SHA-256` transcript hash를 계산해야 한다.
- transcript의 endpoint는 `http://{canonical-ip}:21000` ASCII 형식으로 고정하고 trailing slash를 넣지 않는다. IPv4는 canonical dotted decimal, IPv6는 RFC 5952 의미의 소문자 압축 주소를 대괄호로 감싼다. DNS 이름과 zone identifier는 허용하지 않는다.
- 양쪽은 `ECDiffieHellmanCng`의 hash KDF를 SHA-256으로 고정하고 `SecretPrepend=ASCII("DPAI-SD-PAIR-K0-v1")`, `SecretAppend=TranscriptHash`로 설정한 `DeriveKeyMaterial(peerPublicKey)` 32바이트 결과를 `K0`로 사용한다. `HMAC-SHA256(K0, purpose-label || TranscriptHash)`로 `pair-confirm-initiator-v1`, `pair-confirm-responder-v1`, `pair-sas-v1`, `pair-root-v1` 목적별 키를 분리하며 한 목적의 MAC이나 키를 다른 목적으로 재사용하지 않는다.
- 양쪽은 `POST /api/sync/pairing/key-confirm`에서 각 방향 confirmation key로 transcript hash를 MAC해 실제로 같은 ECDH secret을 가진 것을 확인한다. MAC은 고정 시간으로 비교하며 확인이 끝나기 전에는 SAS를 신뢰하거나 표시하지 않는다.
- SAS는 정확히 8자리 십진수다. `K_sas=HMAC-SHA256(K0, ASCII("pair-sas-v1") || TranscriptHash)`로 만들고, counter 0부터 `HMAC-SHA256(K_sas, ASCII("sas-digits-v1") || TranscriptHash || UInt32BE(counter))`의 첫 4바이트를 unsigned big-endian 정수로 읽는다. 값이 `4,200,000,000` 이상이면 counter를 증가시켜 다시 계산하고, 미만인 첫 값을 `100,000,000`으로 나눈 나머지를 선행 0 포함 8자리로 표시한다. 두 서버가 로컬에서 독립적으로 계산하며 SAS 값을 네트워크로 보내지 않는다. 양쪽 운영자는 두 화면의 SAS와 PairingId가 같은지 직접 확인하고 각 서버의 `POST /admin/sync/pairing/confirm`을 별도로 실행한다.
- 로컬·원격 확인 결정은 방향별 pairing confirmation key로 MAC한 `POST /api/sync/pairing/decision`으로 교환한다. 두 확인을 모두 검증한 뒤에만 `BothConfirmed`로 전이한다.
- 양쪽 SAS 확인이 끝나면 pair root와 `PairingId`, transcript hash, peer binding, 새 key epoch를 DPAPI로 보호한 `PairedPendingCommit` 레코드로 먼저 원자 저장하고 `config.xml`의 `LastPeerKeyEpoch`도 같은 복구 작업에서 해당 epoch로 증가시킨다. 이 시점 뒤 실패·취소가 나도 epoch를 되돌리거나 재사용하지 않는다. 그 뒤 `POST /api/sync/pairing/commit`을 해당 root로 MAC한 멱등 요청·응답으로 교환한다. 양쪽 commit 확인을 저장한 뒤에만 `PairedDisabled`로 전이하며 어느 단계에서도 sync를 허용하지 않는다.
- `BothConfirmed` 전의 취소, 검증 실패, 재시작 또는 5분 timeout은 비밀 메모리를 지우고 `Unpaired`로 돌아간다. `PairedPendingCommit`은 재시작 뒤 같은 PairingId로 commit/status만 재시도할 수 있도록 최대 24시간 보존하고, 한쪽만 완료된 상태에서는 sync를 계속 금지한다. 24시간 안에 양쪽 commit을 확인하지 못하면 운영자에게 불일치 상태를 표시하고 명시적 취소·재페어링을 요구한다. 가능한 경우 인증된 abort를 상대에게 통지한다.
- `KeyEpoch`는 `1..18446744073709551615` 범위의 unsigned 64-bit 정수다. 양쪽이 합의한 후보는 §5.3의 `PairedPendingCommit` 원자 저장 시 발급한 것으로 확정한다. 페어링 자격 증명을 폐기해도 `LastPeerKeyEpoch`는 삭제하거나 감소시키지 않는다. 최댓값에 도달하면 새 페어링을 거부하고 새 설치 `InstanceId`를 만드는 명시적 복구 절차를 요구한다. 새 commit 뒤 이전 root·epoch로 서명한 요청은 모두 거부한다.
- 저장된 peer endpoint·`InstanceId`가 변경됐거나, 설치 복제·pair root 유출이 의심되거나, DPAPI 복호화·ACL 검증이 실패하면 기존 관계로 자동 fallback하지 않고 명시적 폐기와 재페어링을 요구한다.

pair root, `KeyEpoch`, transcript hash, 양쪽 `InstanceId`·endpoint binding, pairing state와 local·remote commit 확인 여부는 `%ProgramData%\DEEPAi\ServiceDirectory\secrets\peer.dat`에 DPAPI `LocalMachine` 범위로 암호화해 원자적으로 저장한다. 파일 ACL은 메인 서비스 SID, `SYSTEM`, 로컬 `Administrators`만 읽고 쓸 수 있게 한다. `LocalMachine` 보호는 파일을 읽을 수 있는 로컬 고권한 주체까지 막아 주지 않으므로 이 ACL을 완화하지 않는다. ECDH private key, 공유값, `K0`, 목적별 임시 키와 SAS는 영속화하지 않고 해당 단계가 끝나거나 실패하면 메모리에서 제거한다. pair root 원문은 API·UI·로그에 노출하지 않는다.

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

1. protocol version
2. 방향 문자열 `request`
3. sender `InstanceId`
4. receiver `InstanceId`
5. key epoch
6. session ID. handshake·revoke 요청은 길이 0
7. 대문자 HTTP method
8. 정규화된 path와 query
9. 소문자로 정규화한 content type
10. 수신한 raw body bytes의 SHA-256 32바이트
11. timestamp
12. nonce 16바이트

query는 UTF-8 RFC 3986 percent-encoding을 사용해 이름·값 순으로 정렬하고 중복 값도 보존한다. GUID는 소문자 `D`, key epoch는 앞자리 0이 없는 invariant decimal, 시각은 위 고정 형식으로 canonicalize한다. `Content-Encoding`은 지원하지 않으며 body hash는 XML 재직렬화 결과가 아니라 전송된 원문 바이트를 대상으로 한다.

canonical response bytes도 같은 length-prefix 방식을 사용하며 순서는 다음과 같다.

1. protocol version
2. 방향 문자열 `response`
3. 응답 sender `InstanceId`
4. 응답 receiver `InstanceId`
5. key epoch
6. 발급되었거나 요청에서 검증한 session ID. revoke 응답은 길이 0
7. 원 요청의 대문자 HTTP method
8. 원 요청의 정규화된 path와 query
9. HTTP status의 앞자리 0 없는 decimal
10. 소문자로 정규화한 응답 content type
11. raw response body SHA-256 32바이트
12. response timestamp
13. 새 CSPRNG response nonce 16바이트
14. 원 요청 nonce 16바이트

응답도 대응하는 `X-DPAI-*` 헤더에 responder identity, epoch, session, timestamp, response nonce와 signature를 보낸다.

pair root로부터 `HMAC-SHA256`을 사용해 handshake request, handshake response, session request, session response, revoke request, revoke response의 서로 다른 purpose label과 key epoch를 결합한 키를 각각 파생한다. session 키에는 양쪽 handshake nonce와 발급된 session ID도 결합한다. revoke 키는 session 없이 현재 peer binding과 key epoch에만 묶는다. 방향·목적별 키를 서로 재사용하지 않는다.

검증 순서는 다음과 같다.

1. listener·endpoint, 본문 크기, 필수 헤더 형식, 설정된 peer `InstanceId`와 key epoch를 확인한다.
2. raw body hash와 canonical bytes를 만들고 HMAC을 고정 시간으로 비교한다. XML은 아직 parse하지 않는다.
3. timestamp가 수신 시각 기준 ±60초인지 확인한다.
4. 서명이 유효한 요청 nonce를 해당 peer·epoch·session의 replay cache에 원자적으로 선등록한다. 이미 존재하면 거부한다.
5. 그 뒤에만 XML을 안전한 설정으로 parse·검증하고 상태를 변경한다.

handshake·revoke nonce replay 항목은 수신 후 최소 10분, session nonce 항목은 적어도 해당 10분 session 만료까지 유지하고 cache 크기를 제한한다. 관계 폐기로 revoke cache가 사라져도 해당 root의 요청을 다시 허용하지 않는다. cache 포화 시 기존 항목을 조기 제거하지 않고 새 요청을 `429`로 거부한다. 인증 전에 발생한 크기·형식 오류와 유효한 키가 없는 요청에는 서명된 상세 오류를 반환하지 않는다.

#### 5.4.1 `POST /api/sync/handshake`

요청:

```xml
<Handshake>
  <ProtocolVersion>1.0-draft</ProtocolVersion>
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <PeerInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</PeerInstanceId>
  <KeyEpoch>1</KeyEpoch>
  <HandshakeNonce>base64-256-bit-random</HandshakeNonce>
  <UtcNow>2026-07-17T02:00:00Z</UtcNow>
  <SyncEnabled>true</SyncEnabled>
</Handshake>
```

수신자는 양쪽 상태가 `Enabled`인지, HMAC identity·실제 원격 endpoint·본문의 `InstanceId`와 저장된 peer binding이 모두 일치하는지, 시계 편차가 60초 이하인지 확인한다. 프로토콜 버전이 호환되지 않으면 세션을 발급하지 않는다.

성공 응답은 responder의 별도 256-bit nonce, CSPRNG 128-bit `SessionId`와 정확히 10분 뒤의 `ExpiresUtc`를 포함한다. 새 session은 성공 응답이 인증된 뒤 피어별 유일한 활성 session이 되며 이전 session은 폐기한다.

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Handshake>
    <InstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</InstanceId>
    <KeyEpoch>1</KeyEpoch>
    <HandshakeNonce>base64-256-bit-random</HandshakeNonce>
    <SessionId>base64-128-bit-random</SessionId>
    <ExpiresUtc>2026-07-17T02:10:01Z</ExpiresUtc>
    <UtcNow>2026-07-17T02:00:01Z</UtcNow>
    <SyncEnabled>true</SyncEnabled>
  </Handshake>
</Response>
```

### 5.5 `POST /api/sync/exchange`

exchange는 유효한 10분 session ID가 필수다. 발신자의 전체 스냅샷은 활성 레코드와 톰스톤을 포함하고 승인 대기 큐는 포함하지 않는다. 한 batch는 최대 1,000개이면서 요청·응답 각각 4 MiB 이하여야 한다.

```xml
<Exchange Mode="Push">
  <SyncData>
    <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
    <SnapshotId>6f248a04-cc3e-409a-b499-cb571e6d30b7</SnapshotId>
    <BatchIndex>0</BatchIndex>
    <TotalCount>1250</TotalCount>
    <IsLastBatch>false</IsLastBatch>
    <Items>
      <Service>
        <Name>Old App</Name>
        <ProductCode>WXYZ</ProductCode>
        <ServerAddress>10.0.0.7</ServerAddress>
        <Port>22000</Port>
        <LastModifiedUtc>2026-07-01T00:00:00Z</LastModifiedUtc>
        <Deleted>true</Deleted>
        <DeletedUtc>2026-07-15T09:30:00Z</DeletedUtc>
        <OriginInstanceId>9f2ed127-9834-42b4-a379-eaad9df8fcec</OriginInstanceId>
      </Service>
    </Items>
  </SyncData>
</Exchange>
```

- `SnapshotId`는 사이클마다 새 GUID이며 그 사이클의 불변 스냅샷을 식별한다. `BatchIndex`는 0부터 연속 증가하고 `TotalCount`는 모든 batch의 전체 항목 수이며 `IsLastBatch`는 마지막 batch에서만 `true`다.
- 1,000개 또는 4 MiB를 넘는 스냅샷은 반드시 여러 batch로 나눈다. 톰스톤도 항목 수와 크기에 포함한다. 한 항목만으로 4 MiB를 넘으면 손상 데이터로 처리하고 sync를 중단한다.
- `Mode=Push` 응답은 검증한 batch의 `SnapshotId`와 `BatchIndex` ACK를 반환한다. 수신자는 같은 session의 batch를 staging하고 ID, 정렬, 연속 index, 중복, `TotalCount`, 마지막 표식과 각 레코드를 모두 검증한다.
- 마지막 batch까지 모두 검증하기 전에는 어떤 수신 항목도 메모리 현재 스냅샷이나 XML에 게시하지 않는다. 누락·중복·불일치·서명 실패·session 만료가 있으면 전체 staging snapshot을 폐기한다.
- 전체 Push를 검증한 뒤 §5.8에 따라 한 번 병합·원자 영속화하고, 그 결과의 불변 서버 스냅샷 ID를 ACK에 반환한다. 호출자는 `Mode=Pull`과 그 snapshot ID·다음 `BatchIndex`를 보내고 동일한 메타데이터를 가진 응답 batch를 순서대로 받는다.
- 호출자도 모든 Pull 응답 batch를 인증·검증·staging한 뒤에만 한 번 병합·원자 게시한다. 양쪽의 `IsLastBatch=true` 처리와 최종 게시가 끝나기 전에 sync 성공으로 기록하지 않는다.
- 최종 게시 시에는 공통 state mutation gate 안에서 최신 로컬 immutable snapshot과 검증된 원격 staging snapshot을 병합한다. 네트워크 교환 중 승인·삭제된 로컬 변경을 과거 송신 snapshot으로 덮어쓰지 않으며, 상대가 아직 받지 못한 로컬 변경은 다음 sync 사이클에서 전파한다.

한 batch인 경우에도 `SnapshotId`, `BatchIndex=0`, `TotalCount`, `IsLastBatch=true`를 반드시 포함한다. 사이클 중 새 관리 변경이 생기면 현재 immutable snapshot에는 섞지 않고 다음 사이클에서 수렴시킨다.

### 5.6 `POST /api/sync/release`

요청:

```xml
<Release>
  <InstanceId>7a1c3bb2-9e8b-4a8d-b404-f670f746eb77</InstanceId>
  <SessionId>base64-128-bit-random</SessionId>
</Release>
```

유효한 10분 session과 HMAC을 가진 현재 피어 요청만 허용한다. 수신자는 동기화 비활성 상태를 영속화한 뒤 서명된 성공 응답을 반환하고 이후 exchange를 거부한다. session이 없거나 만료됐거나 다른 peer·epoch에서 발급됐으면 XML 처리 전에 거부한다.

### 5.7 `POST /api/sync/revoke`

이 엔드포인트는 sync session이 없거나 이미 비활성인 상태에서도 양쪽의 페어링 관계를 폐기할 수 있게 한다. 현재 pair root에서 파생한 전용 revoke request·response key를 사용하며 일반 session key나 unsigned 요청을 허용하지 않는다.

```xml
<Revoke>
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

ProductCode별 비교 시각은 활성 레코드의 `LastModifiedUtc`, 톰스톤의 `DeletedUtc`다.

| 로컬 | 원격 | 결과 |
|---|---|---|
| 없음 | 있음 | 원격 채택 |
| 있음 | 없음 | 로컬 유지 후 응답으로 전파 |
| 둘 다 있음 | 비교 시각이 다름 | 더 최신 레코드 채택 |
| 둘 다 있음 | 비교 시각이 같고 OriginInstanceId가 다름 | 정규화한 `OriginInstanceId`의 Ordinal 문자열 비교에서 큰 쪽 채택 |
| 둘 다 있음 | 비교 시각·OriginInstanceId·내용이 같음 | 그대로 유지 |
| 둘 다 있음 | 비교 시각·OriginInstanceId가 같고 내용이 다름 | `2005 REVISION_COLLISION`, 전체 exchange 중단 |

- GUID는 소문자 `D` 형식으로 정규화한 뒤 Ordinal 비교한다.
- revision collision은 정상 동시 변경이 아니라 손상 또는 잘못된 구현으로 취급한다. 수신 스냅샷을 게시·저장하지 않고 오류를 기록한다.
- 삭제와 수정 충돌도 같은 규칙을 적용한다.
- 승인 대기 큐는 동기화하지 않는다.
- 톰스톤은 시간 경과로 정리하지 않는다. 같은 ProductCode의 신규 등록 승인 때만 새 활성 레코드로 대체한다.
- 전체 병합 후보에서 `Deleted=false` 활성 서비스가 1,000개를 넘으면 `2006 DIRECTORY_CAPACITY`로 exchange 전체를 중단하고 staging snapshot을 폐기한다. 현재 메모리·XML은 바꾸지 않으며 운영자가 한쪽에서 충분한 활성 서비스를 삭제한 뒤 새 session으로 다시 sync한다.
- 로컬 변경 시각은 `max(UtcNow, 해당 키의 이전 비교 시각 + 최소 단위, 마지막 로컬 변경 시각 + 최소 단위)`로 만들어 로컬 시계 역행에도 단조 증가시킨다.
- 60초 이내 시계 편차에서도 실제 사건 순서와 LWW 결과가 다를 수 있다. 이 위험을 수용할지 논리 시계로 바꿀지는 1.0 전에 결정한다.

병합 구현은 교환법칙, 결합법칙, 멱등성과 결정성을 속성 테스트로 검증한다.

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

응답:

```text
OK
```

`START`, `STOP`, `RESTART`의 성공은 위 응답을 사용한다. `STATUS` 성공은 다음 형식으로 실제 `ServiceControllerStatus`를 반환한다.

```text
OK: RUNNING
```

허용 상태값은 `STOPPED`, `START_PENDING`, `STOP_PENDING`, `RUNNING`, `CONTINUE_PENDING`, `PAUSE_PENDING`, `PAUSED`다. 상태를 확인할 수 없으면 성공이나 `UNKNOWN`으로 숨기지 않고 오류를 반환한다.

오류:

```text
ERROR: 사용자에게 노출 가능한 일반 사유
```

- 알 수 없는 명령과 인수는 거부한다.
- pipe ACL은 `DEEPAi-ServiceDirectory-Operators`, 와치독 서비스 SID, `SYSTEM`, 로컬 `Administrators`에만 필요한 연결·읽기·쓰기 권한을 부여한다. 서버는 연결 뒤 호출자 identity가 이 ACL과 운영자 그룹 정책을 충족하는지 다시 확인한다.
- `Everyone`, `Users`, `Authenticated Users`, Anonymous에 연결 또는 쓰기 권한을 주지 않는다. 일반 사용자 트레이는 해당 사용자 또는 그 AD 그룹을 로컬 운영자 그룹에 추가해 허용한다.
- 요청 원문, OS 내부 경로, 스택 또는 시크릿을 응답하지 않는다.
- 와치독은 전용 Windows 가상 서비스 계정과 서비스 SID로 실행하고 메인 서비스 제어에 필요한 권한만 가진다. 메인 서비스도 별도 가상 서비스 계정을 사용하며 두 서비스를 `LocalSystem`으로 설치하지 않는다. 대상 Windows에서 가상 계정을 사용할 수 없는 예외가 확인되면 구현 전에 별도 계정과 보완 통제를 승인 기록한다.

## 7. 구현 전 확정 목록

- 외부 등록 요청의 승인·거절 결과 조회 및 보존
- 내부 API와 sync 프로토콜 버전 협상
- 내부·외부 API의 최종 HTTP 상태와 envelope 오류 매핑
- `LogRetentionDays` 설치 기본값과 최대 허용 일수
- 벽시계 LWW 위험 수용 또는 논리 버전 도입
