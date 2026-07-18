# 설치 산출물과 디버그 심볼 정책

이 디렉터리는 저장소 루트 기준 `installer\`이며, 최종 Inno Setup 설치 파일과 그 SHA-256 manifest의 출력 위치다. 현재는 설치 실행체와 Inno Setup 스크립트가 구현되지 않았으므로 이 문서는 산출물 계약만 정의한다.

## 설치 산출물

- 설치 파일명은 루트 [`VERSION`](../VERSION)의 값을 사용해 `DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe`로 만든다.
- SHA-256 manifest 파일명은 설치 파일명 뒤에 `.sha256`을 붙인다. 예: `DEEPAi-ServiceDirectory-1.0.0-build.3-x64.exe.sha256`.
- manifest는 BOM 없는 UTF-8 단일 행이며 `64자 lowercase SHA-256 hex`, ASCII 공백 두 칸, exact 설치 EXE 파일명, 마지막 LF 한 개 순서로 기록한다. 경로, 주석, 추가 행과 CRLF는 넣지 않는다.
- 제품 버전과 빌드 번호를 Inno Setup 스크립트나 패키징 스크립트에 복사하거나 하드코딩하지 않는다.
- 코드 서명 인증서가 없으므로 현재 설치 파일에 코드 서명을 적용하지 않는다. SHA-256 manifest는 설치 파일과 분리된 승인된 신뢰 채널로 전달해야 하며, 같은 디렉터리에 있는 manifest만으로 출처가 인증됐다고 간주하지 않는다.
- 설치 payload와 오프라인 배포 bundle에는 저장소 루트 `THIRD-PARTY-NOTICES.md`와 실제 Release restore 결과에서 확정한 모든 직접·전이 의존성의 라이선스 고지 및 SBOM을 포함한다. 생성 과정에서 이 고지를 `installer\` 출력 루트에 별도 추적 파일로 복사하지 않고 설치 EXE payload와 배포 bundle 구성 단계에서 가져온다.

생성된 설치 파일과 manifest는 Git에 커밋하지 않는다. 향후 추가할 `.iss` 소스와 이 문서는 추적 대상으로 유지한다.

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
- 세트에 포함된 각 파일의 SHA-256 hash manifest
- 배포 bundle에 포함한 제3자 라이선스 고지와 SBOM

로컬에서 내부 심볼 세트를 준비해야 할 때는 저장소 루트 `artifacts\symbols\`만 임시 staging 위치로 사용한다. 이 경로는 Git에서 제외되며 `installer\`로 복사하지 않는다.

심볼 세트는 해당 빌드의 지원 종료 후 3년까지 보존한다. 지원 종료일이 정해지지 않았으면 삭제하지 않는다. 접근 권한은 장애 분석과 보안 대응에 필요한 담당자로 제한한다.
