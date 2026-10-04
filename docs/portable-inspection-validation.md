# 플랫폼 독립 검사·후보 예산 검증

2026-10-04. 설계는 [ADR-023](decisions/0023-portable-inspection-and-candidate-budget.md), 실행법은 [CLI README](../src/VisionInspection.Cli/README.md)를 따른다. 실촬영 검출률·실시간 FPS·상용 matcher 동등성을 주장하지 않는다.

## 후보 누락 재현

768×480 합성 영상의 동일 패턴40개, 최소 최종 점수.72, 추가 윤곽 감점1, 최대 결과1000. 설정별 워밍업1회 후5회 중앙값이다.

| 설정 | 기존 검출 | 수정 후 검출 | 기존 ms | 수정 후 ms |
|---|---:|---:|---:|---:|
| 기본 후보2048 / 원본 검증64 | 11/40 | 40/40 | 347.1 | 342.5 |

기존 빠르게는6/40에 그쳤고 수정 스케줄을 써도 검증32개 상한으로40개 전체를 검사할 수 없어 UI에서 제거했다. 점수·지원율 기준, 기본 후보/검증 예산, 정밀화는 낮추지 않았다. 개선점은 같은 물체 주변 후보가 검증 예산을 독점하지 않게 방문 순서를 분산한 것이다. 작은 시간 차이를 속도 향상 근거로 삼지 않는다. 후보가 예산보다 많다는 경고도 유지한다.

신규9개와 관련 기존58개 집중 회귀67 PASS. 회전13.4°/배율1.07,64px 간격의16개 패턴,25% 가림 pose, 같은 중심의 다른 회전·배율, 셀 경계, 예산 이내 순서·취소를 포함한다. 회전·배율·가림/추가 윤곽 fixture13 positive와3 negative에4개 정책을 적용한64행 비교에서 기존 판정·검출 개수 변화0이다. 이 중 추가 윤곽이 많은 positive2개는 전후 모두 점수 미달이며 현장 검출을 완전히 해결했다는 근거로 쓰지 않는다.

원시 자료: [반복 이전](data/fast-profile-repeated-before.json), [반복 이후](data/fast-profile-repeated-after.json), [형상 이전](data/fast-profile-recall-before.json), [형상 이후](data/fast-profile-recall-after.json). 실행 fixture/benchmark는 `artifacts/fast-profile-*` 기록을 보존했다.

## 플랫폼과 CLI

- PASS: VisionImaging/VisionInspection/Algorithm의 WPF·WindowsBase·PresentationCore·VisionCanvas·Vortice 참조 없음. CPU 원본 파일·마스크·타일·취소·결과 상한 회귀.
- PASS: Windows에서 실제 파일 레시피4종 실행. 논문2개, 기하2개, 템플릿1개, 일반 도형4개. `artifacts/portable-cli-*.json`.
- PASS: Linux x64 framework-dependent cross-publish. CLI 게시물에 `libvips.so.42`, `libOpenCvSharpExtern.so`와 중립 managed DLL 포함, WPF/DirectX/Windows native DLL 제외. 각 패키지 라이선스 포함.
- NOT_RUN: 실제 Linux CPU/CUDA 실행. 현재 호스트에는 실행 가능한 WSL 배포판·Docker·Podman이 없다. cross-publish는 native ABI/드라이버 동작 검증이 아니다.

Linux x64에서 .NET 10 SDK를 설치하고 저장소 루트에서 다음을 실행한다. 배포 패키지의 native 시스템 라이브러리와 CUDA 선택 시 NVIDIA 드라이버도 해당 호스트에서 확인해야 한다.

```sh
dotnet test PortableInspection.slnx -c Release
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/primitives.json --output artifacts/linux-primitives.json
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/paper.json --output artifacts/linux-paper.json
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/geometric.json --output artifacts/linux-geometric.json
dotnet run --project src/VisionInspection.Cli -c Release -- --recipe samples/inspection/template.json --output artifacts/linux-template.json
# 지원되는 NVIDIA GPU가 있는 환경에서만 별도로 실행
dotnet test tests/VisionCompute.Tests -c Release
```

Windows의 동일 공통 검증은 `pwsh -File scripts/Test-PortableInspection.ps1`로도 실행한다. GPU 테스트는 `-Gpu`를 명시한다. GPU가 없으면 지원 불가 오류이며 테스트를 skip해 GPU PASS로 집계하지 않는다.

## CUDA 동등성

실제 NVIDIA GeForce RTX 3060 Laptop GPU에서 독립 convolution·Sobel 정답과 최종 pose/점수/품질의 완전 일치를 확인했다. stride·offset·홀수 크기·마스크·타일·prepared 경로·직접 refinement,8 Mpx 극단 종횡비,버퍼 확대/축소,취소 후 재사용·중복 Dispose를 포함한다. 이전 DX12 전용 fence 테스트는 제거한 구현의 계약이며 새 CUDA 동등성·수명 테스트로 검증한다.

GPU는 평활화2패스+Sobel까지만 연속 계산한다. CPU 정규화·방향 binning·검색·검증을 유지한다. API는 단계별 중간 결과를 CPU로 왕복하지 않지만, 원본 업로드와 마지막 미분 배열 다운로드는 필요하다. 전체 서비스 시간에는 CUDA 초기화/커널 준비도 포함한다. 전처리 diagnostics만으로 전체 가속을 주장하지 않는다.

## 8K 전체 검사 시간

원본8192×8192 procedural PCB, 기존 사용자 핀 ROI(중심759.518/546.178,105.827×129.459), 각도±180°,최소점수.72,추가윤곽감점1,최대결과1000. 각 설정 워밍업1회 후3회, 전원·열 조건은 별도 통제하지 않았다. GPU/CPU 모두 매회256개와 정답 위치1px 기준을 통과했고, 최종 pose·점수·지원율·추가윤곽비율은 완전히 같았다.

| 설정 | 호출 전체 중앙값 ms |
|---|---:|
| 기본 후보 예산 · CPU1 | 4046.41 |
| 확대 후보 예산 · CPU1 | 4008.10 |
| 기본 후보 예산 · CPU4 | 4019.06 |
| 기본 후보 예산 · CUDA+CPU | 4084.50 |

호출 전체는 `WallMilliseconds`이며 Task 반환과 reader/CUDA 자원 해제까지 포함한다. 서비스 내부 `TotalMilliseconds`는 결과 구성 시점에서 끝나 CPU4046.21/CUDA4060.71ms였다. 전처리 진단은 별도 하위 구간이다. 새 CUDA는 이 입력에서 CPU보다 약0.9% 느렸고, 병렬4와 후보 확대도 확실한 가속으로 판단할 수 없는 작은 차이였다. 따라서 CPU 기본을 유지한다. 이전 DX12보다 빠른 별도 날짜/상태의 기록을 동일 조건 A/B 비교로 주장하지 않는다.

이 8K 핀 영상은 후보 예산에 잘리지 않아 과거 빠르게 예산도256개를 찾는다. 빠르게 모드의 누락 개선 근거는 앞의 어려운40개 fixture이며 서로 다른 결과를 혼동하지 않는다. 원시 반복 데이터는 [CUDA 비교](data/portable-cuda-benchmark-20261004.json), 콘솔 기록은 `artifacts/portable-cuda-benchmark.log`다.

## 전체 회귀

최종 Release **687 PASS / skip0**: Core182,Algorithm152,검사/CLI46,CUDA27,Canvas100,앱180. 기존 앱 테스트 중 원본 서비스 테스트는 VisionInspection.Tests로 이동했고 삭제한 DX12 백엔드의 전용 테스트는 CUDA 동등성·수명 검증으로 교체했다. 테스트 개수 변화만을 커버리지 개선 근거로 쓰지 않는다. `artifacts/portable-verified-tests.log`와 프로젝트별 TRX를 보존했다.

`pwsh -File scripts/Format-Code.ps1 -Verify` PASS, `artifacts/portable-format-verified.log`. 마지막 추가 파일의 줄 끝 형식 오류를 CRLF로 수정한 뒤 통과했다. 빌드 경고·오류0. 리뷰에서 확인한 부분 JSON 옵션 기본값 변경, 지원하지 않는 템플릿 GPU 요청 무시, CLI 배포 고지 누락, CUDA 해제 예외 시 후속 자원 정리 누락을 모두 수정했다. 설계 원칙 예외 없음.

## 실제 앱 검사

별도 배포본 `artifacts/publish-portable-inspection/TeachingToolWpf.exe`의 `--inspection-smoke --gpu-debug`가 exit0으로 통과했다. 기본칩16,OpenCV핀256,논문CPU핀256,CUDA핀256,회전기하2,일반도형4,닫힌오목폴리곤·점선·특징표시·Undo/Redo·잘림경고·오류없는DX12표시를 확인했다. 로그는 `artifacts/portable-inspection-runtime/artifacts/inspection-smoke.log`다. 이 harness의 DX12 readback 합성 캡처를 OS 창 캡처와 혼동하지 않는다.

직접 실행한 새 앱에서 원본 핀 ROI(759.518/546.178,105.827×129.459)를 학습하고 CUDA를 선택해 전체검사를 눌렀다. 기본 후보 예산,점수.72,출력256에서 **256개 / 4424.6ms / OK**였다. 결과의 backend는 `ILGPU CUDA: smoothing + Sobel; CPU normalization`, adapter는 RTX3060 Laptop이다. 기본/확대 예산 UI,실선검출·점선검색을 실제 창에서 확인했다. 원본 OS 스크린샷 `artifacts/portable-inspection-user-state/cuda-pins.jpg`(2560×1392)와 접근성 기록 `.txt`를 남겼다. 단일 UI 실행은 반복 측정과 다르다. 앱은 결과를 표시한 상태로 유지했다.

직접 조작 첫 시도에서는 화면 밖 속성 적용 버튼에 대한 자동화 입력이 의도대로 확정되지 않아 기존 중앙 ROI를 학습했다. 그9개 결과를 핀검사증거로 쓰지 않았다. 속성 패널을 실제로 펼쳐 원본 수치와 `ROI 속성 변경` 확정을 확인하고 다시 학습·검사한256개 결과를 최종 증거로 기록했다.
