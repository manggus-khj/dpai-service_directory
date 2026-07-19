# 서비스 디렉토리 API 명세 안내

> 문서 상태: 인증서 기반 목표 계약 확정
> 구현 상태: PKI core 1차 소스만 부분 구현. 현재 wire·XSD·listener는 평문 HTTP·승인 대기 계약이며 목표 명세로 전환 필요
> 최종 정리일: 2026-07-19

이 파일은 호출 주체와 신뢰 경계별 상세 명세의 색인이다. 인증서 전환 범위와 구현 순서는 [인증서 전환 변경계획](./서비스디렉토리_인증서전환_변경계획.md), 실제 요청·응답과 호출 절차는 아래 외부·내부 명세가 단일 원본이다.

## 목표 운영 기준

- Milestone Management Server 주소는 같은 서버에 설치된 Directory의 위치다. 외부 앱은 성공한 Milestone session에서 얻은 `DirectoryHostName`·`DirectoryIpv4Address`를 같은 Directory identity로 저장하고 `https://{DirectoryHostName}:21000` 또는 `https://{DirectoryIpv4Address}:21000`으로 접속한다. 이 두 값은 Directory 위치·TLS 검증 전용이며 등록할 서비스 주소가 아니다.
- 등록 서버 앱은 자기 서비스의 `ServiceHostName`과 `ServiceIpv4Address`를 사용자가 선택하게 하고 제한 ACL 설정에 한 쌍으로 영속화한다. 등록·조회 record, CSR와 등록 서비스 leaf SAN은 이 pair만 사용하고 Milestone/Directory 주소, TCP source IP와 DNS 역조회 값을 사용하지 않는다.
- Directory listener, Peer와 등록 서비스 주소는 IPv4만 지원한다. CA certificate 자체에는 endpoint SAN을 넣지 않고, Directory leaf와 등록 서비스 leaf는 각각 자기 DNS+IPv4 pair를 섞지 않고 사용한다.
- 모든 leaf의 CRL Distribution Point는 Directory identity로 만든 `https://{DirectoryHostName}:21000/pki/crl`과 `https://{DirectoryIpv4Address}:21000/pki/crl` 두 absolute URI다. 외부 응답의 `CrlUri=/pki/crl`은 현재 검증된 Directory base에 결합하는 상대 API path일 뿐 인증서 extension 값이 아니다.
- 연결정보 파일, Directory 주소·CA pin·ProductCode의 설치 입력을 사용하지 않는다.
- 첫 Directory 연결은 Directory leaf의 DNS·IPv4 SAN과 chain·CA 제약·키 강도를 검증한 제한적 TOFU이고, 이후에는 Milestone server identity별로 저장한 site CA와 SHA-256 SPKI pin을 강제한다.
- 원격 External·Peer API는 TLS 1.2 이상을 지원하는 OS 보안 기본값의 HTTPS만 허용한다. protocol·cipher suite를 앱 코드에 고정하지 않으며 평문 HTTP remote listener와 redirect fallback은 제공하지 않는다.
- Admin과 와치독 loopback은 로컬 IPC 경계로 유지하고 exact `127.0.0.1` endpoint, Negotiate·운영자 인가 또는 health 검증을 적용한다.
- External 일일 API 키 알고리즘은 유지한다. health·조회·등록 admission과 ProductCode 결합에 사용하지만 강한 caller identity·request signature로 표현하지 않는다.
- 외부 승인 대기는 제거한다. 관리자가 설정 UI의 등록 서비스 화면에서 ProductCode 입력 없이 전역 등록 모드를 열고, 1시간 안의 첫 유효 요청 한 건을 즉시 등록·인증서 발급한 뒤 닫는다.
- 서비스 삭제·재등록은 기존 인증서 serial 폐기와 CRL 갱신까지 완료해야 성공이다.
- URL·media type·XML에 API version 필드를 두지 않는다. 암호 domain label과 알고리즘 식별자는 API version이 아니다.
- External·Admin·Peer XML은 각 고정 namespace와 strict XSD를 사용한다. 현재 XSD는 목표 계약으로 아직 갱신하지 않았다.
- 인증·인가·endpoint·pin·CSR·발급·폐기 실패는 비밀값을 배제한 보안 감사 대상으로 한다.

## 상세 명세

| 문서 | 대상 | 포함 범위 |
|---|---|---|
| [외부 애플리케이션 API 명세](./서비스디렉토리_외부애플리케이션_API명세.md) | 조회 클라이언트와 등록 서버 앱 | Management Server session 기반 Directory 위치 구성, 별도의 등록 서비스 hostname·IPv4 pair, TOFU·pin, 일일 키, CA·CRL, 서비스 조회, 등록 모드 중 CSR 즉시 발급, pair 변경 재발급, 대상 서버 인증서 검증 |
| [내부 API 명세](./서비스디렉토리_내부_API명세.md) | 설정 UI, 와치독, 상대 Directory | 등록 모드 시작·종료·상태, 등록 서비스·인증서 폐기, CA 운영, Peer HTTPS·동기화, 로컬 서비스 제어 |

## endpoint 소유권

| 경계 | 호출 주체 | endpoint | 상세 문서 |
|---|---|---|---|
| PKI bootstrap | Milestone Management Server와 같은 서버의 Directory DNS·IPv4 pair를 이미 아는 외부 앱 | `GET /pki/ca`, `GET /pki/crl` | 외부 명세 |
| External 조회 | 일일 API 키와 저장 CA pin 검증을 통과한 외부 앱 | `GET /api/health`, `GET /api/services` | 외부 명세 |
| External 발급 | 열린 전역 등록 모드에서 자기 `ServiceHostName`·`ServiceIpv4Address`와 첫 유효 CSR을 제출하는 서버 앱 | `POST /api/registration` | 외부 명세 |
| External 갱신 | 현재 유효한 leaf private key proof를 가진 등록 서버 앱 | `POST /api/certificates/renew` | 외부 명세 |
| Admin | loopback Negotiate와 로컬 운영자 그룹 인가를 통과한 설정 UI | `/admin/*` | 내부 명세 |
| Peer | 같은 site CA·pin과 기존 ECDH/SAS/HMAC 계약을 통과한 상대 Directory | `/api/sync/*` | 내부 명세 |
| Local IPC | 로컬 인가된 설정 UI | `\\.\pipe\SvcDirWatchdog` | 내부 명세 |

와치독은 loopback `GET /api/health`의 응답 wire를 재사용하고 `WDOG` 일일 키를 사용한다. 원격 External trust bootstrap과 인증서 발급 권한은 갖지 않는다.

## UI와 API 경계

- 등록 모드 시작·종료는 트레이 context menu가 아니라 설정 UI의 `등록 서비스` 화면에서만 제공한다.
- 설정 UI 좌측 메뉴에서 `승인 대기`를 제거한다.
- 설정 UI는 `GET/POST /admin/registration-mode*`만 사용하며 process memory나 파일을 직접 읽지 않는다.
- 외부 앱은 등록 모드를 열 수 없고 상세 상태도 조회하지 않는다. 닫힌 신규 등록은 `REGISTRATION_MODE_CLOSED`로만 알 수 있다.
- 설치하는 사람은 ProductCode를 입력하지 않는다. 등록 요청 ProductCode는 앱 자체에 내장된 값이다.

## 분리 원칙

- 외부 앱은 외부 명세만으로 Directory 신뢰부터 대상 서버 인증서 검증까지 구현할 수 있어야 한다.
- 외부 DTO에는 톰스톤, LogicalVersion, peer identity, CA private key, ledger 저장 경로를 노출하지 않는다.
- Admin·Peer·파일 저장 계약을 외부 앱이 호출하거나 추측하지 않는다.
- `directory.xml`, certificate ledger, CRL 원자 교체와 journal은 [개발계획](./서비스디렉토리_개발계획.md)의 책임이다.
- Directory 주소를 신뢰한다는 사실은 certificate validation bypass 근거가 아니다. 최초 TOFU 뒤 저장 pin이 인증서 identity의 근거다.
- Directory 주소는 Directory를 찾고 검증하는 데서 역할이 끝난다. 서비스 등록과 발급에서는 외부 앱이 선택·저장한 자기 `ServiceHostName`·`ServiceIpv4Address`만 사용하며 두 identity 사이에 값을 복사하거나 추론하지 않는다.
- 일일 API 키 알고리즘 비공개를 암호학적 secret으로 표현하지 않는다. 발급 예외의 실제 보완 통제는 로컬 관리자가 연 1시간·1건 등록 모드와 HTTPS·CSR·폐기 절차다.

## 호환성과 구현 상태

배포된 외부 소비자가 없으므로 다음 기존 계약을 호환 유지하지 않는다.

- remote `http://` base와 TLS 미사용 예외
- CSR 없는 `POST /api/registration`
- `PENDING_NEW`, `PENDING_MODIFY`, `PENDING_EXISTS`, `PendingId`
- 외부 승인 대기와 Admin approve/reject endpoint
- HTTP canonical Peer endpoint

CA·Directory/service leaf·CSR 검증·serial·CRL·certificate ledger 상태·endpoint identity primitive와 단위 테스트 소스는 추가됐지만 현재 코드의 wire와 XSD는 아직 위 계약을 구현하지 않는다. core 소스만으로 빌드·실행·설치가 완료됐다고 표시하지 않으며, 후속 구현에서 XSD·DTO·상태·저장·listener·installer·UI·테스트를 같은 목표 계약으로 변경한다.
