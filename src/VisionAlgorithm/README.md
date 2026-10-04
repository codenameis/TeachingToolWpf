# VisionAlgorithm

순수 C# CPU 형상 매칭 프로젝트다. .NET 10을 대상으로 하고 WPF, TeachingToolWpf.Core, DX12, OpenCV와 상용 SDK를 참조하지 않는다. 앱에서 `VisionAlgorithm`을 사용하고, 영상·ROI 변환은 앱의 `Imaging/Matching` 어댑터가 맡는다.

주력 구현은 [OpenSrc/PatternMatching](OpenSrc/PatternMatching/README.md)의 응답맵 후보 검색 → 연속 gradient 검증 → point-to-line 정밀화다. `GrayImage`는 Gray8 buffer와 width/height/stride, `TrainingRegion`은 학습 마스크와 기준점·각도, `ShapeModel`은 불변 학습 결과를 표현한다. 학습 ROI 기준 각도 범위를 넣으면 검색 결과는 이미지 기준 절대각으로 반환한다.

현재 기본 공급자는 OpenSrc 패키지뿐이다. [Cognex](Cognex/README.md)와 [Halcon](Halcon/README.md)는 향후 SDK 연결 경계 문서이며 동작 구현이나 가짜 공급자를 포함하지 않는다.

논문 아이디어를 따로 비교하는 CPU 구현은 [OpenSrc/Comparisons](OpenSrc/Comparisons/README.md)에 있다. Directional chamfer, gray-gradient fusion, BOLD 경로는 실험/측정 전용이며 앱의 기본 검사 흐름은 사용하지 않는다. BOLD 비교 입력 상한은 262,144 pixel이다. 같은 합성 입력의 속도·오차와 미검증 범위는 [검증 보고](../../docs/shape-matching-validation.md)에 기록했다.

```powershell
dotnet test .\tests\VisionAlgorithm.Tests\VisionAlgorithm.Tests.csproj -c Release
dotnet run --project .\benchmarks\VisionAlgorithm.Benchmarks\VisionAlgorithm.Benchmarks.csproj -c Release -- .\docs\data\shape-matching-benchmark-2026-10-04.json
```

정확도·성능 및 미실행 검증은 [검증 기록](../../docs/shape-matching-validation.md)을 확인한다.
