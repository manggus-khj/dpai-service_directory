# 서비스 디렉토리 계획 문서 안내

```text
최초 작성일: 2026-07-17
최종 변경일: 2026-07-20
revision: 2
```

> 문서 묶음 상태: 인증서 기반 목표 설계·외부/내부 API 계약 확정
> 구현 상태: PKI core 1차 소스(CA·Directory/service leaf·CSR 검증·serial·CRL·certificate ledger 상태·IPv4 endpoint identity)와 단위 테스트 소스를 추가했다. 2026-07-20 `Debug|x64` 솔루션 빌드는 경고·오류 없이 성공했으며 테스트 실행·Release·HTTPS·설치·실행 검증은 미완료

이 디렉터리는 서비스 디렉토리의 제품 설계, 인증서 전환 계획, 외부·내부 API와 Directory 구조 제품 전용 보안 기준을 관리한다. 사내 `Directory서비스_애플리케이션_하드닝_가이드` 개정에 따라 원격 평문 HTTP와 외부 승인 대기 계약을 사이트 CA·HTTPS·TOFU pin·1시간 1건 등록 모드·CSR 즉시 발급 계약으로 전환한다.

문서에서 “확정”은 목표 설계 결정이며 구현 완료가 아니다. PKI core 1차 소스와 테스트 소스는 추가됐지만 실제 서비스는 여전히 remote HTTP, 일일 API 키, `pending.xml`, approve/reject API와 승인 대기 UI를 사용한다. `Debug|x64` 컴파일 성공을 HTTPS·발급 API·설치·실행 또는 테스트 완료로 표시하지 않는다.

## 목표 운영 기준

- 현재 저장소 버전 값은 `v1.0.0 build 8`이다. 이후 버전과 build 번호 변경은 루트 `AGENTS.md` §12를 따르며, 일반 코드·계획 수정이나 빌드 체크·커밋·푸시만으로 변경하지 않는다.
- Milestone XProtect `2021 R1` 이상, .NET Framework 4.8, x64 전용
- 지원 OS는 x64 Windows Server 2019+ Standard·Datacenter Desktop Experience, Windows 10 1809+와 Windows 11 24H2+ Pro·Enterprise·IoT Enterprise. Server Core 제외
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
| 1 | [Directory Service 사용 애플리케이션 하드닝 가이드](./service-directory-01-hardening.md) | Directory 구조 제품 전용 추가 보안 기준 |
| 2 | [인증서 전환 변경계획](./service-directory-02-certificate-transition.md) | 새 Directory 전용 가이드에 따른 차이, 목표 상태와 구현 단계 |
| 3 | [서비스 디렉토리 개발계획](./service-directory-03-development.md) | 제품 구성, 데이터·복구·동기화 불변식과 전체 개발 순서 |
| 4 | [API 명세 안내](./service-directory-04-api.md) | 신뢰 경계와 endpoint 소유권 |
| 5 | [외부 애플리케이션 API 명세](./service-directory-04-api-01-external-application.md) | 주소 구성, TOFU·pin, 일일 키, CSR 발급·갱신, CRL과 대상 서비스 인증서 검증 |
| 6 | [내부 API 명세](./service-directory-04-api-02-internal.md) | 설정 UI 등록 모드, 와치독, CA 운영과 Peer 동기화 계약 |

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
| 1 | CA·leaf·CSR·serial·ledger·CRL PKI core | 진행 중 — `Debug|x64` 빌드 성공, 테스트 실행·실제 DPAPI/ACL 검증 미완료 |
| 2 | schema migration과 다중 파일 저장·복구 | 부분 선행 구현 — CA·ledger·CRL·backup·repair 복원은 연결, 등록 transaction은 미구현 |
| 3 | HTTPS listener와 설치·repair·upgrade | 대기 — repair CA 복원 진입점만 선행 구현 |
| 4 | External TOFU·등록 모드·즉시 발급·갱신 | 대기 |
| 5 | Admin·설정 UI의 pending 제거와 등록 모드 | 부분 선행 구현 — CA 상태·backup·원장·serial 폐기만 연결 |
| 6 | Peer HTTPS·동일 CA·rotation·폐기 전파 | 대기 |
| 7 | 지원 OS·Milestone 조합 릴리스 검증 | 대기 |

상세 종료 조건은 [인증서 전환 변경계획 §8](./service-directory-02-certificate-transition.md#8-구현-단계와-종료-조건)을 따른다.

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
| remote transport | HTTP/1.1 IP literal prefix | OS 보안 기본값의 TLS 1.2+ HTTPS, Directory 전용 DNS+IPv4 SAN |
| service identity | 단일 `ServerAddress` | 외부 앱이 선택·영속화한 `ServiceHostName`+`ServiceIpv4Address`, 등록 leaf에 exact 두 SAN |
| external registration | 일일 키, 승인 대기, `PendingId` | 전역 1시간·1건 등록 모드, CSR 즉시 등록·발급 |
| UI | 승인 대기와 등록 서비스 별도 화면 | 승인 대기 제거, 등록 서비스 화면에 등록 모드 |
| storage | `directory.xml`, `pending.xml`, `config.xml`, peer secret | pending 제거, CA·ledger·CRL·idempotency와 schema migration 추가 |
| installer | URL ACL·방화벽, certificate binding 없음 | site CA·leaf·encrypted backup·HTTPS binding과 rollback |
| Peer | HTTP + HMAC | HTTPS + 같은 HMAC, 동일 site CA 운영 |
| XSD/tests | 기존 pending wire에 고정 | 목표 CSR·certificate·registration-mode 계약으로 갱신 필요 |
| PKI core | CA·CSR·leaf·CRL 없음 | primitive, DPAPI CA key·metadata·ledger·CRL 저장, 암호화 backup, 상태·원장·serial 폐기 Admin/UI와 repair 복원 소스 추가. `Debug|x64` 빌드 성공; 등록·발급·HTTPS·Peer PKI·rotation 연결과 테스트·실행 검증 필요 |

구체적인 후속 변경 대상과 장애 검증은 [인증서 전환 변경계획](./service-directory-02-certificate-transition.md)을 따른다.
