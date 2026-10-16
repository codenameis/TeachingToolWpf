# CpuMatching.Cuda

Line2D/Sub-NCC/Directional Chamfer용 선택형 ILGPU CUDA 후보 점수 계산기다. CPU 피라미드/특징 준비·후보 선택·정밀 검증과 함께 사용한다. ONNX·TorchSharp·WPF를 참조하지 않는다. NVIDIA CUDA 장치가 필요하고 CPU 자동 대체는 하지 않는다.

```csharp
using var session = new CudaTemplateScoreSession(); // 호스트가 보관. 아직 GPU를 만들지 않음
using var accelerator = session.Rent();            // 한 검사만 독점 대여
var result = matcher.Find(image, options, cancellationToken, accelerator: accelerator);
var timing = accelerator.Diagnostics;
```

CPU 모드는 기존 `matcher.Find(image, options, cancellationToken)` 호출을 그대로 사용한다. 가속기 대여는 재학습과 관계없다. CPU 알고리즘 프로젝트는 이 프로젝트를 참조하지 않으며 호스트에서만 참조/주입한다.

[WPF/CLI 사용법·검증](../../docs/cpu-matching-cuda.md), [기초 알고리즘 설명](../../docs/cpu-matching-algorithm-guide.md).
