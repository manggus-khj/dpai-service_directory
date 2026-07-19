# 설치 산출물과 디버그 심볼 정책

이 디렉터리는 저장소 루트 기준 `installer\`이며 최종 Inno Setup 설치 EXE의 출력 위치다. 현재는 설치 실행체와 Inno Setup 스크립트가 구현되지 않았으므로 이 문서는 산출물 계약만 정의한다.

## 설치 산출물

- 설치 파일명은 루트 [`VERSION`](../VERSION)의 값을 사용해 `DEEPAi-ServiceDirectory-{version}-build.{build}-x64.exe`로 만든다.
- 제품 버전과 빌드 번호를 Inno Setup 스크립트나 패키징 스크립트에 복사하거나 하드코딩하지 않는다.
- 코드 서명 인증서가 없으므로 현재 설치 파일에 코드 서명을 적용하지 않는다. 오프라인 설치용 `.sha256`이나 다른 체크섬·manifest를 생성·배포·검증하지 않으며 제공된 설치 EXE를 수동으로 직접 실행한다.
- 이 방식으로는 설치 파일의 배포자 출처, 반입 중 변조와 손상을 기술적으로 확인할 수 없다. 바뀐 EXE가 설치 권한으로 실행될 수 있는 잔여 위험은 [개발계획 §8.4](../docs/plan/서비스디렉토리_개발계획.md#84-코드-서명체크섬-없는-오프라인-패치-예외-기록)의 승인 범위이며, 이를 무결성 검증이 된 배포라고 표현하지 않는다.
- 설치 EXE payload에는 저장소 루트 `THIRD-PARTY-NOTICES.md`와 실제 Release restore 결과에서 확정한 모든 직접·전이 의존성의 라이선스 고지 및 SBOM을 포함한다. 이 고지는 `installer\` 출력 루트에 별도 파일로 만들지 않아 최종 설치 산출물은 EXE 하나만 유지한다.

생성된 설치 EXE는 Git에 커밋하지 않는다. 향후 추가할 `.iss` 소스와 이 문서는 추적 대상으로 유지한다.

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

로컬에서 내부 심볼 세트를 준비해야 할 때는 저장소 루트 `artifacts\symbols\`만 임시 staging 위치로 사용한다. 이 경로는 Git에서 제외되며 `installer\`로 복사하지 않는다.

심볼 세트는 해당 빌드의 지원 종료 후 3년까지 보존한다. 지원 종료일이 정해지지 않았으면 삭제하지 않는다. 접근 권한은 장애 분석과 보안 대응에 필요한 담당자로 제한한다.
