# 서비스 디렉토리 저장소 에이전트 지침

이 파일은 저장소 루트와 모든 하위 경로에 적용한다. `CLAUDE.md`는 이 파일을 가져오기만 하는 참조 파일이다. 프로젝트 규칙을 바꿀 때는 원칙적으로 `AGENTS.md`만 수정하고 `CLAUDE.md`에 같은 내용을 복사하지 않는다.

## 1. 현재 저장소 상태

- .NET Framework 4.8, x64 전용 솔루션과 Domain, Application, ExternalProtocol, InternalProtocol, Infrastructure 라이브러리 및 WPF Tray 프로젝트가 있다.
- 현재 구현 범위는 서비스 정의 검증, `LogicalVersion`·snapshot `LogicalClock`을 포함한 immutable 디렉토리 스냅샷과 결정적 revision 비교, 승인 서비스 조회 projection, 등록·승인·거절·삭제 상태 전이, 단일 mutation gate·복구 coordinator, 저장 인터페이스, External 일일 API 키 코덱·헤더 검증 경계, 단일 파일 원자 교체, 시스템 파일 로그·1~1,095일 보존 정리, Windows Application Event Log 보안 진단·flood limiter primitive, bounded strict UTF-8 XML 입력, 확정된 상세 상태를 처리하는 Named Pipe wire codec, `ListenAddress` prefix·요청 endpoint guard, Admin Windows identity 인가와 Peer P-256 pairing 암호 primitive다. InternalProtocol에는 Admin XML DTO·codec가, Tray에는 loopback Admin·와치독 클라이언트와 라이트 테마 UI 소스가 추가되어 있다.
- Windows Service, HTTP listener와 서버 측 API DTO·라우팅, XML serializer·`LogicalClock` high-water의 내구적 저장·다중 파일 복구 저널, 실제 sync staging·병합, 설정 영속화, 와치독 Windows Service 실행체, 실제 Inno Setup 설치 프로그램과 테스트 프로젝트는 아직 없다. 로그 이벤트 호출 시점·Admin 설정 API 서버, Admin 인가와 보안 진단 logger의 HTTP·Pipe 통합, Event Source installer 등록, Peer 상태·session·DPAPI 저장, Named Pipe ACL·client token 검증도 아직 통합되지 않았다.
- 공통 버전 주입과 Windows/MSBuild 빌드 진입점을 추가했고 2026-07-18 Visual Studio Build Tools 2022에서 기존 기반의 `Debug|x64` 솔루션 빌드를 경고·오류 없이 확인했다. 이후 추가된 InternalProtocol·Tray 프로젝트를 포함한 현재 작업 트리는 아직 빌드·실행 검증하지 않았고 Release, 테스트와 패키징도 검증하지 않았다.
- 문서의 “확정”은 승인된 설계 결정이라는 뜻이며 구현·빌드·현장 검증 완료라는 뜻이 아니다. 존재하지 않거나 검증하지 않은 구조, 명령 또는 결과를 완료로 표시하지 않는다.

## 2. 작업 전에 읽을 문서

작업 범위에 맞게 다음 순서로 읽는다.

1. [계획 문서 안내](./docs/plan/README.md)
2. [애플리케이션 하드닝 가이드](./docs/plan/애플리케이션_하드닝_가이드.md)
3. [서비스 디렉토리 개발계획](./docs/plan/서비스디렉토리_개발계획.md)
4. API 변경이면 [API 명세 안내](./docs/plan/서비스디렉토리_API명세.md)와 해당 상세 명세
   - 다른 애플리케이션 계약: [외부 애플리케이션 API](./docs/plan/서비스디렉토리_외부애플리케이션_API명세.md)
   - 트레이·와치독·피어 계약: [내부 API](./docs/plan/서비스디렉토리_내부_API명세.md)

문서 책임은 다음과 같다.

- 하드닝 가이드: 공통 보안 기준선과 예외 승인 절차
- 외부·내부 API 명세: 요청·응답과 호출자 계약의 단일 원본
- 개발계획: 제품 구성, 데이터 소유권, 저장과 동기화 불변식의 단일 원본

충돌을 발견하면 약한 규칙을 임의로 선택하지 않는다. 보안 기준을 우선하고, 필요한 결정을 문서에서 먼저 해소한다. 예외에는 사유, 위험, 보완 통제, 적용 기간과 승인자가 필요하다. 서비스 디렉토리의 승인된 프로젝트별 판정과 예외는 [개발계획 §8](./docs/plan/서비스디렉토리_개발계획.md#8-프로젝트-하드닝-적용)을 따른다.

## 3. 기본 작업 방식

1. `git status --short`와 관련 파일을 먼저 확인한다. 사용자의 기존 변경과 미추적 파일을 보존한다.
2. 비단순 작업은 구현 전에 접근 방식과 바꿀 파일·이유를 짧게 제시한다.
3. 실제 `.sln`, `.csproj`, 패키지 선언, 스크립트와 기존 코드를 읽기 전에는 API나 의존성을 가정하지 않는다.
4. 요청을 충족하는 가장 작은 응집된 변경을 한다. 요청 밖 리팩터링이나 기능 추가는 별도로 제안한다.
5. 파일을 편집한 직후 현재 내용을 다시 읽고, 기억한 이전 상태가 아니라 실제 결과를 기준으로 다음 작업을 한다.
6. 오류 메시지는 끝까지 읽고 원인을 고친다. 같은 방식이 반복해서 실패하면 전제를 다시 확인한다.
7. 관련 정적 검사와 문서 검증은 작업 범위에 맞게 실행한다. 전체 또는 부분 빌드, 컴파일을 수반하는 테스트와 패키징은 사용자가 명시적으로 빌드 체크를 요청했을 때만 실행한다. 수정·커밋·push 요청을 빌드 체크 요청으로 해석하지 않는다.
8. 작동을 확인하지 않은 코드, TODO, mock 또는 placeholder를 완성품이라고 보고하지 않는다.

PowerShell로 Markdown과 기타 텍스트 문서를 읽을 때는 기본 인코딩에 의존하지 말고 항상 UTF-8을 명시한다. `Get-Content -LiteralPath <path> -Encoding UTF8` 또는 `[System.IO.File]::ReadAllText(<path>, [System.Text.Encoding]::UTF8)`을 사용한다. 한글이 깨진 출력은 판단이나 편집 근거로 사용하지 말고 UTF-8로 다시 읽는다.

요구사항이 여러 해석으로 갈리고 선택이 외부 호환성, 보안 모델, 저장 형식 또는 운영 권한을 바꾼다면 추측하지 말고 핵심 질문을 한다. 되돌리기 쉬운 세부사항은 기존 규칙에 맞춰 합리적으로 결정한다.

## 4. 확정된 기술 경계

- C#과 .NET Framework 4.8
- 지원 대상: Milestone XProtect `2021 R1` 이상
- 지원 OS: x64 Windows Server `2019` 이상은 Standard·Datacenter의 Desktop Experience만 지원하고 Server Core는 제외한다. Windows 10 `1809` 이상과 Windows 11 `24H2` 이상은 Pro·Enterprise·IoT Enterprise를 지원한다. Enterprise·IoT Enterprise의 LTSC release는 버전 하한과 해당 Milestone의 OS 지원 범위를 모두 충족하고 조합 검증을 통과한 경우에 포함한다. 모든 실제 배포 조합은 해당 Milestone 버전이 지원하는 OS와의 교집합으로 제한
- 플랫폼: `x64` 전용. `AnyCPU` 또는 `x86` 산출물을 운영 대상으로 추가하지 않음
- 배포 환경: Active Directory 도메인과 Workgroup을 모두 지원
- 메인: Windows Service(`ServiceBase`) + `HttpListener`
- UI: WPF 트레이 앱 + `H.NotifyIcon.Wpf` `2.4.1`. 라이트 테마를 사용하고 일반 텍스트는 11pt에 해당하는 WPF `14.667` DIP를 기준으로 한다. 제목·상태 수치처럼 명확한 계층이 필요한 텍스트만 더 크게 사용한다.
- 트레이 상태 아이콘의 단일 원본은 [`docs/plan/tray_running.png`](./docs/plan/tray_running.png)와 [`docs/plan/tray_stopped.png`](./docs/plan/tray_stopped.png)다. 실행 중에는 running, 중지·전환·오류 상태에는 stopped를 사용하고 같은 이미지를 다른 경로에 복제하지 않는다.
- 감시·서비스 제어: 별도 경량 Windows Service
- 영속화: XML + `XmlSerializer`
- 설치: Inno Setup. 최종 설치 파일과 분리된 SHA-256 manifest는 저장소 루트 [`installer`](./installer/)에 출력하며 상세 파일명과 배포 계약은 [`installer/README.md`](./installer/README.md)를 따른다.
- API 포트: TCP `21000`
- 전송: 폐쇄망의 HTTP/1.1. TLS/HTTPS는 [개발계획 §8의 승인된 예외](./docs/plan/서비스디렉토리_개발계획.md#82-tls-미사용-예외-기록)에 따라 사용하지 않음
- 네트워크 노출: 승인된 폐쇄망 인터페이스에만 바인딩하고 wildcard를 금지하며 Windows 방화벽은 Domain·Private 프로필만 허용하고 Public 프로필에서는 차단. 원격 CIDR·원격 대역 allowlist는 사용하지 않음
- External·Peer 원격 prefix 주소는 설치 시 선택한 단일 IPv4·IPv6 unicast literal `ListenAddress`를 `config.xml`에 저장해 사용한다. Admin·와치독 loopback prefix는 별도다. interactive 설치는 활성 Domain·Private 인터페이스 주소 중 하나를 요구하고 unattended 설치는 명시적 주소 인수를 요구한다. IPv6 link-local·multicast·IPv4-mapped와 zone identifier는 지원하지 않으며 mapped 주소는 원래 IPv4 literal로 입력한다. 누락·미할당·loopback·wildcard·multicast·Public 주소면 서비스 기동을 실패시키며 자동 주소 선택이나 wildcard fallback을 하지 않는다.
- HTTP Server API의 IP literal URL prefix는 보안 경계가 아니다. External·Peer 요청마다 `HttpListenerRequest.LocalEndPoint`가 설정한 `ListenAddress:21000`과 정확히 일치하는지 검사하고, Admin·와치독 loopback 요청은 local endpoint가 `127.0.0.1:21000`이며 remote address도 loopback인지 검사한다. endpoint 정보를 얻지 못하거나 불일치하면 fail closed한다.
- 정상 운영 규모: `Deleted=false`인 활성 서비스 최대 1,000개
- 외부 애플리케이션 호출 부하: 저빈도

이 선택을 임의로 다른 프레임워크, 데이터베이스, 웹 UI 또는 설치 기술로 바꾸지 않는다. 새 의존성을 추가하기 전에는 실제 프로젝트 대상 프레임워크 지원, 고정할 버전, 라이선스, 배포 파일, 취약점과 대안 유무를 확인한다.

Milestone XProtect 2021 R1 이상과 위 Windows edition·버전 하한은 확정 범위다. 각 Milestone·Windows edition·설치 옵션 조합의 호환성, .NET Framework 4.8 제공 여부와 실제 설치·기동은 배포 환경에서 검증해야 하며, 확인 전에는 모든 조합에서 런타임이 항상 설치되어 있다고 단정하지 않는다.

### 제품·빌드 버전

- 버전의 단일 원본은 저장소 루트 [VERSION](./VERSION) 파일이다.
- 현재 제품 버전은 `1.0.0`, 빌드 번호는 `3`이다.
- 제품 버전 `major.minor.patch`는 사용자가 명시적으로 변경하라고 요청한 경우에만 바꾼다. 기능 추가, 수정, 릴리스 준비, 커밋 또는 push를 이유로 에이전트가 임의로 올리지 않는다.
- 새 변경을 커밋하고 push하는 한 번의 전달 단위마다 `BUILD`를 정확히 1 증가시켜 같은 커밋에 포함한다. 초기 원격 push는 build `1`부터 시작한다.
- 같은 전달 단위의 `commit --amend`, push 실패 후 재시도, upstream 설정 또는 내용 변경 없는 재push에는 빌드 번호를 다시 올리지 않는다. push 실패 뒤 새로운 파일 변경을 추가해 새 커밋을 만들 때만 다음 build를 사용한다.
- 현재 .NET 프로젝트는 공통 MSBuild 설정으로 `VERSION`의 제품 버전과 빌드 번호를 적용한다. 향후 설치 프로젝트에도 같은 단일 원본을 적용한다. 외부 API 응답에는 보안·호환성 계약상 제품 build를 노출하지 않는다.

## 5. 구성요소 책임

### 메인 서비스

- 디렉토리, 톰스톤, 승인 대기, 동기화 설정과 API의 유일한 소유자다.
- 검증, 승인 상태 전이, 저장, 병합과 스케줄링을 담당한다.
- UI를 포함하지 않는다.

### 트레이 앱

- 표시와 사용자 명령 수집만 담당한다.
- 일반 사용자 권한으로 실행한다.
- Admin API와 제한된 Named Pipe만 사용한다.
- XML 파일 또는 피어 API를 직접 읽거나 수정하지 않는다.

### 와치독

- 서비스 상태와 health를 감시하고 허용된 서비스 제어만 수행한다.
- 디렉토리 데이터, 승인 요청 또는 동기화 설정을 변경하지 않는다.
- 메인 서비스와 상호 의존성을 만들지 않는다.

공통 도메인 규칙과 직렬화 원칙은 구성요소마다 복제하지 말고 적절한 공유 계층에서 한 번 구현한다. 외부, Admin, Sync DTO는 목적과 노출 범위가 다르므로 도메인 저장 타입이나 서로의 DTO를 그대로 재사용하지 않는다.

## 6. 도메인 불변식

- `ProductCode`는 trim 후 `ToUpperInvariant()`로 정규화한 `[A-Z0-9]{4}` 형식의 정확히 4바이트 ASCII 값이며 `StringComparer.OrdinalIgnoreCase` 의미로 비교하는 유일 키다.
- `ServerAddress`는 [개발계획 §5.1](./docs/plan/서비스디렉토리_개발계획.md#51-도메인-레코드)의 공통 문법으로만 검증한다. 요약하면 trim한 IPv4·IPv6 literal 또는 최대 253자 ASCII DNS hostname만 허용하며 scheme·path·query·port·IPv6 zone identifier와 모호한 IPv4 표기를 거부한다.
- 활성 서비스는 최대 1,000개다. 톰스톤과 승인 대기는 이 수에 포함하지 않으며 용량 경계의 요청·승인 동작과 별도 항목 제한은 [외부 API 명세](./docs/plan/서비스디렉토리_외부애플리케이션_API명세.md)와 [내부 API 명세](./docs/plan/서비스디렉토리_내부_API명세.md)를 따른다.
- 등록과 수정은 승인 전 정식 디렉토리에 반영하지 않는다.
- 등록 요청은 같은 제품코드의 기존 대기를 먼저 확인한다. 요청이 대기 내용과 같으면 기존 요청을 재사용하고, 다르면 현재 활성값과 같더라도 충돌로 처리한다.
- 대기가 없을 때만 활성값·톰스톤·미등록 상태를 평가해 `ALREADY_REGISTERED`, Modify 또는 New를 결정한다.
- 승인 대기는 재시작 후에도 유지하지만 피어와 동기화하지 않는다.
- 대기 생성 시 해당 키의 불변 base revision 또는 동등한 전체 스냅샷을 보존한다.
- 승인 시 현재 revision이 base와 다르면 요청값이 이미 활성값과 같은 경우만 “이미 충족”으로 정리한다. 그 밖에는 대기를 유지한 채 충돌을 반환하고 자동 덮어쓰기·재분류하지 않는다.
- 거절은 대기 요청만 처리하고 활성 데이터와 톰스톤을 바꾸지 않는다.
- 삭제는 물리 삭제가 아니라 톰스톤이다.
- 톰스톤은 시간으로 제거하지 않으며 같은 제품코드의 신규 등록 승인 때만 새 활성 레코드로 대체한다.
- 외부 조회는 톰스톤과 신규 승인 대기를 없는 항목으로 취급한다. Modify 대기 중에는 기존 승인값을 반환한다.
- 변경 레코드는 마지막 변경 출처 `OriginInstanceId`를 보존한다. 현재 송신자 ID를 레코드 출처로 대신하지 않는다.
- 변경 레코드는 unsigned 64-bit `LogicalVersion`을 가진다. 병합 순서는 `(LogicalVersion, canonical OriginInstanceId)`이며 GUID는 소문자 `D` 형식으로 정규화해 `Ordinal` 비교한다. `LastModifiedUtc`와 `DeletedUtc`는 감사·표시용 UTC 시각이며 병합 순서를 결정하지 않는다.

## 7. 저장·동시성 규칙

- 기동 시 XML을 한 번 읽고 전체 검증에 성공한 스냅샷만 메모리에 게시한다.
- 조회는 메모리에서 처리하고 파일은 변경 시에만 연다.
- 같은 볼륨의 임시 파일에 쓰고 flush한 뒤 원자적으로 교체하며 백업을 유지하고 핸들을 즉시 닫는다.
- 최초 대상 파일이 없을 때 `File.Replace`가 동작한다고 가정하지 않는다.
- 손상 XML, 중복 키, 백업 복구, 디스크 부족, 권한 실패와 교체 실패를 명시적으로 처리한다.
- 메인 서비스는 디렉토리·대기·설정의 모든 변경 명령과 sync 최종 병합·게시를 인스턴스당 하나의 state mutation gate로 직렬화한다. 네트워크 송수신과 XML parse는 gate 밖에서 수행하고, gate 안에서 현재 revision을 다시 검증한 뒤 저널·원자 저장과 immutable snapshot 교체를 완료한다.
- 조회는 현재 immutable snapshot을 사용해 mutation gate를 점유하지 않는다. sync 중 생긴 로컬 변경은 최종 병합 시 현재 snapshot에 보존하고 다음 사이클에서 피어로 전파한다.
- `directory.xml` 갱신과 `pending.xml` 제거처럼 여러 파일이 참여하는 작업은 파일별 원자 쓰기만으로 충분하지 않다. 저널 또는 동등한 복구 설계와 강제 종료 테스트가 필요하다.
- 실행 파일은 `%ProgramFiles%\DEEPAi\ServiceDirectory\`, 상태·설정·로그는 제한된 ACL의 `%ProgramData%\DEEPAi\ServiceDirectory\`에 둔다.
- `config.xml`은 동기화 설정과 함께 `1..1095` 범위의 정수 `LogRetentionDays`를 영속화한다. 설치 기본값은 `30`이며 트레이는 파일을 직접 수정하지 않고 Admin API를 사용한다.
- 인스턴스의 unsigned 64-bit `LogicalClock` high-water mark를 재시작과 장애 복구 뒤에도 감소하지 않게 내구적으로 영속화한다. 로컬 변경은 `max(현재 LogicalClock, 지금까지 관찰한 LogicalVersion) + 1`을 사용하고 clock과 레코드 변경을 복구 일관성 있게 저장한다. 최댓값에서는 wrap하지 않고 변경을 실패시킨다.
- `ListenAddress` 변경은 설치 프로그램의 repair 흐름만 사용한다. 서비스 중지, 새 주소 검증, exact URL ACL·방화벽과 설정 변경, 재기동을 하나의 롤백 가능한 작업으로 수행한다.
- External 일일 API 키에는 저장할 secret이 없다. 고정 AES secret이나 별도 master key를 코드·설정에 추가하지 않는다. Admin은 Windows identity와 로컬 운영자 그룹만 사용하므로 애플리케이션 자격 증명을 저장하지 않는다. Peer pair root만 내부 API 명세에 따라 `secrets/peer.dat`에 DPAPI `LocalMachine`으로 보호한다.
- 일반 제거는 서비스, 프로그램 파일, URL ACL과 방화벽 규칙을 제거하되 `%ProgramData%\DEEPAi\ServiceDirectory\`의 운영 데이터·설정·로그·백업·Peer 자격 증명을 기본 보존한다. 사용자가 명시적으로 전체 삭제를 선택한 경우에만 정확한 데이터 루트를 삭제하고 재페어링 필요성을 알린다. 보존 데이터의 ACL을 완화하지 않으며 재설치 시 새 서비스 SID에 맞게 복구한다.

## 8. 시스템 로그 규칙

상세 계약의 단일 원본은 [개발계획의 시스템 로그 정책](./docs/plan/서비스디렉토리_개발계획.md#9-시스템-로그-정책)이다.

- 파일은 데이터 루트 기준 `logs/system/dpai-sd_yyyy-MM-dd.log`, 절대 경로로는 `%ProgramData%\DEEPAi\ServiceDirectory\logs\system\dpai-sd_yyyy-MM-dd.log`다.
- 파일명 날짜와 각 레코드 시각은 UTC가 아니라 기록 시점의 시스템 로컬 시간대를 사용한다.
- 레코드 시각은 `DateTimeOffset.Now` 의미의 `yyyy-MM-ddTHH:mm:ss.fffzzz` 형식으로 offset을 포함한다. `Z`로 기록하지 않는다.
- 시스템 로그와 External 일일 API 키 날짜만 로컬 시간을 사용한다. API payload의 표시·감사 시각은 UTC를 사용하고 병합 순서는 논리 버전으로 결정한다.
- 파일 로그 이벤트는 `SERVICE_STARTED`, `SERVICE_STOPPED`, `REGISTERED_SERVICE_CREATED`, `REGISTERED_SERVICE_UPDATED`, `REGISTERED_SERVICE_DELETED`, `SYNC_INITIAL_STARTED`, `SYNC_STARTED`, `SYNC_STOPPED`, `SYNC_SUCCEEDED`로 제한한다.
- 최초 sync는 `SYNC_INITIAL_STARTED`만 기록하고 같은 사이클에 `SYNC_STARTED`를 중복 기록하지 않는다. 성공 시 `SYNC_SUCCEEDED`를 추가한다.
- 등록·수정·삭제 이벤트는 상태가 성공적으로 영속화된 뒤 기록한다. 승인 대기와 거절은 파일 로그 대상이 아니다.
- 보존기간은 `LogRetentionDays`개의 로컬 달력 날짜이며, 트레이 설정과 `GET/PUT /admin/settings/logging`으로 관리한다.
- 보존 정리는 정확한 로그 디렉터리의 `dpai-sd_yyyy-MM-dd.log` 파일만 대상으로 한다. 다른 파일이나 하위 경로를 삭제하지 않는다.
- `LogRetentionDays` 설치 기본값은 `30`, 최대 허용값은 3년인 `1095`다. 범위 밖 값은 저장하지 않는다.
- 인증·인가 실패와 local·remote endpoint 불일치 같은 신뢰 경계 실패는 위 9개 파일 이벤트와 분리해 Windows Application Event Log의 source `DEEPAi.ServiceDirectory.Security`에 기록한다. 서비스는 installer가 만든 exact `Application` source key를 read-only로 확인하고 source 자동 생성 API를 사용하지 않는다. 비밀값·API 키·토큰·요청 원문을 기록하지 않고 반복 실패는 집계·속도 제한해 로그 flood를 억제한다.

## 9. 동기화 규칙

- 실행 시점은 승인·삭제 직후, 10분 주기, 서비스 기동, 관리자 수동 요청이다.
- 모든 사이클은 인증된 handshake부터 시작한다.
- 60초 시계 편차 한계는 Peer 요청 인증의 timestamp freshness와 replay 방지에만 사용한다. 데이터 병합 순서는 벽시계와 무관하다.
- 활성 레코드와 톰스톤 모두 `LogicalVersion`을 먼저 비교하고, 같으면 canonical `OriginInstanceId`를 결정적 타이브레이크로 사용한다.
- `LogicalVersion`과 `OriginInstanceId`가 모두 같은데 payload가 다르면 정상 revision이 아니므로 전체 병합을 중단하고 상태를 변경하지 않는다.
- 병합은 교환법칙, 결합법칙, 멱등성과 결정성을 만족해야 한다.
- sync는 일관된 스냅샷을 사용하고 동시 관리 변경·다른 sync와 직렬화 또는 명확한 버전 경계를 가진다.
- 인증된 전체 원격 snapshot과 모든 batch의 검증·병합이 성공한 commit에서만 채택 여부와 관계없이 내구적 `LogicalClock`을 원격 snapshot의 `LogicalClock` 이상으로 전진시킨다. 실패한 exchange에서는 clock을 바꾸지 않으며 다음 로컬 변경은 성공적으로 관찰한 모든 버전보다 큰 값을 사용한다.
- 해제 통지 실패와 로컬 해제 성공을 구분해 기록한다.

최초 페어링, 피어 인증과 handshake·exchange 결합은 [내부 API 명세의 Peer 계약](./docs/plan/서비스디렉토리_내부_API명세.md#22-peer)을 그대로 구현한다. 논리 버전 도메인 primitive는 소스에 반영됐지만 serializer·복구 저장·실제 sync 병합은 미구현이므로 이를 완료 상태로 두거나 벽시계 비교와 혼합한 임시 병합 프로토콜을 만들지 않는다.

## 10. API 경계와 변경 규칙

- External, Admin, Health와 Peer API는 URL, media type, 요청·응답 XML 또는 별도 header에 API 버전 필드를 두지 않는다. `/v1` 같은 버전 경로, `ApiVersion`과 `ProtocolVersion` 요소를 추가하지 않고 현재 무버전 경로를 영구 유지한다.
- 계약 변경은 기존 소비자가 계속 동작하는 호환 추가만 허용한다. 기존 경로·메서드·필드 의미를 바꾸거나 필수 필드를 추가하는 호환되지 않는 변경을 금지하고, 새 필드는 상세 명세가 이미 허용한 선택적 확장점에서만 추가한다. 암호 primitive의 domain-separation label과 알고리즘 식별자는 API 버전 필드가 아니다.
- External, Admin, Peer XML은 각각 `urn:deepai:service-directory:external`, `urn:deepai:service-directory:admin`, `urn:deepai:service-directory:peer` 고정 namespace와 [`docs/plan/xsd`](./docs/plan/xsd/)의 XSD를 사용한다. 요청의 알 수 없는 요소·속성은 거부하고 응답 확장은 상세 명세가 허용한 마지막 `Extensions` 지점에서만 한다. HTTP 상태·오류 envelope의 단일 원본은 각 상세 API 명세다.

### 외부 애플리케이션 계약

- 외부 앱이 사용할 수 있는 현재 범위는 `GET /api/health`, `GET /api/services`, `POST /api/registration`뿐이다.
- 기준 주소는 `http://{ListenAddress}:21000`이며 설치된 IP literal을 그대로 사용한다. IPv6 주소는 URI에서 대괄호로 감싼다. DNS hostname, HTTPS listener나 인증서 바인딩을 추가하지 않는다.
- health를 포함한 외부 API는 [외부 명세 §2.3~§2.5](./docs/plan/서비스디렉토리_외부애플리케이션_API명세.md#23-일일-api-키-생성-계약)의 44자 일일 API 키를 요구한다.
- 키의 ProductCode는 trim·대문자 정규화된 4바이트 ASCII이고, 날짜는 호출 호스트의 시스템 로컬 `yyyyMMdd`다. 서비스는 검증 시점 서버 로컬 날짜만 허용한다.
- 서비스 조회·등록 요청은 키에서 복원한 ProductCode와 쿼리·XML ProductCode가 일치해야 한다. 와치독 health는 등록 ProductCode가 아닌 전용 구성요소 코드 `WDOG`로 같은 키를 생성한다.
- External 일일 API 키 방식을 Admin·Peer 인증에 재사용하지 않는다.
- 외부 앱이 Admin, Peer, Named Pipe 또는 저장 파일에 의존하게 만들지 않는다.
- 외부 등록 요청의 별도 상태·결과 조회 API와 거절 이력 API를 추가하지 않는다. 외부 앱은 `GET /api/services?productCode={code}`를 재조회해 요청한 값이 승인 정보에 반영됐는지만 확인하며 대기와 거절을 구분하지 않는다.
- 외부 응답에 톰스톤, 피어 ID, 내부 경로, 스택, 제품 빌드·패치 버전을 노출하지 않는다.
- 브라우저 cross-origin 호출과 정적 파일 제공은 지원하지 않는다. CORS 허용 헤더, 디렉터리 목록, 설정·백업 파일 노출을 추가하지 않는다.
- 외부 계약 변경에는 명세, DTO, 직렬화 테스트, 오류·호환성 테스트와 소비자 영향을 함께 갱신한다.
- 무버전 외부 경로를 영구 유지하고 호환되지 않는 변경을 덮어쓰지 않는다.
- 외부 호출은 저빈도를 전제로 하고 활성 서비스는 최대 1,000개를 지원한다. 구체적인 본문·필드·호출 제한은 [외부 API 명세](./docs/plan/서비스디렉토리_외부애플리케이션_API명세.md)를 단일 원본으로 사용한다.

### 내부 계약

- `/admin/*`는 `127.0.0.1` loopback `HttpListener`에서만 수신하고 `Negotiate` 인증을 사용한다. IP literal loopback에서는 Kerberos를 전제로 하지 않고 AD·Workgroup 모두 NTLM을 허용하되 `UnsafeConnectionNtlmAuthentication=false`를 유지한다. 추후 검증된 loopback hostname·SPN을 추가한 환경에서 Kerberos가 협상될 수 있지만 필수 계약은 아니다. 설치 프로그램이 만드는 로컬 `DEEPAi-ServiceDirectory-Operators` 그룹만 운영자로 인가한다. 상세 계약은 [내부 API 명세의 Admin 절](./docs/plan/서비스디렉토리_내부_API명세.md#21-admin)을 따른다.
- Admin 인가는 현재 요청에서 전달된 인증된 `WindowsIdentity`와 정확한 로컬 `DEEPAi-ServiceDirectory-Operators` SID만 사용한다. 현재 프로세스 identity, 이름이 같은 도메인 그룹, 내장 Administrators 또는 UI 표시 상태로 대체하지 않으며 SID 해석·token 검사가 실패하면 허용하지 않는다.
- `/api/sync/*`의 최초 신뢰 설정은 ECDH P-256과 양쪽 운영자의 8자리 SAS 확인을 사용하고, 확정한 피어 자격 증명은 DPAPI로 보호하며 이후 요청·응답은 HMAC-SHA256으로 인증·무결성·freshness·재전송 방지를 검증한다. 상세 상태 전이와 wire contract는 [내부 API 명세의 Peer 절](./docs/plan/서비스디렉토리_내부_API명세.md#22-peer)을 따른다.
- 피어 자격 증명을 폐기해도 `config.xml`의 `LastPeerKeyEpoch`를 삭제하거나 감소시키지 않는다. 새 페어링 epoch는 내부 API 명세의 단조 증가 계약을 따른다.
- loopback, 원격 IP 또는 방화벽을 인증으로 취급하지 않는다.
- handshake를 거치지 않은 exchange와 인증되지 않은 release·revoke를 허용하지 않는다.
- Named Pipe는 `START`, `STOP`, `RESTART`, `STATUS`만 허용한다. `STATUS` 성공 응답은 [내부 API 명세 §6](./docs/plan/서비스디렉토리_내부_API명세.md#6-named-pipe-서비스-제어-계약)의 선행 서비스 상태 토큰과 `HEALTH`, `FAILURES`, `RESTARTS_10M`, `AUTO_RESTART` 필드를 포함하고, health 실행 상태에 따라 `LAST_HEALTH_UTC`를 포함하거나 생략한다. 중복·잘못된 필드와 문법 위반은 거부한다.
- 로그 설정은 `GET /admin/settings/logging`과 `PUT /admin/settings/logging`으로만 조회·변경한다.

외부와 내부 명세에서 같은 규칙을 복사해 서로 다른 단일 원본을 만들지 않는다. 공용 계약은 한 문서에 정의하고 다른 문서에서 링크한다.

## 11. 보안 필수 규칙

[하드닝 가이드](./docs/plan/애플리케이션_하드닝_가이드.md)를 최소 기준으로 적용한다.

- 서비스 디렉토리 API는 승인된 폐쇄망에서 HTTP/1.1을 사용하며 TLS/HTTPS를 구성하지 않는다. 이는 인증서 발급·배포·갱신이 어려운 운영 환경에 대한 전송 암호화 한정 예외다.
- HTTPS listener, 인증서 바인딩·설치·검증 우회 코드를 추가하지 않는다. 배포망이 외부 또는 비신뢰 네트워크와 연결되면 이 예외는 효력을 잃으며 구현 전에 보안 모델을 재검토한다.
- External은 별도 secret 없이 ProductCode와 시스템 로컬 날짜로 만든 AES-256-CBC 일일 API 키를 사용한다. 이는 강한 호출자 인증, 요청·응답 무결성 또는 replay 방지를 제공하지 않는 [개발계획 §8.3의 승인 예외](./docs/plan/서비스디렉토리_개발계획.md#83-external-일일-api-키-예외-기록)다.
- External의 당일 replay와 method·path·body 미결합 위험을 숨기거나 차단된 것으로 보고하지 않는다. 대신 폐쇄망 인터페이스 바인딩, Domain·Private 방화벽 프로필, 저빈도에 맞춘 rate limit, ProductCode 일치와 등록·수정 운영자 승인을 적용한다.
- Admin은 [내부 API 명세의 Negotiate·로컬 운영자 그룹 계약](./docs/plan/서비스디렉토리_내부_API명세.md#21-admin), Peer는 [ECDH P-256·8자리 SAS·DPAPI·HMAC-SHA256 계약](./docs/plan/서비스디렉토리_내부_API명세.md#22-peer)을 적용한다. 어느 환경에서도 무인증이나 공용 시크릿 하드코딩으로 대체하지 않는다.
- External 키를 위해 고정 AES secret, 난독화 공용 secret, 별도 master key, API 키 설정 파일 또는 배포 절차를 추가하지 않는다.
- XML parser는 DTD와 외부 엔터티를 비활성화한다.
- 본문 크기, XML 깊이, 항목 수, 필드 길이, 포트 범위, 주소·GUID·UTC 형식, rate limit, 동시 실행과 페이지네이션은 [외부 API 명세](./docs/plan/서비스디렉토리_외부애플리케이션_API명세.md)와 [내부 API 명세](./docs/plan/서비스디렉토리_내부_API명세.md)의 확정 상수를 수신 측에서 검증한다. ProductCode `[A-Z0-9]{4}`, 일일 키 Base64 44자와 로컬 `yyyyMMdd` 규칙도 같은 계약을 따른다.
- 메인·와치독은 각각의 Windows 가상 서비스 계정과 서비스 SID로 실행한다. 메인에는 데이터·로그·DPAPI blob, 와치독에는 필요한 메인 서비스 제어 권한만 부여하고 `LocalSystem`으로 설치하지 않는다. 대상 Windows에서 가상 계정을 사용할 수 없는 경우에만 별도 승인 기록을 먼저 만든다.
- Named Pipe는 [내부 API 명세](./docs/plan/서비스디렉토리_내부_API명세.md#6-named-pipe-서비스-제어-계약)의 로컬 운영자 그룹 ACL, BOM 없는 UTF-8, 256바이트와 3초 제한을 그대로 적용한다. 동작시키기 위해 `Everyone` 쓰기 같은 넓은 권한을 주지 않는다.
- 현재 저장하는 애플리케이션 시크릿은 Peer pair root뿐이며 `secrets/peer.dat`에 DPAPI `LocalMachine`과 제한 ACL로 보호한다. Admin용 별도 비밀번호·토큰을 추가하지 않는다.
- 로그·오류에 비밀번호, API 키, 토큰, 인증서 개인키, 요청 원문 전체, 내부 경로와 스택을 남기지 않는다.
- 시스템 파일 로그에 §8의 목록 밖 이벤트를 임의로 추가하지 않는다. 인증·인가·신뢰 경계 실패는 Windows Application Event Log source `DEEPAi.ServiceDirectory.Security`에 분리 기록하고 민감정보 배제와 flood 억제를 적용한다.
- 방화벽 규칙은 TCP 21000과 Domain·Private 프로필로 제한하고 Public 프로필에서는 차단한다. 원격 CIDR·원격 주소 allowlist는 구성하지 않는다.
- External·Peer listener에 wildcard `http://+:21000/` 또는 `0.0.0.0` 바인딩을 사용하지 않는다. External 키 누락·형식·복호화·날짜·ProductCode 검증 실패는 동일한 `401`로 거부하고, Peer 인증 설정이 없거나 손상되면 Peer API를 열지 않는다.
- IP literal prefix 등록만으로 인터페이스 격리가 보장된다고 가정하지 않는다. 모든 요청의 실제 local endpoint를 해당 신뢰 경계의 주소와 TCP `21000`에 다시 결합하고 loopback 경계에서는 remote endpoint도 loopback인지 확인한다.
- 트레이와 일반 실행 파일은 `asInvoker`를 사용하고 설치 작업 외의 권한 상승을 금지한다.
- DLL 검색 경로를 제한하고 쓰기 가능한 위치에서 실행되는 설치 파일의 DLL 하이재킹을 검증한다.
- 운영 빌드의 DEP/NX, ASLR, CFG 적용 여부를 확인하고 인증 우회 플래그, 테스트 코드와 개발자 백도어를 포함하지 않는다. `Release|x64`는 최적화와 `pdbonly` PDB를 사용하고 PDB를 설치 파일·설치 payload·운영 장비에 포함하지 않는다. 실제 배포 바이너리와 정확히 일치하는 심볼 세트는 [`installer/README.md`](./installer/README.md)의 접근 통제·보존 정책에 따라 별도 보관한다.
- 자동 업데이트를 추가하지 않는다. 패치는 승인된 수동·오프라인 경로와 별도 신뢰 채널의 SHA-256 이상 manifest로 검증한다.
- 보안 관련 변경은 리뷰하고 SAST, 시크릿 스캔, 의존성 CVE 점검과 재현 빌드 산출물 해시 기록을 릴리스 절차에 포함한다.
- 코드 서명 인증서가 없으므로 실행 파일, 라이브러리와 설치 파일의 코드 서명은 현재 범위에서 제외한다. 서명 도구, 단계 또는 인증서 시크릿을 임의로 추가하지 않는다.

## 12. 코드와 파일 구성

- 파일과 타입은 하나의 명확한 책임을 가진다.
- 약 1000줄은 분리 검토 기준이다. 자연스러운 책임 경계가 있을 때만 나누고 함수 중간이나 응집된 로직을 숫자 때문에 쪼개지 않는다.
- 기존 명명, nullable 처리, 오류 모델, 테스트 스타일은 실제 코드가 생긴 뒤 그 관례를 따른다.
- 예외를 조용히 삼키지 않는다. 복구 가능한 오류와 서비스 중단 오류를 구분하고 관찰 가능하게 만든다.
- 외부 입력을 OS 명령, 경로 또는 XML에 검증 없이 전달하지 않는다.
- 시크릿, 토큰, 인증서 개인키와 실제 고객 주소를 소스·테스트 픽스처·문서 예제에 넣지 않는다.

## 13. 빌드·테스트

빌드 체크는 사용자가 명시적으로 요청했을 때만 수행한다. 매 수정, 커밋 또는 push 때 자동으로 빌드하지 않으며 컴파일을 수반하는 테스트와 패키징도 같은 규칙을 적용한다. 요청받지 않아 실행하지 않은 빌드를 실패나 미검증 은폐 없이 완료 보고에 명시한다.

Windows/MSBuild 빌드 진입점은 `powershell -NoProfile -File .\tools\build.ps1 -Configuration Debug`이며 Release는 `-Configuration Release`를 사용한다. 이 스크립트는 Visual Studio Installer의 `vswhere.exe`로 MSBuild를 찾고 package restore 후 `DEEPAi.ServiceDirectory.sln`의 `x64` 구성만 빌드한다. 빌드 환경에는 Visual Studio Build Tools의 .NET desktop build tools, .NET Framework 4.8 SDK·Targeting Pack과 WPF build targets가 필요하며 스크립트는 이 조건을 fail-fast로 확인한다. 2026-07-18 Visual Studio Build Tools 2022 MSBuild `17.14.40.60911`에서 InternalProtocol·Tray 추가 전 기존 기반의 Debug 빌드를 경고 0개·오류 0개로 확인했다. 현재 작업 트리의 Debug, Release와 테스트·패키징 진입점은 아직 검증하지 않았다. 사용자가 빌드 체크를 명시적으로 요청하기 전에는 이 명령을 실행하지 않으며, .NET Framework 4.8 프로젝트가 `dotnet build`로 빌드된다고 가정하지 않는다.

코드가 생긴 뒤 최소 검증 범위:

- 관련 단위 테스트와 전체 솔루션 빌드
- Milestone XProtect 2021 R1 이상과 x64 Windows Server 2019+ Standard·Datacenter Desktop Experience, Windows 10 1809+ 및 Windows 11 24H2+ Pro·Enterprise·IoT Enterprise 지원 교집합의 빌드·설치·기동 및 AD·Workgroup 양쪽 환경. Server Core 제외와 포함된 LTSC release도 검증
- 외부·내부 API XML 직렬화와 고정 namespace·XSD·알 수 없는 요소 처리·HTTP 상태 계약 테스트
- 등록 상태표, 활성 서비스 1,000개 경계, 승인 대기 1,000개·800개 경고와 ProductCode 3·4·5바이트, ASCII 영문·숫자, 소문자 정규화, 비ASCII·내부 공백 거부, 선후행 공백 trim, IPv4·IPv4-embedded IPv6 선행 `0`과 숫자·점 전용 hostname 거부, 동시 요청
- 최초 저장, 손상·백업 복구, 다중 파일 작업 중 강제 종료
- unsigned 64-bit `LogicalVersion`·내구적 `LogicalClock`의 재시작·복구·overflow, 동일 버전 타이브레이크·collision과 병합의 교환법칙·결합법칙·멱등성·결정성, 활성 병합 후보 1,000개 초과 거부, 1,000개 batch·다중 batch staging과 동시 sync
- External 일일 API 키 고정 벡터, 44자 Base64, 무작위 IV, 잘못된 padding·날짜·ProductCode와 자정 rollover
- External 동일 날짜 replay와 요청 미결합이 승인된 제한으로 유지되는지, Admin·Peer 인증 우회와 Peer 변조·재전송은 차단되는지 분리 검증
- XXE, 확정된 16KiB·4MiB 본문과 XML 깊이 16, 페이지·batch·rate limit·동시 실행 제한
- HTTP 전용 listener, HTTPS·인증서 의존성 부재, 승인된 폐쇄망 인터페이스 바인딩, wildcard 금지, Domain·Private 허용과 Public 차단, 원격 CIDR allowlist 부재
- interactive·unattended 설치의 `ListenAddress` 선택, 미할당·loopback·wildcard·Public 주소 기동 실패, repair 변경과 URL ACL·방화벽 롤백
- IP literal prefix 우회 시도와 실제 `LocalEndPoint` 불일치 거부, Admin·와치독의 non-loopback remote endpoint 거부
- Admin Negotiate의 IP literal loopback NTLM, 선택적 hostname·SPN Kerberos, `UnsafeConnectionNtlmAuthentication=false`, 로컬 `DEEPAi-ServiceDirectory-Operators` 인가와 AD·Workgroup 동작
- Peer P-256 blob·KDF·방향별 confirmation MAC·pair root·8자리 SAS 고정 벡터와 잘못된 curve point·길이·SAS rejection sampling
- Peer ECDH P-256 페어링, 양쪽 8자리 SAS 확인, DPAPI 보호, HMAC-SHA256 요청·응답 변조·재전송 차단과 pairing KDF·SAS·canonical MAC 고정 벡터
- CORS·정적 파일·설정 파일 비노출과 응답 헤더의 제품·프레임워크 정보 최소화
- `asInvoker`, DLL 하이재킹, DEP/NX·ASLR·CFG, 운영 우회 코드와 디버그 심볼 정책
- 라이트 테마, 일반 텍스트 11pt(`14.667` DIP), 고대비·DPI와 `tray_running.png`·`tray_stopped.png` 상태 매핑을 포함한 트레이 UI 시각·접근성 검증
- External 고정 AES secret·master key와 Admin 애플리케이션 시크릿 부재, Peer 자격 증명의 `secrets/peer.dat` DPAPI 보호와 시크릿 커밋 부재
- SAST, 시크릿·의존성 CVE 스캔, 재현 빌드 해시와 오프라인 패치 체크섬
- 서비스 재시작 뒤 상태 영속성
- 시스템 로컬 날짜별 로그 파일 전환, offset 포함 시각과 timezone·DST 변경, External 일일 API 키 날짜 rollover
- 정의된 9개 로그 이벤트의 시점·중복 방지, `LogRetentionDays` 기본 30·경계 1/1095·초과 거부와 보존 정리
- Windows Application Event Log source 생성·ACL, 인증·인가·경계 실패 기록, 비밀값 배제와 반복 실패 flood 억제
- 설치·업그레이드·롤백·기본 데이터 보존 제거·명시적 전체 삭제 smoke test
- 설치 경로, 파일·서비스 ACL, 서비스 계정과 방화벽 Domain·Private/Public 프로필

버그 수정은 가능하면 실패 테스트나 재현으로 문제를 먼저 확인하고, 원인을 고친 뒤 원래 문제가 사라지고 회귀가 없는지 검증한다.

## 14. 문서 유지 규칙

- API를 바꾸면 같은 변경에서 해당 상세 명세와 [API 명세 안내](./docs/plan/서비스디렉토리_API명세.md)의 경계 표를 확인한다.
- ProductCode 또는 External 일일 API 키를 바꾸면 외부 명세 §2.3~§2.5, 개발계획 §8.3, 내부 health 참조와 고정 테스트 벡터를 함께 갱신한다.
- 아키텍처, 데이터 또는 단계가 바뀌면 개발계획을 갱신한다.
- 보안 적용 여부가 바뀌면 개발계획의 하드닝 적용표를 갱신한다.
- 경로, 로그 이벤트, 로그 로컬 시각 또는 보존 정책이 바뀌면 개발계획 §9와 내부 설정 API를 함께 갱신한다.
- 문서의 설계 상태와 구현 상태를 분리하고 날짜·버전·상대 링크를 확인한다.
- 같은 규범을 여러 문서에 복사하지 말고 단일 원본을 링크한다.
- 미결정을 확정처럼, 설계를 구현 완료처럼, 실행하지 않은 검증을 통과처럼 표현하지 않는다.

## 15. 완료 보고

결과는 다음을 간결하게 포함한다.

- 바꾼 내용과 이유
- 실제로 실행한 검증과 결과
- 실행하지 못한 검증과 이유
- 남은 미결정, 호환성 영향과 운영 위험

사용자의 명시적 요청 없이 커밋, push, 배포, 서비스 설치·시작·중지, 방화벽 변경 또는 외부 시스템 변경을 하지 않는다.
