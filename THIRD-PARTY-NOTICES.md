# Third-party dependencies

이 샘플은 아래 패키지와 네이티브 라이브러리를 사용합니다. 패키지 nuspec 및 배포자가 제공한 고지를 기준으로 기록했습니다. `licenses/`와 네이티브 라이브러리를 배포 폴더에 함께 유지합니다. WPF 앱은 .NET 10 Desktop Runtime, CLI는 .NET 10 Runtime을 별도 설치하며 framework-dependent 배포에 번들하지 않습니다.

| Runtime component | Version | License / source |
|---|---|---|
| Vortice.Direct3D12, DXGI, D3DCompiler, DirectX | 3.8.3 | MIT · [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) |
| Vortice.Mathematics | 2.1.0 | MIT · [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT · [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools) |
| NetVips | 3.2.0 | MIT · [net-vips](https://github.com/kleisauke/net-vips) |
| NetVips.Native.win-x64 | 8.18.6 | LGPL-3.0-or-later plus bundled dependencies · [packaging/source recipes](https://github.com/kleisauke/libvips-packaging), [libvips source](https://github.com/libvips/libvips) |
| OpenCvSharp4 / OpenCvSharp4.runtime.win.slim | 4.13.0.20260627 | Apache-2.0 · Copyright 2008–2026 shimat and contributors · [source at packaged commit](https://github.com/shimat/opencvsharp/tree/b161e7e012f5101f6d5dc68a835c59db6cc88b18) |
| OpenCV (OpenCvSharp native runtime) | 4.13.0 | Apache-2.0 · [OpenCV source](https://github.com/opencv/opencv/tree/4.13.0) |
| ILGPU / ILGPU.Algorithms | 1.5.3 | University of Illinois/NCSA Open Source · Copyright (c) 2016–2025 ILGPU Project · [source](https://github.com/m4rs-mt/ILGPU/tree/v1.5.3) |
| NetVips.Native.linux-x64 | 8.18.6 | LGPL-3.0-or-later plus bundled dependencies · [packaging/source recipes](https://github.com/kleisauke/libvips-packaging) |
| OpenCvSharp4.official.runtime.linux-x64.slim | 4.13.0.20260627 | Apache-2.0 · [fixed source version](https://github.com/shimat/opencvsharp/tree/4.13.0.20260627) |

검사의 CUDA 전처리는 ILGPU를 사용합니다. 원문 [ILGPU](licenses/ILGPU-LICENSE.txt), [ILGPU.Algorithms](licenses/ILGPU.Algorithms-LICENSE.txt), 각각의 [전이 고지](licenses/ILGPU-THIRD-PARTY.txt)와 [Algorithms 전이 고지](licenses/ILGPU.Algorithms-THIRD-PARTY.txt)를 함께 배포합니다. NVIDIA 드라이버는 별도 설치합니다. WPF 렌더링의 DirectX 의존성은 CLI 배포에 포함하지 않습니다. Linux x64 native 패키지는 CLI의 `RuntimeIdentifier` 또는 Linux 호스트에서 선택하며 Windows DLL과 함께 섞어서 배포하지 않습니다. Linux 실제 실행 여부는 [휴대 가능한 검사 검증](docs/portable-inspection-validation.md)을 따릅니다.

OpenCV/OpenCvSharp는 제공된 바이너리를 변경하지 않고 동적으로 로드합니다. 원문은 [OpenCvSharp license](licenses/OpenCvSharp-LICENSE.txt), [OpenCV license](licenses/OpenCV-LICENSE.txt)입니다. Windows x64 `OpenCvSharpExtern.dll`을 포함하며 Visual C++ 2022 x64 runtime이 필요합니다. 이 장비에서는 네이티브 필터 실행을 검증했습니다. slim 배포자의 빌드/모듈 구성은 [runtime README](https://github.com/shimat/opencvsharp)와 고정된 소스 commit을 따릅니다.

네이티브 패키지는 `libvips-42.dll`을 동적으로 로드하며 라이브러리를 샘플 실행 파일 안에 정적으로 결합하지 않습니다. 바이너리는 변경하지 않았습니다. 정확한 내장 라이브러리 버전은 [versions](licenses/NetVips.Native-versions.json), 원래 배포자의 구성별 고지는 [native notices](licenses/NetVips.Native-THIRD-PARTY-NOTICES.md)에 있습니다. LGPL/GPL 원문도 포함합니다. 네이티브 패키지의 소스/빌드 자료는 위 packaging 프로젝트와 각 구성 라이브러리의 버전을 따릅니다.

Linux는 `libvips.so.42`를 동적으로 로드합니다. 플랫폼별 내장 구성이 다를 수 있으므로 Linux 패키지의 [구성 버전](licenses/NetVips.Native.linux-x64-versions.json)과 [배포자 고지](licenses/NetVips.Native.linux-x64-THIRD-PARTY-NOTICES.md)를 별도로 포함합니다.

MIT 구성의 저작권 고지:

- Vortice: Copyright (c) Amer Koleci and Contributors.
- SharpGen: (c) 2010–2017 Alexandre Mutel, 2017–2023 Jeremy Koritzinsky, 2023–2024 Amer Koleci.
- NetVips: Copyright (c) Kleis Auke Wolthuizen.

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
