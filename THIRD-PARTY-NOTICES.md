# Third-party dependencies

이 샘플은 아래 패키지와 네이티브 라이브러리를 사용합니다. 패키지 nuspec 및 배포자가 제공한 고지를 기준으로 기록했습니다. `licenses/`와 네이티브 라이브러리를 배포 폴더에 함께 유지합니다. framework-dependent 배포는 .NET 10 Runtime을 별도 설치합니다. Publish-LoaderComparison.ps1의 self-contained 배포는 .NET runtime을 포함하며 라이선스와 제공되는 제3자 고지를 licenses/Microsoft.NETCore.App 및 licenses/Microsoft.WindowsDesktop.App에 복사합니다.

| Runtime component | Version | License / source |
|---|---|---|
| PoseMatch-TDCM 가중치·공식 예제 | a6adc3a74fcf38ddbe4beb49e4bc76be07f848a2 | MIT · Copyright(c)2025 ZhouJ6610 · [공식 저장소](https://github.com/ZhouJ6610/PoseMatch-TDCM), [원문](licenses/posematch-tdcm/LICENSE.txt) |
| Microsoft.ML.OnnxRuntime.Gpu / Gpu.Windows / Gpu.Linux / Managed | 1.24.4 | MIT · Microsoft · [LICENSE](licenses/onnxruntime/LICENSE.txt), [제3자 고지](licenses/onnxruntime/ThirdPartyNotices.txt) |
| Vortice.Direct3D12, DXGI, D3DCompiler, DirectX | 3.8.3 | MIT · [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) |
| Vortice.Mathematics | 2.1.0 | MIT · [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT · [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools) |
| OpenCvSharp4 / OpenCvSharp4.runtime.win.slim | 4.13.0.20260627 | Apache-2.0 · Copyright 2008–2026 shimat and contributors · [source at packaged commit](https://github.com/shimat/opencvsharp/tree/b161e7e012f5101f6d5dc68a835c59db6cc88b18) |
| OpenCV (OpenCvSharp native runtime) | 4.13.0 | Apache-2.0 · [OpenCV source](https://github.com/opencv/opencv/tree/4.13.0) |
| ILGPU / ILGPU.Algorithms | 1.5.3 | University of Illinois/NCSA Open Source · Copyright (c) 2016–2025 ILGPU Project · [source](https://github.com/m4rs-mt/ILGPU/tree/v1.5.3) |
| OpenCvSharp4.official.runtime.linux-x64.slim | 4.13.0.20260627 | Apache-2.0 · [fixed source version](https://github.com/shimat/opencvsharp/tree/4.13.0.20260627) |
| LibTIFF (기본 파일 코덱) | 4.7.2 | LibTIFF 및 구성 파일 고지 · [원문](licenses/streaming-codecs/libtiff-LICENSE.md) |
| libpng (기본 파일 코덱) | 1.6.59 | PNG Reference Library License · [원문](licenses/streaming-codecs/libpng-LICENSE.txt) |
| libjpeg-turbo (기본 파일 코덱) | 3.2.0 | IJG / BSD 및 구성 파일 고지 · [라이선스](licenses/streaming-codecs/libjpeg-turbo-LICENSE.md), [IJG 원문](licenses/streaming-codecs/libjpeg-turbo-README.ijg) |
| libwebp (기본 WebP 코덱) | 1.6.0 | BSD-3-Clause · [COPYING](licenses/streaming-codecs/libwebp-COPYING.txt), [PATENTS](licenses/streaming-codecs/libwebp-PATENTS.txt) |
| zlib (기본 파일 코덱) | 1.3.2 | zlib License · [원문](licenses/streaming-codecs/zlib-LICENSE.txt) |

기본 파일 로더는 `VisionImageCodecs.dll` 안에 위 다섯 코덱 의존성을 정적으로 연결합니다. 외부 코덱 소스는 수정하지 않았으며 버전·원본 URL·SHA256과 빌드 옵션은 [고정 목록](experiments/VisionImaging.Codecs/native/dependencies.json), [빌드 스크립트](experiments/VisionImaging.Codecs/Build-Native.ps1)에 있습니다. 이 파일과 `licenses/streaming-codecs`의 전체 원문을 배포물에 유지합니다. This software is based in part on the work of the Independent JPEG Group. LibTIFF의 LZW 구현에는 University of California, Berkeley가 개발한 소프트웨어가 포함됩니다. Windows 빌드 도구 NASM 2.16.03은 빌드에만 사용하고 실행물에 포함하지 않습니다([NASM 고지](licenses/streaming-codecs/nasm-LICENSE.txt)). Windows native DLL은 MSVC/UCRT 런타임을 사용하며 .NET에 그 런타임이 포함된 것으로 가정하지 않습니다. Linux용 빌드 정의와 실제 Linux 실행 검증은 별개입니다.

2026-10-14부터 기본 앱·샘플·검사 CLI·테스트·비교 도구의 NetVips/libvips 패키지 참조를 제거했습니다. 과거 배포 ZIP과 저장소의 NetVips 관련 고지는 당시 배포 이력이며 새 배포에는 포함하지 않습니다. OpenCV를 사용하는 필터·템플릿 검사 의존성은 파일 코덱 선택과 별개입니다.

검사의 CUDA 후보 점수 계산과 별도 전처리 전용 API는 ILGPU를 사용합니다. 현재 앱/CLI는 방향 응답·정수 후보 점수를 CUDA에서 계산하고 전처리·연속 검증·원본 판정은 CPU에서 수행합니다. 원문 [ILGPU](licenses/ILGPU-LICENSE.txt), [ILGPU.Algorithms](licenses/ILGPU.Algorithms-LICENSE.txt), 각각의 [전이 고지](licenses/ILGPU-THIRD-PARTY.txt)와 [Algorithms 전이 고지](licenses/ILGPU.Algorithms-THIRD-PARTY.txt)를 함께 배포합니다. NVIDIA 드라이버는 별도 설치합니다. WPF 렌더링의 DirectX 의존성은 CLI 배포에 포함하지 않습니다. Linux x64 native 패키지는 CLI의 `RuntimeIdentifier` 또는 Linux 호스트에서 선택하며 Windows DLL과 함께 섞어서 배포하지 않습니다. Linux 실제 실행 여부는 [휴대 가능한 검사 검증](docs/portable-inspection-validation.md)을 따릅니다.

OpenCV/OpenCvSharp는 제공된 바이너리를 변경하지 않고 동적으로 로드합니다. 원문은 [OpenCvSharp license](licenses/OpenCvSharp-LICENSE.txt), [OpenCV license](licenses/OpenCV-LICENSE.txt)입니다. Windows x64 `OpenCvSharpExtern.dll`을 포함하며 Visual C++ 2022 x64 runtime이 필요합니다. 이 장비에서는 네이티브 필터 실행을 검증했습니다. slim 배포자의 빌드/모듈 구성은 [runtime README](https://github.com/shimat/opencvsharp)와 고정된 소스 commit을 따릅니다.

MIT 구성의 저작권 고지:

- Vortice: Copyright (c) Amer Koleci and Contributors.
- SharpGen: (c) 2010–2017 Alexandre Mutel, 2017–2023 Jeremy Koritzinsky, 2023–2024 Amer Koleci.

MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

테스트 도구(xUnit, Microsoft.NET.Test.Sdk 및 전이 의존성)는 앱 publish에 포함하지 않습니다. 이 문서는 앱 자체의 별도 오픈소스 라이선스를 지정하지 않습니다.

2026-10-04 사용 범위 추가: 동일 OpenCV/OpenCvSharp 패키지를 ROI 필터와 `VisionAlgorithm`의 CCOEFF_NORMED 템플릿 매칭에 사용합니다. Mat은 각 영상 처리 경계 안에 유지합니다. 논문 기반 C# 형상 매칭은 외부 소스의 복사/포팅이 아닌 독립 결합 구현이며 [논문 대조 기록](docs/decisions/0016-template-matching-and-pyramid-search.md)에 차이를 명시합니다.

## SkiaSharp 비교 실험 (2026-10-14)

`experiments/VisionImaging.Skia`가 SkiaSharp 및 NativeAssets 4.153.1을 참조한다. 선택형 UseSkiaImageLoader=true 앱 비교 빌드에도 연결하며 생산 앱의 기본 로더에는 연결하지 않는다. 패키지의 MIT 전문은 [SkiaSharp-LICENSE.txt](licenses/skia/SkiaSharp-LICENSE.txt), 네이티브 포함 구성요소 고지는 [Windows](licenses/skia/Win32-THIRD-PARTY-NOTICES.txt)와 [Linux](licenses/skia/Linux-THIRD-PARTY-NOTICES.txt)에 보관하고 실험 빌드/배포에 복사한다. Linux 패키지 포함과 실제 Linux 실행 검증은 구별한다. TIFF 경로의 기존 `licenses/streaming-codecs` 고지도 유지한다.

## 독립 AnomalyMonitor 영상 이상 감지 (2026-10-16)

새 앱은 `OpenCvSharp4` / `OpenCvSharp4.runtime.win`4.13.0.20260627의 full Windows x64 런타임을 사용한다. 기존 티칭 앱의 slim 참조는 변경하지 않는다. `video`/`videoio` 모듈로 optical flow와 영상 입력을 수행하며, `opencv_videoio_ffmpeg4130_64.dll`을 동적으로 로드한다. FFmpeg는 LGPL2.1-or-later이며 [배포 고지와 전문](licenses/FFmpeg-LICENSE.txt)을 함께 둔다. 출처와 빌드 설명은 [OpenCV4.13 FFmpeg 안내](https://github.com/opencv/opencv/blob/4.13.0/3rdparty/ffmpeg/readme.txt), [OpenCvSharp](https://github.com/shimat/opencvsharp), [OpenCV FFmpeg 빌드 소스](https://github.com/opencv/opencv_3rdparty/tree/ffmpeg)다. 네이티브 라이브러리는 변경하지 않았다.

CPU 추론에는 Microsoft.ML.OnnxRuntime1.24.4를 사용하며 [MIT 전문](licenses/onnxruntime/LICENSE.txt), [네이티브 포함 고지](licenses/onnxruntime/ThirdPartyNotices.txt), [관리 코드 포함 고지](licenses/onnxruntime/Managed-ThirdPartyNotices.txt)를 함께 복사한다.

특징 모델은 [Torchvision ResNet18 IMAGENET1K_V1](https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.resnet18.html)의 공개 사전 학습 가중치에서 layer2/3와 고정 채널 축소를 변환했다. [Torchvision BSD3 전문](licenses/Torchvision-LICENSE.txt), [변환 스크립트](scripts/Export-AnomalyFeatures.py), [변환 hash/출력 대조](src/VisionAnomaly/Models/conversion.json)를 보존한다. Torch/Python은 일회성 변환 도구이며 앱 실행 의존성이 아니다. ImageNet 데이터셋 자체는 포함하지 않는다. PatchCore는 알고리즘 참고이며 공식 저장소 소스를 복사하지 않았다. 입력 샘플 AVI는 이 저장소에서 자체 생성한 합성 영상이다.

## TorchSharp / LibTorch (PoseMatch C# 학습·추론)

TorchSharp 및 TorchSharp-cuda-windows0.107.0, LibTorch2.10.0 CUDA12.8을 사용한다. TorchSharp는 MIT이며 [전문](licenses/TorchSharp/LICENSE.txt)을 포함한다. LibTorch 및 포함 구성요소는 [배포 고지 전문](licenses/TorchSharp/LICENSE-LIBTORCH.txt)을 따른다. 출처는 https://github.com/dotnet/TorchSharp 와 공식 NuGet 패키지다. CUDA/cuDNN 네이티브 라이브러리는 패키지에 포함되며 NVIDIA 드라이버는 별도다. TorchSharp 전이 의존성에는 Google.Protobuf3.21.9, SharpZipLib1.4.0, SkiaSharp2.88.6이 포함된다. 공식 PoseMatch 모델의 기존 MIT 고지도 유지한다. C# 학습·추론 실행에는 Python이 필요하지 않다.

## dotnet-anti-slop (빌드 전용)

0.1.0-preview.1, MIT. 출처: https://github.com/devmobasa/dotnet-anti-slop, revision `6d4188203bbc6dfdee6159204b8b73b76e5ee4ff`. 저작권 및 전문은 [.dotnet-anti-slop/LICENSE](.dotnet-anti-slop/LICENSE)에 보관한다. 분석기는 netstandard2.0이며 Microsoft.CodeAnalysis.CSharp 4.8.0 및 Microsoft.CodeAnalysis.Analyzers 5.6.0을 private 빌드 참조로 사용한다. 앱 런타임/네이티브 배포 의존성은 추가하지 않는다. 선택 실행과 출처 메타데이터 주의사항은 [도입 기록](docs/dotnet-anti-slop-adoption.md)을 따른다.
