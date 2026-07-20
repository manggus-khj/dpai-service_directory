# 할 일 목록

```text
최초 작성일: 2026-07-20
최종 변경일: 2026-07-20
```

## AGENTS.md rev 9 적용 — 저장소 구조 정합화

- [x] 계획 문서 7건의 파일명에서 `service-directory-` 접두를 제거해 개명: `service-directory-00-overview.md`→`00-overview.md`, `service-directory-01-hardening.md`→`01-hardening.md`, `service-directory-02-certificate-transition.md`→`02-certificate-transition.md`, `service-directory-03-development.md`→`03-development.md`, `service-directory-04-api.md`→`04-api.md`, `service-directory-04-api-01-external-application.md`→`04-api-01-external-application.md`, `service-directory-04-api-02-internal.md`→`04-api-02-internal.md` (근거: AGENTS.md §2.2, §3.1) (완료: 2026-07-20)
- [x] 계획용 자산 폴더를 주인 md의 새 이름에 맞춰 개명: `docs/plan/service-directory-03-development/`→`docs/plan/03-development/`, `docs/plan/service-directory-04-api/`→`docs/plan/04-api/` (근거: AGENTS.md §2.1, §3.4) (완료: 2026-07-20)
- [x] 개명에 따른 `docs/plan/service-directory-*` 참조 경로 일괄 갱신: 계획 문서 간 상호 링크, `README.md`, `installer/README.md`, `src/DEEPAi.ServiceDirectory.ExternalProtocol/DEEPAi.ServiceDirectory.ExternalProtocol.csproj`, `src/DEEPAi.ServiceDirectory.InternalProtocol/DEEPAi.ServiceDirectory.InternalProtocol.csproj`, `src/DEEPAi.ServiceDirectory.Tray/DEEPAi.ServiceDirectory.Tray.csproj`의 XSD 경로. 갱신 후 저장소 전체에서 `service-directory-0` 패턴 재검색으로 누락 확인. 링크 외 내용이 바뀐 계획 문서는 §3.2에 따라 `revision`과 `최종 변경일` 갱신 (근거: AGENTS.md §2.2, §3.2) (완료: 2026-07-20)
- [x] 빌드 생성 파일 위치를 `artifacts/service-directory/`에서 `artifacts/` 바로 아래로 평탄화: `Directory.Build.props`의 `RepositoryArtifactsRoot`, `.gitignore`의 `/artifacts/service-directory/`, `tools/test.ps1`의 bin·test-results 경로, `tools/package.ps1`의 package·bin·obj 경로를 갱신하고, 갱신 후 `artifacts\service-directory` 패턴 재검색으로 누락 확인 (근거: AGENTS.md §2.2) (완료: 2026-07-20)
- [x] `README.md` 갱신: 계획 문서 표를 새 파일명 링크로 교체, 자산 폴더·artifacts 경로 설명을 새 구조로 수정, 할 일 목록 파일(`docs/plan/todo-01.md`)의 용도와 위치 소개 추가 (근거: AGENTS.md §1.1, §2.2, §3.5) (완료: 2026-07-20)

## 직접 지시 작업 기록

- [x] 로컬 빌드 잔재 삭제: 최상위 `TestResults/`(레거시 UI 미리보기 PNG 포함)와 `tests/DEEPAi.ServiceDirectory.Tests/bin`·`obj`. 세 경로 모두 Git 미추적(ignored) 확인 후 삭제했으며 저장소 변경 없음 (근거: 사용자 직접 지시, AGENTS.md §2.2 artifacts 일원화 방향) (완료: 2026-07-20)
- [x] 빌드 체크·커밋·푸시·배포파일 생성 자체와 그 실행 결과를 할 일 목록 기록 대상에서 제외하고, 해당 과정에서 실제 코드·문서 변경이 발생한 경우에만 변경 작업을 기록하도록 `AGENTS.md` §3.5·§4.2·§4.3을 개정하고 기존 빌드 체크 기록을 제거 (근거: 사용자 직접 지시) (완료: 2026-07-20)
- [x] `Debug|x64` 전체 테스트 실행: `powershell -NoProfile -File .\tools\test.ps1 -Configuration Debug`의 locked restore·solution 빌드와 x64 MSTest 568개가 모두 통과했고 `artifacts\test-results\Debug\`에 TRX 생성을 확인 (근거: 사용자 직접 지시, AGENTS.md §11) (완료: 2026-07-20)

## 인증서 전환 다음 개발 — `05-next-development.md` rev 1

- [x] 제품 미배포를 근거로 build 12 이하 단일 `ServerAddress`·`pending.xml` 형식을 개발 기준선으로 한정하고, 인증서 기반 목표 형식을 최초 정식 `SchemaVersion="1"`로 확정. 구형 데이터 자동 migration·운영자 매핑·호환 reader는 구현하지 않고 개발·테스트 데이터 루트를 명시적으로 초기화하며, 첫 정식 배포 뒤 변경부터 `N -> N+1` migration 적용 (근거: 05-next-development.md §3.1, 02-certificate-transition.md §5, 03-development.md §5.2·§5.4) (완료: 2026-07-20)
- [x] 최초 정식 저장 schema v1의 canonical XML, 파일 소유권, active issuer leaf DER exact replay ledger와 standby 공개 Peer PKI cache 분리, DER·DPAPI·backup 형식, 9개 recovery journal target·operation별 집합, 교차 파일 불변식과 초기화·복구 조건을 `03-development-01-storage-schema.md`로 확정 (근거: 사용자 직접 지시, 03-development-01-storage-schema.md) (완료: 2026-07-20)
- [ ] Windows Server 2016에서 build 12 fresh install, 메인·와치독 서비스가 이미 중지된 상태의 재설치, 일반 제거를 실행하고 ACL snapshot·복원 오류 재발 여부, SCM 등록·제거와 보존 데이터 상태를 기록 (근거: 05-next-development.md §4 순서 0)
- [ ] `external.xsd`와 External DTO·codec에 `/pki/ca`, `/pki/crl`, `ServiceHostName`·`ServiceIpv4Address`, CSR registration·certificate response와 renewal 계약을 반영하고 unknown XML·부분 identity·비정규 DNS/IPv4·IPv6 거부 테스트 소스를 추가 (근거: 05-next-development.md §5.1, 04-api-01-external-application.md)
- [ ] `admin.xsd`와 Admin DTO·codec에 registration-mode 조회·열기·닫기 계약을 반영하고 pending 조회·승인·거절 wire 모델을 제거하며 strict request/response 테스트 소스를 갱신 (근거: 05-next-development.md §5.1, 04-api-02-internal.md §4)
- [ ] `peer.xsd`와 Peer DTO·codec의 서비스 record를 canonical `ServiceHostName`·`ServiceIpv4Address` pair로 전환하고 PKI state high-water 계약과 strict codec 테스트 소스를 추가 (근거: 05-next-development.md §5.1, 04-api-02-internal.md §5)
- [ ] Domain의 `ServiceDefinition`·`ServiceRecord`·snapshot·sync 모델에서 단일 `ServerAddress`를 제거하고 `ServiceEndpointIdentity` DNS+IPv4 pair를 사용하도록 전환하며 Directory identity 혼용과 모든 IPv6 입력을 거부하는 단위 테스트를 추가 (근거: 05-next-development.md §5.1, 03-development.md §5.1)
- [ ] `03-development-01-storage-schema.md`에 따라 최초 정식 v1 directory·config·PKI state·active issuer ledger·standby Peer cache strict codec, leaf DER exact replay, DER·DPAPI 교차 검증과 9개 journal target을 구현하고 `pending.xml` target·serializer를 제거하며 golden bytes·canonical round-trip·fault-injection 테스트를 추가. build 12 이하 구형 v1 migration·운영자 매핑 경로는 만들지 않음 (근거: 03-development-01-storage-schema.md §4~§12, 05-next-development.md §5.2)
- [ ] registration·renewal exact replay 근거인 request ID, CSR hash, canonical semantic payload와 certificate 결과를 ledger에 영속화하고 같은 ID의 다른 payload 충돌 및 16 MiB canonical ledger 상한을 claim·serial 예약 전에 거부하는 테스트를 추가 (근거: 05-next-development.md §5.2, 04-api-01-external-application.md §9·§10, 03-development-01-storage-schema.md §7)
- [ ] 등록·재등록·삭제와 service definition이 바뀌는 갱신의 directory, certificate ledger, CRL, logical clock 변경을 mutation gate의 한 recovery transaction으로 결합하고, definition 불변 갱신·serial 단독 폐기에서는 directory를 쓰지 않으며 claim·serial·서명·파일 교체·COMMITTED·응답 직전 장애에서 serial 재사용과 CRL rollback이 없는지 검증 (근거: 05-next-development.md §5.2·§5.6, 03-development.md §6)
- [ ] process-local `CLOSED/OPEN/CLAIMED` registration-mode owner와 monotonic 1시간 deadline·재시작 닫힘·valid-request first-wins를 구현하고 invalid key·CSR·SAN·용량 거부가 창을 소비하지 않는 동시성 테스트를 추가 (근거: 05-next-development.md §5.3, 02-certificate-transition.md §3)
- [ ] Admin registration-mode 세 route·handler를 CA `READY`·active issuer 조건과 함께 연결하고 pending 세 route·application handler를 제거 (근거: 05-next-development.md §5.3, 04-api-02-internal.md §4)
- [ ] 설정 UI의 승인 대기 화면을 제거하고 `등록 서비스` 화면에 상태, `HH:mm:ss` countdown, 시작·종료, first-wins 경고와 마지막 성공 결과를 연결하되 tray context menu에는 등록 모드를 추가하지 않음 (근거: 05-next-development.md §5.3, 03-development.md §3.3)
- [ ] installer·repair에서 로컬 Management Server hostname/FQDN과 선택 IPv4를 검증해 Directory leaf의 exact DNS·IP SAN 및 DNS·IPv4 absolute CRL URI를 발급하고 HTTP.sys binding·URL ACL·방화벽·private-key ACL을 rollback 가능한 설치 state에 포함 (근거: 05-next-development.md §5.4, 03-development.md §10)
- [ ] 메인 서비스가 config identity, IPv4 할당·network profile, Directory leaf/private key, SAN·chain·validity와 HTTP.sys binding 검증 뒤에만 External·Peer HTTPS listener를 열도록 전환하고 remote HTTP·redirect·fallback을 제거하면서 Admin·WDOG exact loopback 경계를 유지 (근거: 05-next-development.md §5.4, 04-api-02-internal.md §2)
- [ ] HTTPS `/pki/ca`·`/pki/crl`과 등록 모드 기반 즉시 registration handler를 연결하고 일일 키·ProductCode·CSR·SAN·용량 검증, 원자 claim, 발급 transaction과 닫힌 모드 exact replay를 외부 계약대로 구현 (근거: 05-next-development.md §5.5, 04-api-01-external-application.md §3·§7·§9)
- [ ] 현재 유효·미폐기 leaf 개인키 proof와 새 CSR proof-of-possession을 검증하는 renewal을 구현하고 hostname 또는 IPv4 변경 시 변경 뒤의 완전한 pair만 허용하며 기존 `PendingId`·승인 대기 응답 의존을 제거 (근거: 05-next-development.md §5.5, 04-api-01-external-application.md §10)
- [ ] 서비스 삭제·열린 모드 재등록에서 current serial 폐기·CRL publish·directory tombstone/활성 record·ledger 상태를 원자 변경하고 감사 event 발생 시점을 durable commit 뒤로 고정 (근거: 05-next-development.md §5.6, 03-development.md §6.3·§9)
- [ ] Peer endpoint·outbound transport를 HTTPS IPv4로 전환해 TLS 검증을 ECDH·SAS·HMAC보다 먼저 수행하고, PKI state에서 CRL high-water·ProductCode별 current serial과 single active issuer를 검증해 standby Peer cache에 저장하되 full ledger·CA private key·등록 모드는 동기화하지 않음 (근거: 05-next-development.md §5.6, 04-api-02-internal.md §5.9)
- [ ] 인증된 동일-site backup으로 standby의 Directory leaf·공개 PKI cache를 구성하되 full ledger·CA key primary를 남기지 않고, 중지된 repair에서 관찰 high-water 이상인 backup만 복원해 issuer identity·role·revision과 cache 삭제를 원자 전환하는 명시적 승격 절차 구현 (근거: 03-development-01-storage-schema.md §9~§11, 04-api-02-internal.md §4.16)
- [ ] CA rotation을 제외한 지원 OS·Milestone 교집합에서 HTTPS only, TOFU/pin, 즉시 발급, lost-response replay, 갱신, 삭제·폐기, CRL, CA backup/repair와 일반 제거 보존 시나리오의 검증 결과를 관련 계획 문서에 기록 (근거: 05-next-development.md §4 순서 7, 03-development.md §12)
