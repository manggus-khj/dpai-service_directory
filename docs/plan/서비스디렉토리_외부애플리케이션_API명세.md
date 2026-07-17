# 서비스 디렉토리 외부 애플리케이션 API 명세

> 문서 상태: 1.0 초안
> 구현 상태: 미구현
> 대상 독자: 서비스 디렉토리에 자기 서비스 정보를 조회·등록하는 다른 애플리케이션 개발자
> 배포 범위: 사내 연동 개발자와 승인된 운영 담당자
> 최종 정리일: 2026-07-17

이 문서는 다른 애플리케이션이 서비스 디렉토리의 생존 여부를 확인하고, 제품코드에 해당하는 접속정보를 조회하고, 신규 등록 또는 변경 승인을 요청할 때 사용하는 독립 계약이다. 관리자 기능, 피어 동기화, XML 저장 구조는 이 계약에 포함하지 않는다.

## 1. 계약 범위

| 메서드와 경로 | 목적 | 데이터 변경 |
|---|---|---|
| `GET /api/health` | 서비스 디렉토리 응답 가능 여부와 API 계약 버전 확인 | 없음 |
| `GET /api/services?productCode={code}` | 승인 완료된 단일 서비스 접속정보 조회 | 없음 |
| `POST /api/registration` | 신규 등록 또는 기존 정보 변경 승인 요청 | 승인 대기 요청 생성 |

다른 애플리케이션은 `/admin/*`, `/api/sync/*`, Named Pipe, 서비스 디렉토리의 XML 파일에 의존하면 안 된다.

### 1.1 지원 규모와 호출 특성

- 승인 완료된 활성 서비스는 최대 **1,000개**를 정상 지원 범위로 한다.
- 외부 애플리케이션은 최초 로그인 시 생존 확인과 자기 ProductCode 조회·등록을 한 번 수행하는 정도의 저빈도 호출을 기본 사용 패턴으로 한다.
- 외부 애플리케이션의 설치·실행 인스턴스 수 자체에는 별도 지원 상한을 두지 않는다. 서버 보호는 §2.6의 ProductCode·원격 IP별 속도와 전체 동시 실행 제한으로 수행한다.
- 1,000개는 활성 서비스의 지원 규모다. 톰스톤 개수나 피어 동기화의 snapshot·batch 상한을 뜻하지 않으며, 해당 제한은 내부 API 계약에서 별도로 정의한다.

## 2. 연결과 보안

### 2.1 기본 연결

- 포트: TCP `21000`
- 프로토콜 의미: HTTP/1.1 요청·응답
- 기준 주소: `http://{ListenAddress}:21000`. `ListenAddress`는 설치된 IP literal이며 IPv6 URI에서는 대괄호로 감싼다. DNS hostname은 현재 listener 계약에 포함하지 않는다.
- 요청·응답 본문: `application/xml; charset=utf-8`
- 본문을 보내는 요청은 `Content-Type`을 지정하고, 클라이언트는 `Accept: application/xml`을 보낸다.
- API payload의 모든 시각은 UTC ISO 8601 형식(예: `2026-07-17T02:00:00Z`)이다. §2.3 일일 API 키의 날짜만 시스템 로컬 날짜를 사용한다.
- 현재 XML에는 namespace가 없다. namespace/XSD와 API 버전 경로는 1.0 확정 전에 결정한다.

### 2.2 운영 보안 요구

외부 애플리케이션용이라는 표현은 제한 없는 공개를 의미하지 않는다.

- 운영 통신은 승인된 폐쇄망에서 HTTP/1.1을 사용하며 TLS/HTTPS와 X.509 인증서를 구성하지 않는다. 이는 [개발계획 §8.2](./서비스디렉토리_개발계획.md#82-tls-미사용-예외-기록)의 승인된 전송 암호화 예외다.
- 클라이언트는 `https` fallback, 인증서 검증 우회 또는 HTTP→HTTPS redirect를 전제로 구현하면 안 된다.
- `GET /api/health`를 포함한 세 엔드포인트는 모두 §2.3의 일일 API 키를 요구한다.
- 일일 API 키는 별도 비밀값을 배포·저장하지 않고 ProductCode와 시스템 로컬 날짜로 생성한다. HTTP Basic, URL query key와 별도 bearer token은 사용하지 않는다.
- 서비스 조회와 등록 요청의 ProductCode는 일일 API 키에서 복원한 ProductCode와 일치해야 한다.
- listener는 설치 프로그램이 `config.xml`에 저장한 단일 IPv4·IPv6 literal `ListenAddress`에만 바인딩한다. 주소는 현재 로컬 Domain·Private 인터페이스에 할당된 non-loopback 값이어야 하며 누락·미할당·wildcard·Public 주소면 서비스 기동을 실패시킨다. wildcard `http://+:21000/`, `0.0.0.0`, 자동 주소 선택 또는 외부·비신뢰망 노출을 금지한다.
- Windows 방화벽 인바운드 규칙은 Domain·Private 프로필에서만 TCP `21000`을 허용하고 Public 프로필에서는 허용하지 않는다. 고정 CIDR 또는 원격 IP allowlist는 사용하지 않는다.
- 네트워크 프로필, 인터페이스 바인딩과 요청 원격 IP는 운영 경계와 rate-limit 기준일 뿐 호출자 인증·인가를 대신하지 않는다.
- health를 포함한 모든 엔드포인트에는 §2.6의 요청 속도와 동시 실행 제한을 적용한다.
- 브라우저 cross-origin 호출은 지원하지 않으며 CORS 허용 헤더를 보내지 않는다. 정적 파일, 디렉터리 목록, 설정과 백업 파일도 HTTP로 제공하지 않는다.
- `Server`, `X-Powered-By` 같은 제품·프레임워크 식별 헤더를 제거하거나 플랫폼이 허용하는 최소 정보로 제한한다.

### 2.3 일일 API 키 생성 계약

요청 헤더:

```http
X-DPAI-API-Key: {44-character Base64 value}
```

헤더는 정확히 한 번만 보내야 한다. 누락, 중복, 공백 포함, 44자가 아닌 값과 엄격한 Base64 디코딩에 실패한 값은 거부한다.

| 항목 | 규칙 |
|---|---|
| ProductCode | trim 후 `ToUpperInvariant()`로 정규화한 `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII 값 |
| LocalDate | `DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)` 의미의 호출 호스트 시스템 로컬 날짜, 8바이트 ASCII 값 |
| PlainText | `ASCII(ProductCode + LocalDate)`, 정확히 12바이트 |
| AES Key | `SHA-256(ASCII(LocalDate))`, 32바이트 |
| 암호 | AES-256-CBC, 128비트 block, PKCS#7 padding |
| IV | 요청마다 CSPRNG로 새로 생성한 16바이트 값 |
| TokenBytes | `IV || CipherText`, 32바이트 |
| API Key | `Convert.ToBase64String(TokenBytes)`, padding을 포함한 정확히 44자 |

호출 애플리케이션은 API 요청을 보내기 직전에 자기 시스템의 현재 로컬 날짜로 API 키를 새로 생성한다. 같은 ProductCode와 날짜라도 IV가 달라지므로 API 키 문자열은 매번 달라질 수 있다.

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

테스트 벡터의 고정 IV는 상호운용 테스트에만 사용한다. 운영 구현은 `RandomNumberGenerator` 계열 CSPRNG로 매 요청 새 IV를 생성해야 한다.

### 2.4 일일 API 키 검증 계약

서비스 디렉토리는 요청을 라우팅하거나 본문을 처리하기 전에 다음 순서로 검증한다.

1. `X-DPAI-API-Key` 헤더가 정확히 하나이며 44자 Base64인지 확인한다.
2. 32바이트로 디코딩하고 앞 16바이트를 IV, 뒤 16바이트를 CipherText로 분리한다.
3. 검증 시점 서비스 디렉토리 호스트의 시스템 로컬 날짜를 `yyyyMMdd`로 만든다.
4. 해당 날짜의 SHA-256 결과를 AES-256 key로 사용해 CBC/PKCS#7로 복호화한다.
5. 결과가 정확히 12바이트 ASCII이며 앞 4바이트가 유효한 ProductCode, 뒤 8바이트가 서버의 현재 LocalDate와 같은지 확인한다.
6. 서비스 조회와 등록 요청은 복원한 ProductCode가 쿼리 또는 XML의 정규화된 ProductCode와 같은지 추가 확인한다. health는 이 비교 대상이 없다.

서버의 현재 로컬 날짜만 허용하며 이전·다음 날짜에 대한 유예는 두지 않는다. 자정 경계에서 `401`을 받은 호출자는 새 로컬 날짜로 API 키를 다시 생성해 한 번 재시도할 수 있다. 참여 호스트는 동일한 timezone과 동기화된 시스템 시계를 사용해야 한다.

누락, 형식 오류, 복호화 실패, padding 오류, 날짜 불일치와 ProductCode 불일치는 모두 HTTP `401`과 동일한 `INVALID_API_KEY` 응답으로 처리한다. 어느 검증 단계에서 실패했는지 응답·시스템 파일 로그에 노출하지 않고 API 키 원문도 기록하지 않는다.

### 2.5 승인된 제한사항

이 일일 API 키는 폐쇄망에서 정상 연동 구현의 요청을 구분하기 위한 프로젝트 전용 검증값이다. 별도 비밀키, 공개키 또는 인증서의 배포·저장·교체는 필요하지 않다.

다음 제한은 [개발계획의 하드닝 예외](./서비스디렉토리_개발계획.md#83-external-일일-api-키-예외-기록)로 수용한다.

- 알고리즘과 ProductCode를 아는 주체는 해당 날짜의 API 키를 생성할 수 있으므로 호출 인스턴스를 암호학적으로 식별하지 않는다.
- 캡처한 API 키는 같은 서버 로컬 날짜 동안 재사용될 수 있다.
- API 키는 HTTP method, path와 body 전체를 결합하지 않으므로 요청·응답 무결성을 제공하지 않는다.
- 구현 알고리즘이나 이 문서가 승인되지 않은 주체에게 유출되면 검증값 생성이 가능하다.
- 따라서 폐쇄망 분리, 명시적 listener 바인딩, Windows 방화벽 프로필 제한, rate limit과 등록·수정 운영자 승인을 함께 적용한다.

### 2.6 요청 속도와 동시 실행 제한

모든 제한은 일일 API 키 검증 뒤 복원한 ProductCode와 실제 요청 원격 IP를 기준으로 서버에서 적용한다. IP는 제한 집계 키일 뿐 인증 정보가 아니다.

| 엔드포인트 | ProductCode 제한 | 원격 IP 제한 | burst |
|---|---:|---:|---:|
| `GET /api/health` | ProductCode+IP 조합당 30회/분 | 조합 제한에 포함 | 5 |
| `GET /api/services` | ProductCode당 12회/분 | 60회/분 | 별도 burst 없음 |
| `POST /api/registration` | ProductCode당 3회/분 | 20회/분 | 2 |

- ProductCode 제한과 IP 제한이 함께 있는 엔드포인트는 두 제한을 모두 통과해야 한다.
- External 요청의 전체 동시 실행 수는 **32개**로 제한한다.
- rate limit, burst 또는 동시 실행 제한을 초과하면 HTTP `429`, envelope `1004 LIMIT_EXCEEDED`와 `Retry-After` 헤더를 반환한다.
- `Retry-After`는 해당 요청이 다시 허용될 때까지 남은 초를 나타낸다. 클라이언트는 이 값보다 먼저 재시도하면 안 된다.
- 승인 대기 전체 cap은 해제 시각을 계산할 수 없으므로 이 절의 `Retry-After` 계약을 적용하지 않는다.

## 3. 공통 XML 계약

### 3.1 응답 envelope

모든 정의된 응답은 다음 envelope을 사용한다.

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message></Message>
  <!-- 엔드포인트별 payload -->
</Response>
```

| 필드 | 규칙 |
|---|---|
| `Result` | `OK` 또는 `ERROR` |
| `Code` | 성공은 `0`, 실패는 오류 코드 |
| `Message` | 사용자에게 보여 줄 수 있는 일반 설명. 스택, 내부 경로, API 키와 시크릿은 포함 금지 |

### 3.2 외부 오류 코드

| Code | 이름 | 의미 |
|---|---|---|
| 0 | `OK` | 성공 |
| 1000 | `BAD_REQUEST` | XML 파싱 실패, 필수 필드 누락, 값 형식 오류 |
| 1001 | `NOT_FOUND` | 제품코드가 없거나 삭제 상태 |
| 1002 | `CONFLICT` | 같은 제품코드의 서로 다른 대기 요청 등 상태 충돌 |
| 1003 | `INVALID_API_KEY` | 일일 API 키 누락 또는 검증 실패. 상세 실패 사유는 노출하지 않음 |
| 1004 | `LIMIT_EXCEEDED` | 요청 속도·동시 실행 또는 전체 승인 대기 1,000개 제한 초과 |
| 3000 | `INTERNAL` | 내부 오류. 구체적인 파일·예외 정보는 응답하지 않음 |

rate limit과 승인 대기 cap 초과는 HTTP `429`와 `1004 LIMIT_EXCEEDED`를 사용한다. 그 밖의 HTTP 상태와 envelope 오류 매핑은 §3.3에 따라 1.0 확정 전에 결정한다.

### 3.3 현재 HTTP 상태 규칙

기존 초안과의 경로·행위 호환을 위해 논리 오류는 HTTP `200`과 envelope `Code`로 구분한다. 일일 API 키 실패는 `401`, rate limit·동시 실행·승인 대기 cap 초과는 `429`, 접근 등급 위반은 `403`, 정의되지 않은 경로는 `404`, 처리되지 않은 내부 오류는 `500`이다.

이 규칙은 외부 통합에 중요한 결정이며 아직 전체가 확정되지 않았다. 위에서 고정한 `401`과 `429`를 제외한 논리 오류를 1.0 확정 전에 `400`, `403`, `404`, `409`, `413`, `415` 같은 표준 상태로 전환할지 결정해야 한다.

### 3.4 외부 서비스 DTO

```xml
<Service>
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServerAddress>10.0.0.5</ServerAddress>
  <Port>21500</Port>
  <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
</Service>
```

`Deleted`, `DeletedUtc`, 변경 출처와 피어 식별자는 내부 동기화 필드이므로 외부 DTO에 포함하지 않는다.

## 4. 엔드포인트

### 4.1 `GET /api/health`

서비스 디렉토리가 요청을 받을 수 있는지 확인한다. 제품 빌드·패치 버전은 노출하지 않고 API 계약 버전만 반환한다.

응답:

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <ApiVersion>1.0-draft</ApiVersion>
  <UtcNow>2026-07-17T02:00:00Z</UtcNow>
</Response>
```

헬스체크도 일일 API 키가 필요하다. 외부 애플리케이션은 자신의 ProductCode를 사용하고, 와치독은 health 전용 구성요소 코드 `WDOG`를 사용해 같은 방식으로 생성한다. `WDOG`는 디렉토리 등록 ProductCode가 아니라 health 호출용 프로토콜 상수다. health는 쿼리·본문 ProductCode가 없으므로 복호화된 ProductCode의 형식과 날짜만 검증한다.

### 4.2 `GET /api/services?productCode={code}`

승인 완료된 서비스 하나의 접속정보를 조회한다.

- `productCode`는 필수이며 URL 인코딩한다.
- trim 후 정확히 4바이트 ASCII인지 확인하고 `ToUpperInvariant()`로 정규화한다. 비교는 `StringComparer.OrdinalIgnoreCase` 의미를 사용한다.
- 일일 API 키에서 복원한 ProductCode와 일치해야 한다.
- 승인 대기 중인 신규 항목, 삭제된 항목, 존재하지 않는 항목은 모두 `1001 NOT_FOUND`다.
- 수정 승인 대기 중에는 승인 전의 현재 등록값을 반환한다.

성공 응답:

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Service>
    <Name>VMS Bridge</Name>
    <ProductCode>ABCD</ProductCode>
    <ServerAddress>10.0.0.5</ServerAddress>
    <Port>21500</Port>
    <LastModifiedUtc>2026-07-17T02:00:00Z</LastModifiedUtc>
  </Service>
</Response>
```

### 4.3 `POST /api/registration`

신규 등록 또는 기존 정보 변경을 요청한다. 성공 응답은 요청 접수 또는 이미 등록되었음을 뜻하며, 승인 완료를 뜻하지 않는다.

일일 API 키에서 복원한 ProductCode와 요청 XML의 ProductCode가 일치해야 한다. ProductCode가 아직 등록되지 않았어도 API 키 형식과 날짜가 유효하면 등록 요청 검증을 통과할 수 있다.

요청:

```xml
<RegistrationRequest>
  <Name>VMS Bridge</Name>
  <ProductCode>ABCD</ProductCode>
  <ServerAddress>10.0.0.5</ServerAddress>
  <Port>21500</Port>
</RegistrationRequest>
```

클라이언트는 `LastModifiedUtc`, 삭제 상태, 요청 시각, 요청자 IP 같은 서버 소유 필드를 보낼 수 없다.

처리 규칙은 아래 우선순위대로 처음 일치하는 행 하나만 적용한다. 요청 동일성은 정규화한 `Name`, `ProductCode`, `ServerAddress`, `Port` 전체로 판단한다.

| 우선순위 | 현재 상태 | 요청 내용 | Status | 처리 |
|---|---|---|---|---|
| 1 | 같은 ProductCode의 대기 요청 존재 | 대기 요청과 동일 | `PENDING_EXISTS` | 중복 생성 없이 기존 `PendingId` 반환 |
| 1 | 같은 ProductCode의 대기 요청 존재 | 대기 요청과 다름 | 오류 `1002 CONFLICT` | 현재 활성값과 같더라도 기존 대기를 자동 교체하지 않음 |
| 2 | 대기 요청 없음, 미등록 | 모든 유효 값 | `PENDING_NEW` | New 대기 요청 생성 |
| 2 | 대기 요청 없음, 톰스톤 존재 | 모든 유효 값 | `PENDING_NEW` | New 대기 요청 생성. 승인 때 톰스톤 대체 |
| 2 | 대기 요청 없음, 활성 등록됨 | 현재 활성값과 동일 | `ALREADY_REGISTERED` | 변경 없음 |
| 2 | 대기 요청 없음, 활성 등록됨 | 현재 활성값과 다름 | `PENDING_MODIFY` | Modify 대기 요청 생성 |

성공 응답:

```xml
<Response>
  <Result>OK</Result>
  <Code>0</Code>
  <Message />
  <Status>PENDING_NEW</Status>
  <PendingId>b3f2d9f0-4c64-4dad-a855-44ea8f6e0a12</PendingId>
</Response>
```

- `PENDING_NEW`, `PENDING_MODIFY`, `PENDING_EXISTS`는 `PendingId`를 반드시 반환한다.
- `ALREADY_REGISTERED`에는 `PendingId`가 없다.
- 같은 내용으로 재시도하면 새 요청을 만들지 않으므로 네트워크 실패 뒤 동일 요청을 재전송할 수 있다.
- 대기 생성 시 서버는 해당 ProductCode의 현재 상태 revision을 함께 보존한다. 승인 전에 sync·삭제 등으로 기준 상태가 바뀌면 내부 API의 낙관적 동시성 규칙을 적용하고 조용히 덮어쓰지 않는다.
- 현재 계약만으로는 대기와 거절을 구분할 수 없다. 클라이언트는 서비스 조회로 승인 반영 여부만 확인할 수 있으며, 승인·거절 상태 조회 API와 결과 보존 기간은 1.0 확정 전 결정해야 한다.

승인 대기 제한:

- ProductCode당 승인 대기는 기존 처리 규칙과 같이 최대 1개다.
- 전체 New·Modify 승인 대기는 합계 **1,000개**로 제한하고 800개에 도달하면 운영 경고를 기록한다.
- 전체 cap에 도달해도 기존 대기와 동일한 요청은 새 항목을 만들지 않고 기존 `PendingId`와 `PENDING_EXISTS`를 반환한다.
- 전체 cap에서 새로운 대기 항목을 만들어야 하는 요청만 HTTP `429`, envelope `1004 LIMIT_EXCEEDED`로 거부하고 `Retry-After`는 보내지 않는다. 클라이언트는 자동 재시도하지 않고 운영자가 대기를 처리한 뒤 다음 로그인 또는 명시적 재확인 때 다시 요청한다. 기존 대기와 다른 같은 ProductCode 요청의 `1002 CONFLICT`, `ALREADY_REGISTERED`와 그 밖의 비생성 결과는 기존 규칙을 유지한다.

## 5. 입력 검증

수신 측은 클라이언트 검증 결과를 신뢰하지 않고 다시 검증한다.

| 필드 | 확정 규칙 |
|---|---|
| `Name` | 필수. trim 후 1~128 Unicode scalar, UTF-8 인코딩 시 최대 512바이트. 제어문자 금지. 비교는 Ordinal 대소문자 구분 |
| `ProductCode` | 필수. trim 후 `ToUpperInvariant()`, `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII, `OrdinalIgnoreCase` 유일 키 |
| `ServerAddress` | 필수. IPv4·IPv6 literal 또는 최대 253자의 ASCII DNS hostname. scheme, path, query와 port 포함 금지. trim 후 `OrdinalIgnoreCase` 비교 |
| `Port` | 정수 `1..65535` |

추가 요구:

- XML DTD와 외부 엔터티를 금지하고 안전한 reader 설정을 사용한다.
- 중복 필드, 필수 필드 누락, 잘못된 정수·시각은 `BAD_REQUEST`다.
- ProductCode의 비ASCII 문자, 영문·숫자 이외 문자, 4바이트 미만·초과와 일일 API 키 ProductCode 불일치를 거부한다.
- 모든 GET 요청은 body를 허용하지 않는다.
- `POST /api/registration` body는 UTF-8 원문 기준 최대 **16 KiB(16,384바이트)** 다.
- XML 문서의 최대 깊이는 root 요소를 포함해 **16**이다.
- 오류 응답이나 로그에 일일 API 키, AES key·IV·평문·암호문 중간값, 요청 원문 전체 또는 내부 예외를 남기지 않는다.

## 6. 외부 애플리케이션 권장 흐름

### 6.1 timeout과 재시도

- 클라이언트 연결 timeout은 **3초**다.
- 서버 처리 제한은 `GET /api/health`와 `GET /api/services` 각각 **5초**, `POST /api/registration` **10초**다.
- 연결 실패, timeout 또는 재시도 가능한 서버 오류에 대해 GET 요청과 동일 payload의 registration 요청은 최초 시도 뒤 최대 2회 재시도할 수 있다. 첫 재시도 전 1초, 두 번째 재시도 전 3초를 기다리고 각각 작은 무작위 jitter를 더한다.
- registration 재시도는 정규화된 XML payload를 바꾸지 않되 일일 API 키는 매 시도 새 IV로 다시 생성한다.
- HTTP `429`에 `Retry-After`가 있으면 일반 backoff보다 그 값을 우선한다. 헤더가 없으면 승인 대기 cap으로 보고 자동 재시도를 중단한다.
- 서버 로컬 자정 경계로 추정되는 `401 INVALID_API_KEY`는 현재 로컬 날짜로 키를 다시 생성해 한 번만 추가 재시도한다. 다시 `401`이면 중단한다.

### 6.2 권장 호출 흐름

1. 정규화된 4바이트 ProductCode와 현재 시스템 로컬 날짜로 새 일일 API 키를 생성한다.
2. `GET /api/health`로 연결과 지원 API 버전을 확인한다.
3. 요청마다 새 IV로 일일 API 키를 다시 생성하고 `GET /api/services?productCode={code}`로 현재 승인 정보를 조회한다.
4. 없거나 원하는 값과 다르면 새 일일 API 키로 `POST /api/registration`을 호출한다.
5. `PENDING_*`이면 운영자 승인이 필요함을 표시한다.
6. 승인 반영 확인 polling의 기본 간격은 60초이며 30초보다 짧게 설정할 수 없다. 시작 후 1시간이 지나면 자동 polling을 중단하고 다음 로그인 또는 사용자의 명시적 재확인 때 다시 조회한다.

## 7. 미확정 계약

다음 항목이 결정되면 이 문서의 버전과 예제를 함께 갱신한다.

- API 버전 경로 또는 미디어 타입과 호환성 정책
- 일일 API 키 알고리즘 변경 시 버전 식별과 구버전 병행·폐기 정책
- 등록 요청 상태 조회, 거절 사유 노출 범위, 결과 보존 기간
- HTTP 상태와 envelope 오류 코드의 최종 매핑
- XML namespace/XSD와 알 수 없는 요소 처리
