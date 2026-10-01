# WPF / DX12 표시 경로 확인

2026-10-02. `HwndHost`의 자식 HWND에 DXGI flip-discard swap chain을 붙여 RTX 3060 Laptop에서 실행했다. WPF 영역에는 툴바/목록/속성만 두고, 이미지·ROI·핸들·태그는 같은 DX12 pass의 삼각형 배치로 그린다.

장치: NVIDIA GeForce RTX 3060 Laptop GPU, 실제 DXGI dedicated 5.9 GiB / local budget 5.1 GiB. 드라이버 32.0.15.8183. .NET SDK 10.0.401, net10.0-windows x64, Vortice 3.8.3. FeatureLevel 11_0 이상의 D3D12 장치 생성을 검사한다. NVIDIA 우선 선택 후 지원하는 다른 hardware adapter를 시도하며 실제 선택 이름을 상태 표시줄에 보여준다. WARP fallback은 하지 않는다.

2개 back buffer, allocator/업로드 arena도 2개, 하나의 direct queue를 사용한다. `GetCopyableFootprints`를 따라 업로드하고 frame fence가 끝난 슬롯만 재사용한다. 삭제 texture/descriptor는 해당 frame 완료까지 지연 해제한다. UI의 편집 상태와 GPU 수명은 별도다.

FXC 바인딩으로 배포된 `Shaders/Viewport.hlsl`을 컴파일한다. shader 파일 없이 exe만 복사하지 않는다. Native libvips DLL과 라이선스도 publish 폴더에 포함한다. GPU 오류는 상태줄로 표시하며 **GPU 재연결** 버튼으로 렌더러를 다시 만든다. 실제 device-removal 오류 주입은 아직 검증하지 않았다.

DX12 debug layer 실행, 실제 HWND 포인터 입력, 편집 명령, 렌더러 재시작과 GPU back buffer 캡처를 확인했다. `artifacts/smoke.log`에 실제 장치와 debug 상태가 남는다. [런타임 검증](runtime-validation.md)과 [성능 보고서](performance-baseline.md)를 참조한다.

DX12를 채택한 이유는 현재 Windows/WPF 호스트와 맞는 명시적 표시/진단 경로다. Vulkan이 느리다는 비교 결과는 없으며 두 렌더러를 동시에 유지하지 않는다. 기술 근거는 [Microsoft swap chains](https://learn.microsoft.com/en-us/windows/win32/direct3d12/swap-chains), [WPF/Win32 interoperation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-win32-interoperation), [Vortice](https://github.com/amerkoleci/Vortice.Windows)다.
