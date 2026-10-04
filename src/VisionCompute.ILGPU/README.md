# 선택형 CUDA 전처리

`CudaImagePreprocessor`는 VisionAlgorithm의 `IImagePreprocessor`를 구현한다. ILGPU/ILGPU.Algorithms 1.5.3, NVIDIA CUDA GPU/드라이버가 필요하다. CPU accelerator로 자동 대체하지 않으며 플랫폼 UI·DX12를 참조하지 않는다.

- GPU: Gray8 업로드 → Gaussian 가로/세로 → Sobel dx/dy → 마지막 미분 배열 다운로드.
- CPU: 공통 정규화·방향 binning. 이후 NMS·응답맵·후보 검색·점수·정밀화도 CPU.
- 호출당 최대 8,388,608px. 논리 GPU 버퍼는 픽셀당 17byte, 최대136MiB다. host 배열·출력·드라이버/allocator 비용은 별도다.
- 단일 소유자에서 버퍼를 재사용하고 취소·해제는 제출된 작업의 안전한 종료를 기다린다. GPU 오류 후 해당 실행기를 재사용하지 않는다.

`Diagnostics.TotalMilliseconds`는 Prepare의 버퍼 준비·전송·커널·읽기·CPU 정규화를 포함한다. 생성자의 CUDA context 초기화와 커널 컴파일은 포함하지 않으므로 실제 속도 비교에는 서비스 전체 시간을 사용한다. [검증과 한계](../../docs/portable-inspection-validation.md)를 따른다.
