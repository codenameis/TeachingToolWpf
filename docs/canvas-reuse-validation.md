# 2026-10-05 Canvas 재사용 경계 검증

기준: [구현 계획](../tasks/canvas-reuse-plan.md), [ADR-031](decisions/0031-reusable-canvas-boundaries.md).

이 문서는 새 프로젝트 경계에서 실행한 검증만 기록한다. 과거 Core/Canvas/검사 통과 건수를 이번 변경의 통과 결과로 재사용하지 않는다.

## 변경과 검증 환경

- 작업 브랜치: `codex/vision-canvas-boundaries`.
- 범위: 순수 기하·이미지 계약·편집 코어 소속 분리, 참조/namespace 갱신, 검사·앱 없는 독립 WPF 샘플.
- 보존 범위: ROI/이미지/레시피 이력, 소스·프레임·reader 소유권, 원본 검사 결과와 불완전 경고, 렌더 스냅샷/identity 캐시.
- 커밋·원격 푸시: 사용자 선택에 따라 수행하지 않는다.
- SDK·구성: .NET SDK 10.0.401, Windows x64, Release.
- 실제 adapter: NVIDIA GeForce RTX 3060 Laptop GPU, VRAM 5.9 GiB, 실행 budget 5.1 GiB, DX12 debug 활성.
- 이번 변경 전부터 있던 분석기 도입 미커밋 파일을 보존했다. 분석기 정책이나 실패 기준을 완화하지 않았다.

## 검증 결과

| 항목 | 상태 | 확인할 증거 |
|---|---|---|
| 공통 계약·편집 코어의 플랫폼 독립성 | PASS | net10.0, 중립 managed 참조 허용 목록·native 호출 부재 경계 테스트 |
| Canvas의 앱·검사·알고리즘 참조 부재 | PASS | CanvasBoundaryTests 전이 참조·공개 API 검사 |
| 검사·영상 I/O의 편집 코어 참조 부재 | PASS | PortableBoundaryTests 전이 참조·공개 서비스 계약4개 검사, PortableInspection 빌드 |
| 독립 샘플의 공개 API 조립 | PASS | 직접 ProjectReference는 Canvas 하나. 배포 폴더·실제 로드 어셈블리에 앱/검사/알고리즘 없음 |
| 기하·명시적 계층·선택·변형·Undo/Redo | PASS | 기존 Core190개, 독립 샘플과 앱 실제 편집 smoke |
| 영상·프레임·reader lease·취소·정리 | PASS | Canvas108개·Inspection107개와 기존 앱 회귀에 포함. 샘플 소스 교체 해제·실패 원자성 확인 |
| 검사·앱 결과 보존 | PASS | Algorithm248·CUDA52·앱240개. OpenCV/형상/CUDA/일반 도형 실제 검사 smoke |
| 솔루션 Release 빌드 | PASS | 전체 및 PortableInspection 각각 exit0, 경고0/오류0 |
| 분리·샘플 범위 형식·diff 검사 | PASS | format-scope-verify.log exit0, git diff --check exit0. 아래 기존 위반은 제외해 따로 보고 |
| 전체 솔루션 형식 검사 | FAIL(기존 위반) | Format-Code.ps1 -Verify exit1. 아래6개 파일의 기존 서식 위반 |
| 독립 샘플 실제 WPF/DX12 실행 | PASS | 8192²/24576×8192 합성 소스·PNG 파일·ROI/이력·overlay fence·캡처, 새 배포본 exit0 |
| 기존 앱 실제 WPF/DX12 실행 | PASS | 편집·필터·8K 라이브 smoke exit0. 검사 smoke와 최종 배포본 검사 smoke 각각 exit0 |

## 실행 증거

증거 루트는 `artifacts/canvas-reuse`다. 최종 분리 결과와 별개로 기존 검사 지연 목표를 재측정한 것으로 해석하지 않는다.

- `baseline-tests/*.trx`: 기존936개 PASS, 신규 Canvas 경계5개는 기존 구조에서 예상 FAIL. 단순히 성공하는 검사만 추가하지 않았음을 확인했다.
- `after-tests.log`, `after-tests/*.trx`: **945개 PASS, 실패0/skip0** = Core190 + 앱240 + Canvas108 + Algorithm248 + Inspection107 + CUDA52. 기존936개와 신규 경계9개다.
- `build-final.log`, `portable-build.log`: 전체 Release 및 Canvas/편집 코어가 없는 검사 솔루션 빌드. 각각 경고0/오류0.
- `app-smoke/artifacts/smoke.log`: 실제 HWND 입력, 부모/자식·연쇄 삭제·Undo/Redo·폴리곤·미니맵, OpenCV 필터·8K 라이브와 렌더 재시작.
- `inspection-smoke/artifacts/inspection-smoke.log`, `published-app-smoke/artifacts/inspection-smoke.log`: 기존 앱 및 최종 배포본의 파일 학습·검색·두 pose·CPU/CUDA·일반 도형4종·입력·결과 표시. 모두 렌더 오류 없음/exit0.
- `published-sample-smoke/report.json`, `smoke.log`, PNG: 독립 샘플 최종 배포본 exit0/PASS. TeachingToolWpf/Core/검사/알고리즘/CUDA 어셈블리 미로드. 셰이더·native DLL·라이선스 고지를 포함한 `publish-sample`에서 실행했다.
- 독립 샘플의 8K와24K 최대 대여 타일 배열은 각각1MiB, 종료 직전 private bytes는 보고서 값 약247MiB다. 실제 타일 읽기 관측이며 전체 프로세스나 GPU 메모리 상한이 아니다.
- 교차 리뷰에서 이동한 Core C#34개를 using/namespace·공백 제외 비교했다. ImageReference 추출, PolygonClosure의 외적 계산 로컬화, VideoFrame/mailbox 분리 외 계산 본문은 동일하다. 이력·공간 블록 identity·버퍼 소유권을 재작성하지 않았다.

## 전체 형식 검사의 기존 실패

`scripts/Format-Code.ps1 -Verify`는 다음6개 파일의 기존 줄바꿈·중괄호 규칙 위반 때문에 실패했다. `preexisting-format-findings.json`은 using을 제외한 본문이 **공백까지 HEAD와 동일**함을 기록한다. 분리 작업에서 기존 알고리즘을 일괄 재포맷하지 않았다.

- `src/TeachingToolWpf/Diagnostics/RotationInspectionSmoke.cs`
- `src/TeachingToolWpf/Presentation/ShapeMatchingViewModel.Rotation.cs`
- `src/VisionAlgorithm/OpenSrc/PatternMatching/FineDetailModel.cs`
- `src/VisionAlgorithm/OpenSrc/PatternMatching/MatchingContracts.cs`
- `src/VisionAlgorithm/OpenSrc/PatternMatching/PreparedSearchImage.cs`
- `src/VisionAlgorithm/OpenSrc/PatternMatching/ShapeMatcher.cs`

변경 범위 검사는 이동·새 파일과 이번 수정 파일을 대상으로 수행했다. 위 파일 중 using만 제거된 RotationInspectionSmoke는 제외했으며, 전체 검사의 FAIL을 이 부분 PASS로 대체하지 않는다. 원래 분석기의 별도 감사 실패 역시 이 작업에서 해결했다고 주장하지 않는다.

## 미검증 범위와 해석

독립 샘플의 빌드·실행은 재사용 프로젝트 경계를 검증한다. 배포 패키지 공개, 모든 외부 WPF 앱 조합, 물리 포인터 전체 E2E, 다른 GPU·DPI, 8K/24K 장시간 성능과 Linux 실실행을 증명하지 않는다.

이번 변경은 검사 최적화 작업이 아니다. CPU 125 ms 이하/CUDA 80 ms 미만의 기존 수용 상태와 현장 검출률은 기존 [논문 형상 검증](paper-shape-125ms-validation.md)을 따르며, 단위 테스트나 샘플 실행 성공으로 이를 PASS 처리하지 않는다.
