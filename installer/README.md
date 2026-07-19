# 설치 산출물과 디버그 심볼 정책

이 디렉터리는 저장소 루트 기준 `installer\`이며 Inno Setup 소스와 최종 설치 EXE의 출력 위치다. 표준 패키징 진입점은 저장소 루트에서 실행하는 다음 명령이다.

```powershell
powershell -NoProfile -File .\tools\package.ps1 -Configuration Release
```

이 명령은 locked restore를 포함한 `Release|x64` 빌드와 전체 MSTest를 먼저 통과시킨 뒤 Inno Setup 6으로 [`ServiceDirectory.iss`](./ServiceDirectory.iss)를 컴파일한다. `ISCC.exe`가 기본 설치 경로에 없으면 `-InnoSetupCompilerPath`로 정확한 경로를 전달한다. ISCC 출력은 `artifacts\service-directory\package\installer-output\`의 격리된 staging에 만들고 예상한 이름의 비어 있지 않은 EXE 하나만 생성됐는지 확인한 뒤, 같은 볼륨의 `installer\`에 원자적 move 또는 replace로 게시한다. 따라서 compile 또는 staging 검증 실패는 기존 최종 EXE를 건드리지 않는다. 설치 패키지는 x64 Windows와 .NET Framework 4.8을 요구한다.

현재 `ServiceDirectory.iss`, 네 설치 지원 PowerShell 스크립트와 `tools\package.ps1` 소스는 존재하지만 package 명령, ISCC compile과 실제 설치·repair·rollback·제거는 아직 실행 검증하지 않았다. `installer\`에는 생성된 설치 EXE가 없다. 아래 ACL·URL ACL·방화벽·서비스 상태 rollback 설명은 구현 계약이며 현장 검증 완료를 뜻하지 않는다.

interactive 설치에서는 활성 Domain·Private 네트워크 인터페이스의 지원되는 IP literal 하나를 선택한다. silent 설치는 `/ListenAddress=<canonical-ip-literal>`을 반드시 전달한다. 일반 제거는 운영 데이터를 보존하며 silent 전체 삭제는 별도의 `/PurgeData=1`을 명시한 경우에만 수행한다.

설치·repair는 파일 복사 전에 보호된 임시 setup-state에 기존 메인·와치독 서비스의 실행 여부, 서비스 구성과 서비스 보안 설명자를 저장한다. 데이터 루트와 두 서비스 및 제품 등록이 모두 없던 진짜 최초 설치에서만 canonical empty `directory.xml`·`pending.xml`과 최초 `config.xml`을 생성한다. 보존 데이터 재설치·repair에서 이 primary들이 누락되면 high-water를 0으로 초기화하지 않고 설치를 실패시킨다. 와치독은 자동 시작, 메인은 지연 자동 시작으로 구성하고 서비스 의존성 없이 와치독을 먼저 시작한다. SCM failure restart는 와치독 자체에만 두고 메인 서비스에는 구성하지 않는다. 메인 재시작은 3회 연속 실패와 10분 3회 제한을 적용하는 와치독만 수행해 SCM이 suppression latch를 우회하지 않게 한다. post-install 구성에 들어가기 전 취소·실패 경로에서는 원래 실행 중이던 서비스를 와치독부터 다시 시작하며, 원래 중지된 서비스를 임의로 시작하지 않는다. 주소 변경·최초 상태 생성 중 실패하면 `config.xml`·`directory.xml`·`pending.xml`과 각 backup, 설치기 소유 URL ACL·방화벽 규칙, 제품 등록, 서비스 구성·보안과 기존 파일 ACL을 복원한다.

Inno Setup은 `ssPostInstall` 전에 설치 파일 교체와 uninstaller·설치 로그 확정을 끝내므로 그 이후 helper 오류에서는 이미 교체한 파일과 uninstall metadata를 자동 rollback하지 않는다. 이 시점의 구성 실패는 setup-state를 폐기하고 두 서비스를 중지 상태로 남겨 부분 상태의 자동 실행을 막으며, 관리자가 원인을 해소한 뒤 같은 설치 파일로 repair해야 한다. 이는 이전 바이너리와 uninstall metadata까지 원자 복원한다는 의미가 아니다. 구성 rollback 자체가 불완전한 경우에도 서비스를 자동 재시작하지 않는다.

설치 폴더와 보존 데이터는 protected exact DACL로 다시 적용하고 하위 항목의 외부 명시적 ACE를 제거한다. `secrets\peer.dat`은 런타임 계약과 같은 별도 protected exact DACL을 다시 적용한다. URL ACL은 서비스 정의가 남아 있지 않아도 Windows의 canonical `sc.exe showsid` 결과로 계산한 메인 서비스 SID의 exact SDDL을 사용한다. 방화벽 규칙·Event Source·제품 등록은 설치기 owner marker, 트레이 자동 시작은 exact 실행 경로, 로컬 운영자 그룹은 고정 설명으로 소유권을 확인한다. Event Source 또는 제품 등록에 exact owner marker만 남고 필수 값이 누락된 중간 상태는 repair가 채워 수렴시킨다. 같은 이름의 외부 소유 리소스, 알 수 없는 값 또는 이미 존재하는 충돌 값은 덮어쓰기·삭제하지 않고 설치 또는 제거를 중단한다.

## 설치 산출물

- 설치 파일명은 루트 [`VERSION`](../VERSION)의 값을 사용해 `DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe`로 만든다.
- 제품 버전과 빌드 번호를 Inno Setup 스크립트나 패키징 스크립트에 복사하거나 하드코딩하지 않는다.
- 코드 서명 인증서가 없으므로 현재 설치 파일에 코드 서명을 적용하지 않는다. 오프라인 설치용 `.sha256`이나 다른 체크섬·manifest를 생성·배포·검증하지 않으며 제공된 설치 EXE를 수동으로 직접 실행한다.
- 이 방식으로는 설치 파일의 배포자 출처, 반입 중 변조와 손상을 기술적으로 확인할 수 없다. 바뀐 EXE가 설치 권한으로 실행될 수 있는 잔여 위험은 [개발계획 §8.4](../docs/plan/service-directory-03-development.md#84-코드-서명체크섬-없는-오프라인-패치-예외-기록)의 승인 범위이며, 이를 무결성 검증이 된 배포라고 표현하지 않는다.
- 설치 EXE payload에는 저장소 루트 `THIRD-PARTY-NOTICES.md`와 실제 Release restore 결과에서 확정한 모든 직접·전이 의존성의 라이선스 고지 및 SBOM을 포함한다. 이 고지는 `installer\` 출력 루트에 별도 파일로 만들지 않아 최종 설치 산출물은 EXE 하나만 유지한다.

생성된 설치 EXE는 Git에 커밋하지 않는다. `.iss`, 설치 지원 PowerShell 소스와 이 문서는 추적 대상으로 유지한다.

## Release 디버그 심볼

`Release|x64` 빌드는 최적화된 바이너리와 `pdbonly` PDB를 생성한다. PDB는 다음 위치에 포함하지 않는다.

- 이 `installer\` 출력 디렉터리
- Inno Setup 설치 payload
- 운영 장비의 `%ProgramFiles%\DEEPAi\ServiceDirectory\`

실제 배포한 빌드마다 다음 항목을 하나의 일치하는 심볼 세트로 묶어 접근 통제된 내부 심볼 저장소에 보관한다.

- 배포한 EXE와 DLL
- 각 바이너리와 정확히 일치하는 PDB
- 해당 시점의 `VERSION`
- 소스 commit ID
- MSBuild, C# compiler와 Inno Setup 버전
- 세트에 포함된 각 파일의 SHA-256 hash manifest. 접근 통제된 내부 장애 분석·빌드 추적에만 사용하며 설치 EXE에 포함하거나 오프라인 설치 승인·실행 조건으로 사용하지 않음
- 설치 EXE payload에 포함한 제3자 라이선스 고지와 SBOM

로컬에서 내부 심볼 세트를 준비해야 할 때는 저장소 루트 `artifacts\service-directory\symbols\`만 임시 staging 위치로 사용한다. 이 경로는 Git에서 제외되며 `installer\`로 복사하지 않는다.

심볼 세트는 해당 빌드의 지원 종료 후 3년까지 보존한다. 지원 종료일이 정해지지 않았으면 삭제하지 않는다. 접근 권한은 장애 분석과 보안 대응에 필요한 담당자로 제한한다.
