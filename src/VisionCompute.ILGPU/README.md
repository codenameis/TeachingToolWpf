# 선택형 CUDA 전처리와 후보 점수

`CudaImagePreprocessor`는 VisionAlgorithm의 `IImagePreprocessor`와 `IShapeCandidateScorer`를 구현한다. ILGPU/ILGPU.Algorithms 1.5.3, NVIDIA CUDA GPU/드라이버가 필요하다. CPU accelerator로 자동 대체하지 않으며 플랫폼 UI·DX12를 참조하지 않는다.

- 앱의 혼합 검사 정책: 모든 전처리(Gaussian·Sobel·정규화·방향 binning)와 원본 정밀 검사는 CPU worker에서 실행하고, 상위 후보 점수 계산만 CUDA로 실행한다. `ShapeMatcher`에 후보 점수 공급자가 주입되면 이 정책을 사용한다.
- GPU 후보 점수: 상위 방향 bit 영상 업로드 → 가로·세로 확산 → 8방향 cosine LUT → 모든 템플릿의 정수 점수 배치 → score grid 다운로드. 임계값·NMS·후보 예산·연속 검증·최종 점수는 공통 CPU 알고리즘이 결정한다.
- 혼합 정책은 작은 검색 창·원본 crop의 장치 왕복과 CPU 정규화 직렬 대기를 줄인다. GPU 실패 뒤 자동으로 CPU 성공을 보고하는 폴백이 아니며, 후보 점수 장치 오류는 검사 실패로 전달한다.
- 혼합 검색을 위한 내부 전처리는 CPU 누적 검색에만 필요한8개 선형 응답 평면과 phase 임시 배열을 만들지 않는다. CPU 국소 위치 탐색에 쓰는 raster 확산 영상과 LUT는 유지한다. 공개 `PreparedSearchImage.Create`와 CPU·전처리 전용 경로의 자료 계약은 바꾸지 않는다.
- 이전 전처리 전용 계약도 유지한다. 후보 점수 공급자 없이 `IImagePreprocessor`만 주입하거나 `CudaImagePreprocessor.Prepare`를 직접 호출하면 Gray8 업로드 → GPU Gaussian 가로/세로 → GPU Sobel dx/dy → 미분 배열 다운로드 → CPU 정규화를 수행한다. 이 경로의 호출당 최대 입력은8,388,608px이며 논리 GPU 버퍼는 픽셀당17byte, 최대136MiB다. host 배열·출력·드라이버/allocator 비용은 별도다.
- 후보 요청은 최대2Mpx, 반환 score는 최대16,777,216개 ushort(32MiB), 템플릿은 최대16,384개·각512특징이다. 호출자는 상한 안으로 템플릿 배치를 나누며 순서를 유지한다. 후보 장치 버퍼의 별도 최댓값은 약148MiB다. 두 경로를 함께 보유한 세션의 논리 장치 버퍼 상한은 약284MiB이며 드라이버·컨텍스트·host 메모리는 포함하지 않는다.
- 단일 소유자에서 버퍼를 재사용하고 취소·해제는 제출된 작업의 안전한 종료를 기다린다. GPU 오류 후 해당 실행기를 재사용하지 않는다.

`CudaInspectionSession`은 앱/CLI 실행 범위에서 명시적으로 소유한다. `PrepareAsync`가 장치와 커널을 한 번 준비하고, `Rent`는 동시에 하나의 검사 lease를 반환한다. lease의 `Dispose`는 장치를 다음 검사에 보존하며 진단 값은 그 검사 동안의 누적 차이만 반환한다. 세션을 검사 중 해제하면 active lease의 반환 후 장치를 해제한다. 초기화 중 해제도 native 초기화가 끝난 뒤 생성 자원을 정리한다. 취소 후 안전하게 완료된 장치는 재사용하고, 장치 오류 후에는 같은 세션의 후속 검사를 차단한다.

`Diagnostics.TotalMilliseconds`는 Prepare의 버퍼 준비·전송·커널·읽기·CPU 정규화를 포함한다. 앱의 후보 점수 혼합 경로에서는 이 GPU 전처리 호출 수가0이며, CPU 전처리를 GPU 실행으로 표시하지 않는다. `CandidateDiagnostics.TotalMilliseconds`는 방향 응답과 점수 배치의 전송·GPU 대기·반환을 포함한다. 두 backend 타이머는 장치 호출 직렬화 lock을 얻은 뒤 시작하므로 호출자의 lock 대기까지 포함한 전체 검사 시간과 구별한다. 세션의 `InitializationMilliseconds`는 장치 초기화·커널 컴파일·방향 LUT 업로드를 별도로 측정한다. 첫 준비 비용을 반복 검사 시간에 숨기지 않고, 속도 비교는 원본 읽기·최종 판정까지 포함한 서비스 전체 시간으로 수행한다. [검증과 한계](../../docs/portable-inspection-validation.md)를 따른다.

2026-10-05 정규화 전체를 GPU로 옮긴 실험은 gradient 크기 약1ULP 차이가 최종 pose/score에 전파되어 기존 정확 동등성 11개가 실패했다. 이 변경은 원복했으며 허용오차를 늘리지 않았다. 전처리 전용 경로와 후보 점수 혼합 경로, CPU/GPU 최종 결과의 정확 동등성, 세션 수명 집중52개 테스트가 통과했다(`artifacts/cuda-candidate-tests/cuda-score-only-hybrid.trx`). 실제8K 전체 검사 속도와 첫 준비 비용은 별도 성능 기록을 기준으로 판단한다.
