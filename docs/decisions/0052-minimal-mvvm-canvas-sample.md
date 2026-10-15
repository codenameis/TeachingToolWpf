# ADR-052: 진단 없는 WPF MVVM 소비 샘플

- 날짜: 2026-10-15
- 상태: 사용자 요청에 따른 구현. 로컬 수정·검증만 승인.

## 배경

사용자는 새 WPF 프로젝트에서 VisionCanvas를 얼마나 쉽게 재사용할 수 있는지 확인하고, MVVM·SOLID·GRASP·클린 코드·DRY·YAGNI를 지키는 최소 앱을 요청했다. 필요한 라이브러리 수정도 허용했다. 기존 독립 샘플은 창 안에 UI 구성·명령·이미지 이력·진단이 혼재한다.

## 결정

1. `samples/VisionCanvas.MvvmSample`을 추가한다. 기존 앱·기존 샘플의 소스를 링크하거나 프로젝트를 참조하지 않는다. 직접 참조는 VisionCanvas와 파일 호스트 VisionImaging 두 개다.
2. Model은 기존 Core의 DocumentState/Roi/EditorSession을 사용한다. 별도 모델·기하·계층·Undo/Redo 구현을 복제하지 않는다.
3. App이 객체와 수명을 조립한다. VM은 상태/명령, XAML은 바인딩, View는 포커스/키/Dispatcher/이벤트 해제를 소유한다. View의 UI 책임을 VM으로 옮겨 코드 비하인드를 0줄로 만드는 것을 목표로 삼지 않는다.
4. 기존 앱의 ImageCoordinator를 `VisionCanvas.Presentation`으로 이동한다. 앱 ObservableObject 상속만 자체 INotifyPropertyChanged로 교체하고 기존 소유권·취소·이력·준비 callback을 보존한다. 두 앱이 같은 구현을 사용하며 Canvas가 디코더나 앱을 참조하지 않는다.
5. 검사·필터 패널·성능 진단·라이브 소스를 샘플에 추가하지 않는다. 테스트와 GPU/WARP 실행 검증은 별도 테스트 프로젝트에 둔다. Canvas 내부 진단/오류 보고는 유지한다.

## 결과와 한계

- 호스트가 소스 재개방·실패 원자성·이력 cursor 처리를 다시 작성할 필요가 없어졌다.
- 같은 API로 UI 없는 테스트와 실제 WPF 창을 실행한다. 명령 상태와 단축키 동작은 VM 한 곳에서 결정한다.
- 새 외부 패키지, 서비스 로케이터, DI 프레임워크, 새로운 Core 추상화가 없다.
- 기존 VisionCanvas의 필터 파이프라인/OpenCvSharp 전이 참조는 남는다. 렌더러만의 NuGet 패키지 최소화는 별도 과제다.
- 이번 정적8K/24K 기능 검증은 실시간8K FPS나 대량 ROI60FPS 달성을 뜻하지 않는다.
- 원칙 예외는 적용하지 않는다. [원칙별 검토·검증](../visioncanvas-mvvm-sample.md)을 따른다.

## 후속: 두 카메라의 예제 모델 로딩

사용자가 카메라2대의 이미지·ROI·그룹과 ROI별 임의 알고리즘을 가진 모델 로딩을 요청했다. 이번 구현은 내장 예제와 알고리즘 설정 표시로 범위를 잡았다. 실제 검사 실행이나 외부 모델 파일 형식은 추가하지 않는다.

- `SampleInspectionModel`은 카메라 정보와 ROI ID별 알고리즘 설정을 소유한다. ROI/기하는 Core 타입을 재사용하며 알고리즘 의존성을 Canvas에 넣지 않는다.
- 동일한 `EditorView`를 두 번 배치하고 App이 별도 EditorSession/Input/Layout/ImageCoordinator/VM을 조립한다. 단일 `EditorWindow`도 같은 View를 재사용한다. 두 카메라의 이력과 오류는 독립적이다.
- 기존 API는 개별 ROI 생성만 제공해 모델을 로딩하면 생성·이름 변경 이력이 쌓였다. `PrepareDocumentAsync`가 일반 ROI 계층을 검증·준비하고 기존 `CommitPrepared`로 한 번에 게시하게 한다. 첫 로딩은 이력 없는 시작 상태, 재로딩은 이미지·ROI·맞춤 배율을 함께 되돌리는 한 편집이다.
- `ImageCoordinator.LoadDocumentAsync`가 모델 검증→원본 준비→크기/오래된 준비 결과/이력 예산 확인→소유권 이전→문서 게시 순서를 담당한다. 크기 불일치나 읽기 실패를 임의 재배치로 숨기지 않는다.
- `DemoImageLoader`는 두 합성 소스의 이름과 색상을 구별하고 기존 파일 로더에 나머지를 위임한다. 전체8K 픽셀 배열이나 카메라 SDK를 추가하지 않는다.
- 모델의 알고리즘 연결은 불변이며 같은 예제를 재사용한다. 삭제/이미지 교체 Undo로 ROI ID가 돌아오면 설정이 다시 보인다. 새 ROI/붙여넣기는 미지정이며 실행·설정 편집·모델 저장은 범위 밖이다.
- 두 카메라 전체를 묶은 원자적 로딩·통합 이력은 제공하지 않는다. 한 카메라가 실패하면 그쪽 이전 문서를 유지하고 오류를 표시한다. 원칙 완화나 새로운 패키지는 없다.
