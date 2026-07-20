# 할 일 목록

```text
최초 작성일: 2026-07-20
최종 변경일: 2026-07-20
```

## AGENTS.md rev 9 적용 — 저장소 구조 정합화

- [ ] 계획 문서 7건의 파일명에서 `service-directory-` 접두를 제거해 개명: `service-directory-00-overview.md`→`00-overview.md`, `service-directory-01-hardening.md`→`01-hardening.md`, `service-directory-02-certificate-transition.md`→`02-certificate-transition.md`, `service-directory-03-development.md`→`03-development.md`, `service-directory-04-api.md`→`04-api.md`, `service-directory-04-api-01-external-application.md`→`04-api-01-external-application.md`, `service-directory-04-api-02-internal.md`→`04-api-02-internal.md` (근거: AGENTS.md §2.2, §3.1)
- [ ] 계획용 자산 폴더를 주인 md의 새 이름에 맞춰 개명: `docs/plan/service-directory-03-development/`→`docs/plan/03-development/`, `docs/plan/service-directory-04-api/`→`docs/plan/04-api/` (근거: AGENTS.md §2.1, §3.4)
- [ ] 개명에 따른 `docs/plan/service-directory-*` 참조 경로 일괄 갱신: 계획 문서 간 상호 링크, `README.md`, `installer/README.md`, `src/DEEPAi.ServiceDirectory.ExternalProtocol/DEEPAi.ServiceDirectory.ExternalProtocol.csproj`, `src/DEEPAi.ServiceDirectory.InternalProtocol/DEEPAi.ServiceDirectory.InternalProtocol.csproj`, `src/DEEPAi.ServiceDirectory.Tray/DEEPAi.ServiceDirectory.Tray.csproj`의 XSD 경로. 갱신 후 저장소 전체에서 `service-directory-0` 패턴 재검색으로 누락 확인. 링크 외 내용이 바뀐 계획 문서는 §3.2에 따라 `revision`과 `최종 변경일` 갱신 (근거: AGENTS.md §2.2, §3.2)
- [ ] 빌드 생성 파일 위치를 `artifacts/service-directory/`에서 `artifacts/` 바로 아래로 평탄화: `Directory.Build.props`의 `RepositoryArtifactsRoot`, `.gitignore`의 `/artifacts/service-directory/`, `tools/test.ps1`의 bin·test-results 경로, `tools/package.ps1`의 package·bin·obj 경로를 갱신하고, 갱신 후 `artifacts\service-directory` 패턴 재검색으로 누락 확인 (근거: AGENTS.md §2.2)
- [ ] `README.md` 갱신: 계획 문서 표를 새 파일명 링크로 교체, 자산 폴더·artifacts 경로 설명을 새 구조로 수정, 할 일 목록 파일(`docs/plan/todo-01.md`)의 용도와 위치 소개 추가 (근거: AGENTS.md §1.1, §2.2, §3.5)

## 직접 지시 작업 기록

- [x] 로컬 빌드 잔재 삭제: 최상위 `TestResults/`(레거시 UI 미리보기 PNG 포함)와 `tests/DEEPAi.ServiceDirectory.Tests/bin`·`obj`. 세 경로 모두 Git 미추적(ignored) 확인 후 삭제했으며 저장소 변경 없음 (근거: 사용자 직접 지시, AGENTS.md §2.2 artifacts 일원화 방향) (완료: 2026-07-20)
