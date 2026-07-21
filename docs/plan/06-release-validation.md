# 서비스 디렉토리 현장 검증 실행계획

```text
최초 작성일: 2026-07-21
최종 변경일: 2026-07-22
revision: 7
```

## 1. 목적과 범위

이 문서는 인증서 전환 소스의 자동 테스트가 끝난 뒤 실제 Windows 설치에서 수행할 비파괴 증거 수집과 수동 시나리오의 실행 순서를 정의한다. 제품 계약과 저장 불변식은 [개발계획](./03-development.md), API 상호운용은 [외부 API](./04-api-01-external-application.md)와 [내부 API](./04-api-02-internal.md)가 단일 원본이다.

검증 도구는 설치 성공을 대신하지 않는다. 설치·repair·제거 로그, 실제 TLS 연결, 외부 앱의 TOFU·pin 저장, 두 Directory의 페어링·동기화·승격과 Milestone 조합은 별도로 실행하고 기록해야 한다. CA key rotation·dual-pin은 [전용 구현계획](./07-ca-key-rotation.md)의 최초 릴리스 필수 범위이며, 구현 뒤 별도 상태·trust bundle·issuer별 CRL·maintenance 증거를 추가하지 않은 현재 도구만으로 완료 처리하지 않는다.

## 2. 설치 상태 검증 도구

설치 패키지는 다음 읽기 전용 도구를 `%ProgramFiles%\DEEPAi\ServiceDirectory\installer-support\`에 설치한다.

```text
ServiceDirectory.InstalledValidation.ps1
```

관리자 PowerShell에서 기존 파일이 없는 절대 로컬 JSON 경로를 지정한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "$env:ProgramFiles\DEEPAi\ServiceDirectory\installer-support\ServiceDirectory.InstalledValidation.ps1" `
  -OutputPath "C:\Temp\dpai-sd-installed-validation.json"
```

의도적으로 중지한 서비스 상태를 점검할 때만 `-AllowStoppedServices`를 추가한다. 이 옵션은 서비스 등록·계정·시작 모드·파일·ACL·HTTP.sys·인증서·방화벽 판정을 완화하지 않고 실행 상태만 허용한다.

도구는 어떤 시스템 구성도 만들거나 수정하지 않는다. 출력 JSON과 같은 경로의 일시 staging 파일만 생성하며 기존 보고서를 덮어쓰지 않는다. 실패 판정이 하나라도 있으면 보고서를 남기고 exit code `2`, 모두 통과하면 `0`을 반환한다. 도구 자체의 입력·출력 오류는 PowerShell 실패로 종료한다.

## 3. 자동 수집 범위

| 영역 | 판정·증거 |
|---|---|
| 실행 환경 | 관리자 토큰, x64 Windows와 installer 최소 build, .NET Framework 4.8 Release key |
| canonical 설정 | DTD를 금지한 bounded strict UTF-8 `config.xml`, `SchemaVersion="1"`, exact `ListenAddress`·`DirectoryHostName`·`DirectoryIpv4Address` pair |
| SCM | 메인·와치독 등록, exact 실행 파일·own-process type·virtual service account·unrestricted service SID, 메인 지연 자동/와치독 자동 시작, 기본 실행 중 상태 |
| 로컬 identity | 두 service SID, installer description과 일치하는 로컬 운영자 그룹, exact 보안 Event Log source 값·형식 |
| 설치 등록 | installer owner와 `ListenAddress`만 있는 exact registry 값 집합·형식 및 config 주소 일치 |
| 파일·ACL | 설치·데이터 root protected exact SID 집합, 필수 state/PKI 파일, forbidden legacy·secret backup 부재 |
| CA 역할 | `ACTIVE_ISSUER`의 ledger·CA key 및 standby cache 부재, `STANDBY`의 공개 cache 및 ledger·CA key 부재, 역할별 공개 XML hash |
| HTTP.sys | exact IPv4 `sslcert`, installer AppId, 유일한 thumbprint, LocalMachine leaf·private key·유효기간, IPv4·hostname 두 remote HTTPS와 loopback HTTP URL ACL의 exact service SID SDDL |
| 방화벽 | installer-owned 단일 rule의 inbound allow, Domain·Private only, edge traversal 차단, TCP 21000, exact local IPv4·main executable·main service와 unrestricted remote boundary |

일반 파일은 길이와 SHA-256을 보고서에 기록한다. `secrets\ca-a.key`, phase에 따라 존재하는 `secrets\ca-b.key`와 `secrets\peer.dat`은 존재 여부·길이만 기록하고 내용과 hash를 읽거나 출력하지 않는다. 보고서에는 컴퓨터 이름, Directory hostname·IPv4, certificate subject·thumbprint, ACL SDDL이 포함되므로 내부 운영 증거로 취급하고 외부 공개하지 않는다.

## 4. 라이브 External endpoint 검증 도구

설치 상태 보고서가 정상이고 메인 서비스가 실행 중인 장비에서는 다음 도구를 별도 새 로컬 JSON 경로로 실행한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "$env:ProgramFiles\DEEPAi\ServiceDirectory\installer-support\ServiceDirectory.LiveEndpointValidation.ps1" `
  -OutputPath "C:\Temp\dpai-sd-live-endpoint-validation.json"
```

도구는 시스템 구성을 변경하지 않고 다음 GET 요청만 수행한다. 연결은 항상 config의 `DirectoryIpv4Address`로 보내되 TLS target name을 IPv4와 `DirectoryHostName`으로 각각 지정해 두 SAN 경계를 독립적으로 확인한다. hostname은 DNS IPv4 결과에 구성 주소가 포함되는지도 확인한다.

- OS 기본 TLS 선택으로 협상한 protocol이 TLS 1.2 이상인지 확인
- HTTP.sys binding의 exact leaf thumbprint, target name mismatch 부재와 설치 CA의 leaf signature 확인
- 두 identity에서 `GET /pki/ca`의 strict success XML, CA DER·SPKI pin·`/pki/crl` 값이 설치 원본과 일치하는지 확인
- 두 identity에서 `GET /pki/crl`의 media type·DER bytes와 CA signature가 설치 원본과 일치하는지 확인
- 요청마다 새 IV로 만든 `WDOG` 일일 키를 사용해 `GET /api/health`의 strict success XML과 60초 이내 서버 UTC 확인
- HTTP/1.1, bounded ASCII header, 단일 canonical `Content-Length`, 중복 header·chunked framing 부재 확인

도구는 등록 모드를 열지 않고 등록·갱신·삭제·폐기·Peer API를 호출하지 않는다. API key·CA/CRL 원문은 보고서에 기록하지 않으며 leaf subject·thumbprint, SiteId와 CA/CRL SHA-256은 내부 증적으로 기록한다.

## 5. 도구가 완료로 판정하지 않는 범위

- 설치 상태 보고서만으로 실제 TLS handshake 완료로 보지 않으며 라이브 보고서를 함께 수집한다.
- 라이브 TLS 1.2+ 성공만으로 SSLv3·TLS 1.0·1.1 비활성이나 배포 OS cipher 정책 전체를 검증한 것으로 보지 않는다.
- 서비스가 `Running`이어도 health·Admin Negotiate·WDOG deadline·파일 로그를 성공으로 보지 않는다.
- 한 장비 보고서로 외부 앱 TOFU/pin·등록·갱신·폐기 또는 두 장비 Peer 동기화를 완료 처리하지 않는다.
- 설치 후 보고서로 repair·rollback·일반 제거 보존·전체 삭제를 완료 처리하지 않는다. 각 작업 전후에 별도 보고서와 설치 로그가 필요하다.
- Milestone 설치·session과 Directory 주소 전달은 별도 상호운용 실행이 필요하다.

## 6. 현장 실행 순서

1. Windows Server 2016에서 기존 build 12 EXE의 fresh install, 중지 서비스 재설치와 일반 제거를 실행해 이전 ACL snapshot 오류가 해소됐는지 설치 로그로 확인한다. 이 이전 package에는 새 검증 도구가 없으므로 도구 부재를 실패로 세지 않는다.
2. 사용자가 명시적으로 최신 package 생성을 요청한 뒤 생성된 EXE로 깨끗한 설치를 수행한다.
3. 설치 직후 installed-state와 live-endpoint 검증 도구를 순서대로 실행하고 두 JSON·Inno Setup log·Application Event Log를 함께 보존한다.
4. 서비스를 의도적으로 중지한 재설치 전후에는 `-AllowStoppedServices` 보고서를 각각 수집하고 성공 뒤 기본 실행 보고서를 다시 만든다.
5. repair 주소 변경 전후 보고서로 config·registry·URL ACL·HTTPS binding·leaf thumbprint·방화벽의 일관된 교체와 rollback을 확인한다.
6. active issuer와 standby 두 장비에서 각각 보고서를 수집한 뒤 페어링·PKI state·directory sync·명시적 승격과 장애 복구를 실행한다.
7. 외부 시험 앱에서 제한적 TOFU, 이후 pin 강제, 즉시 등록·lost-response replay·갱신·삭제/폐기·CRL 실패 정책을 검증한다.
8. [rotation 구현계획 §13](./07-ca-key-rotation.md#13-필수-테스트-행렬)에 따라 current CA `STABLE`부터 `PUBLISHED`, dual-pin 수집, `ACTIVATED`, 기존 service leaf의 새 CA renewal, terminal CRL과 완료 `STABLE`까지 수행한다. next pin을 받은 client와 놓친 client, 두 Directory 순차 leaf 전환, standby 승격과 old key 폐기 증거를 각각 보존한다.
9. 일반 제거 뒤 SCM·제품 registry·URL ACL·HTTPS binding·방화벽·Program Files 제거와 ProgramData의 current·rotation·retired CA 보존을 확인한다. 전체 삭제는 별도 장비에서 이중 확인 뒤 두 slot key·retired archive·client trust cache 완전 삭제를 검증한다.
10. 지원 OS·Milestone 조합별 결과, 실패 로그, 보완 조치와 재검증 결과를 이 문서와 [TODO](./todo-01.md)에 반영한다.

## 7. 구현 상태와 남은 조건

2026-07-21 installer payload에 읽기 전용 installed-state와 live-endpoint 검증 도구를 연결했고 config·HTTP.sys·URL ACL·service path, 일일 키 고정 벡터·External success XML·HTTP/1.1 framing parser 회귀 소스를 추가했다. 라이브 hostname 요청이 IPv4 전용 `HttpListener` prefix에서 HTTP.sys에 거부될 수 있던 계약 불일치를 찾아, runtime과 installer를 Directory IPv4·hostname 두 exact HTTPS prefix·URL ACL로 맞추고 repair rollback·uninstall·installed-state 판정에 두 항목을 모두 포함했다. installer와 검증 도구의 `netsh http show sslcert` parser는 `IP:port` 라벨과 IPv4 endpoint 값의 콜론을 key/value 구분자로 오인하지 않도록 공백으로 둘러싸인 구분자만 사용한다. 최신 작업 트리의 `Debug|x64`·`Release|x64` locked restore·빌드와 구성별 663개 테스트, ACL·HTTPS binding·installed-state·live endpoint PowerShell 회귀가 모두 통과했다. package 진입점은 다음 명시적 package 실행 때 네 회귀 소스를 함께 실행하도록 구성했으며 실제 package와 Windows 현장 검증은 아직 수행하지 않았다.

남은 완료 조건은 CA key rotation·dual-pin 구현과 검증 도구 확장, Windows Server 2016 설치 기준선 확인, 최신 package의 지원 환경 설치와 보고서 수집, 실제 TLS/DPAPI/ACL/SCM, 외부 앱·Milestone 및 두 장비 rotation·승격 시나리오 실행이다.
