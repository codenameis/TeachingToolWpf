# 2026-10-05 Canvas 재사용 경계 검증

기준: [구현 계획](../tasks/canvas-reuse-plan.md), [ADR-031](decisions/0031-reusable-canvas-boundaries.md).

NetVips 선택형 연결의 최신 결과는 이 문서 마지막의 **ADR-032 후속 검증**을 따른다. 앞부분은 최초 경계 분리 당시의 기록이다.

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

## ADR-032 후속 검증 — NetVips 선택형 연결

기준: [ADR-032](decisions/0032-optional-canvas-file-decoder.md). 동일 브랜치의 로컬 변경이며 커밋·푸시를 수행하지 않았다. 증거 루트는 `artifacts/netvips-boundary`다.

| 항목 | 결과와 증거 |
|---|---|
| 경계 검사의 실제 검출 | `tests/boundary-before.trx`: 기존 구조에서 전이 VisionImaging/NetVips 및 공개 LatestFrameMailbox 노출2개 FAIL. 변경 후 `boundary-after.trx`의 집중22개 PASS |
| 전체 회귀 | `tests/regression*.trx`: Core185 + Canvas113 + 앱240 + Inspection107 + Algorithm248 + CUDA52 = 945 PASS, 실패/skip0. 기존 Core의 프레임/가시 타일5개를 Canvas 테스트로 이동 |
| 빌드·변경 범위 형식 | 전체 Release 경고0/오류0. 변경 범위 whitespace·명명/style 검증 PASS. `format-scope-style.log` exit0, `git diff --check` exit0 |
| 전체 형식 | `format-full.log`: 저장소 스크립트 exit1. 앞 절에 나열한 기존6개 파일만 whitespace FAIL. `preexisting-format-findings.json`으로6개 모두 이번 HEAD와 동일함을 확인 |
| 디코더 없는 실제 호스트 | `probe/CanvasOnly.csproj`는 Canvas만 참조. 새 `canvas-only` 배포의 deps.json·파일과 `report.json`의 실제 assembly/native module에서 NetVips/libvips/VisionImaging 구현 부재 확인 |
| 독립 8K 렌더 | `canvas-only/report.json`, `canvas-only.png`: 8192² Gray8 원본 버퍼 공유, 회전 ROI·선택 핸들·문구, Present/fence 완료·캡처 PASS. RTX3060 Laptop, VRAM5.9GiB/budget5.1GiB, DX12 debug on |
| 기존 파일 샘플 | `file-sample-smoke/output/report.json`: 명시적 VisionImaging 참조로8K/24K 절차적 소스·PNG·ROI/이미지 이력·실패 원자성 PASS/exit0 |
| 기존 앱 | `app-smoke/artifacts/smoke.log`, `inspection-smoke/artifacts/inspection-smoke.log`: 편집·필터·8K라이브·원본 검사·CPU/CUDA·실제 경계/판정 표시 PASS/exit0 |
| 처리 경로 보존 | `behavior-preservation.json`: mailbox·가시 타일 계산·ImageLoader 구현 본문 동일(소속/공백 제외). 검사 직접 Gray8·ROI pipeline/snapshot 변경 없음 |

검증 호스트의 첫 실행은 `OverlayPresented`에 필요한 `BatchLabels=true` 설정을 빠뜨려 timeout이었다. `probe/initial-harness-failure.json`에 보존했다. 기존 공개 계약을 호스트에 적용해 재실행했으며 제품 렌더러나 성공 조건은 변경하지 않았다.

프레임 공유 확인은 CPU 전달 구간에 대한 것이다. 기존 화면 축소·GPU 업로드 복사까지 없다는 뜻이 아니다. OpenCV managed 필터 패키지와 WPF/DX12는 여전히 Canvas의 의존성이다.

### 성능 비교 조건

`Compare-Performance.ps1`은 별도 before/after 배포본을 교차 실행한다. 8K는7680×4320 BGRA32 합성 원본, ROI500개,1920×1080 표시, 각30초 중 metrics clock 앞10초 제외, 전후 각각3회다. DX12 debug는 끈다. CSV의 ready→Present는 프레임 작성 완료부터 제출까지이며 카메라 수집·원본 생성과 모니터 scan-out을 포함하지 않는다. 정식70초×3 수용 시험보다 짧은 회귀 비교다.

24K는24576×131072 Gray8/Gray16 Deflate BigTIFF다. before/after 각각 새 작업 디렉터리·새 디스크 피라미드를 사용해 최초 import와 같은 캐시 재개방을 구별한다. 최초 비교의 작업 집합 차이를 확인하려고 after→before 역순으로 한 번씩 더 실행했다. 각 형식·버전당2회이며 OS 파일 캐시를 강제로 비우지는 않는다. 생성된 파일·캐시는 artifacts에 보존한다.

### 8K 표시 결과

`performance/artifacts/summary.json`과 각 CSV에 원시 결과를 보존했다. 변경 전후 DLL SHA256은 `binary-hashes.json`에 기록했으며 서로 다르다. 각 행은 동일 순서의 한 실행이며 GPU texture 논리 사용량 최대는 모두16.8MiB였다.

| 실행 | 제출 FPS | CPU p95 ms | GPU p95 ms | ready→Present p95 ms | 표시 드롭 | private 최대 MiB |
|---|---:|---:|---:|---:|---:|---:|
| 변경 전1 | 59.985 | 12.621 | 7.272 | 13.918 | 0 | 1114.7 |
| 변경 후1 | 59.976 | 12.939 | 7.271 | 14.236 | 1 | 1096.4 |
| 변경 전2 | 60.016 | 8.501 | 7.279 | 10.709 | 0 | 971.3 |
| 변경 후2 | 59.953 | 9.726 | 7.284 | 11.316 | 0 | 966.3 |
| 변경 전3 | 60.008 | 8.509 | 7.279 | 10.744 | 0 | 964.9 |
| 변경 후3 | 59.937 | 8.490 | 7.276 | 9.973 | 0 | 967.7 |

양쪽 모두 약60FPS이며 지연·private 메모리 범위가 겹친다. 이번 짧은 비교에서 지속적인 성능 저하는 관측하지 않았지만, 변경 후 첫 실행에 드롭1개가 있었으므로 무손실·동일 지연·성능 개선을 보장하는 결과로 해석하지 않는다.

### 24K 파일 결과

`large-before`, `large-after`, `large-after-repeat`, `large-before-repeat` 아래의 `artifacts/large-image-results.json`에 보존했다. 최초 import는 기존 디스크 피라미드 생성과 열기를 포함하며 fixture 파일 생성 시간은 제외한다. 재개방은 이미 생성된 동일 피라미드를 사용한다. 메모리는 파일 생성 후 import/재개방 구간을100ms 간격으로 관측한 프로세스 값이며, 최종 화면 표시를 포함한 전체 실행 최고값이 아니다.

| 실행 순서 | 형식 | 최초 import 초 | 캐시 재개방 ms | private 최대 MiB | 작업 집합 최대 GiB |
|---|---|---:|---:|---:|---:|
| 변경 전1 | Gray8 | 8.042 | 12.141 | 291.8 | 1.063 |
| 변경 전1 | Gray16 | 20.599 | 5.655 | 346.0 | 4.343 |
| 변경 후1 | Gray8 | 7.785 | 11.957 | 289.6 | 1.003 |
| 변경 후1 | Gray16 | 20.729 | 5.853 | 344.6 | 6.290 |
| 변경 후2 | Gray8 | 7.768 | 12.046 | 290.7 | 1.041 |
| 변경 후2 | Gray16 | 20.972 | 6.523 | 349.7 | 4.295 |
| 변경 전2 | Gray8 | 8.647 | 12.115 | 292.9 | 1.021 |
| 변경 전2 | Gray16 | 20.511 | 5.803 | 344.6 | 6.262 |

Gray8 원본은3GiB, Gray16은6GiB이며 압축 파일 크기는 각각26,923,380/47,872,884bytes다. 모든 실행에서 마지막 원본 타일 값224/57568 검증과 실제 DX12 표시·캡처가 성공했다. 메모리 매핑을 포함할 수 있는 작업 집합과 프로세스 private bytes를 구별한다. Gray16 작업 집합은 두 버전 모두 약4.3–6.3GiB로 변동했고 private는 약345–350MiB였다. 첫 비교의 작업 집합 증가가 변경 후에만 재현되지는 않았다. 운영체제의 상주 페이지 정책 등 정확한 원인은 이번 계측으로 확정하지 않으며 전체 메모리를 수백MiB로 제한한다고 주장하지 않는다.

파일 처리 시간과 private 메모리에 큰 차이는 관측하지 않았다. 다만2회만으로 성능 동등성을 확정할 수 없으며, Gray16 최초 import는 변경 후 두 번 모두 조금 길었다. 디코더 교체의 성능 비교로 확대 해석하지 않는다.

라이브 managed 할당량·실제 카메라·장시간 안정성·다른 GPU/Linux·검사125/80ms 목표는 이번 검증에 포함하지 않는다.
