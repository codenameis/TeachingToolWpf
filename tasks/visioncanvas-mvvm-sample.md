# 진단 없는 VisionCanvas WPF 샘플

사용자 요청: 별도 프로젝트에서 라이브러리 소비 난이도를 검증한다. 로컬 수정·검증만 진행한다.

- `samples/VisionCanvas.MvvmSample`: WPF .NET 10 x64, App에서 명시적으로 조립한다.
- XAML은 명령/Scene/Input/Layout을 바인딩한다. View는 포커스·키 전달·UI 이벤트 수명만 담당한다.
- VM은 파일 창·Dx12Viewport·Dispatcher·구체 디코더를 참조하지 않는다. 도메인 규칙은 EditorSession/InteractionController를 사용한다.
- 이미지 열기/8K 예제, 맞춤/확대/축소/이동, 사각형/원/다각형·부모 그리기, 그룹화/해제/삭제, 복사/붙여넣기와 통합 Undo/Redo를 제공한다.
- 검사·필터 패널·대량 성능 시험·진단 실행 분기를 추가하지 않는다. 라이브러리 내부 렌더 진단을 제거하는 작업은 아니다.
- ImageCoordinator의 이미지 소유권·실패 원자성·이력 복원을 Canvas로 이동하고 기존 앱도 동일 구현을 사용한다.
- 성공 기준: 새 프로젝트 단독 Release 빌드, UI 없는 VM 동작/실패·취소 테스트, 실제 WPF 창의 GPU/WARP 표시·입력·종료 검증, 기존 이미지 이력/Canvas 회귀 통과.
- 엄격한 60FPS·실시간 카메라·현장 영상 정확도를 이번 기능 시험의 결과로 주장하지 않는다.

검증 명령:
```powershell
dotnet build samples/VisionCanvas.MvvmSample/VisionCanvas.MvvmSample.csproj -c Release
dotnet test tests/VisionCanvas.MvvmSample.Tests/VisionCanvas.MvvmSample.Tests.csproj -c Release
dotnet test tests/TeachingToolWpf.Imaging.Tests/TeachingToolWpf.Imaging.Tests.csproj -c Release --filter FullyQualifiedName~ImageHistoryTests
dotnet run --project samples/VisionCanvas.MvvmSample/VisionCanvas.MvvmSample.csproj -c Release
```

작업 순서: 이미지 수명 공통화·회귀 → 샘플 VM/명령·행동 검증 → XAML/조립·실제 실행 → 설계 검토와 사용법.

## 2카메라 모델 로딩 후속

- 내장 예제: 카메라마다8192×8192 합성 이미지, 그룹2개+리프 ROI5개, 리프마다 알고리즘 이름/설정 연결.
- 공유 EditorView 두 개와 독립 문서/이력/입력, ROI 목록과 Canvas 단일 선택 동기화.
- Core 문서 준비 API와 공통 이미지 Coordinator에서 검증·읽기 실패·취소·재로딩 Undo/Redo 계약 유지.
- 실제 알고리즘 실행·외부 모델 파일 입출력·카메라 SDK는 추가하지 않는다. 초보자용 한국어 주석 유지.
- 수용 기준: 두 네이티브 화면의 GPU/WARP 픽셀,14개 ROI/그룹·10개 설정, 카메라별 삭제/Undo, 바인딩 오류 없음, 기존 Core/Canvas/앱 회귀. 결과는 [검증 문서](../docs/visioncanvas-mvvm-sample.md)에 기록한다.
