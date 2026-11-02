# 렌더러별 코드 교육 HTML

2026-10-16. 사용자 요청에 따라 실제 코드를 따라 읽는 다섯 안내서를 각 `manual/<분류>` 폴더에 추가했다. 후속 요청으로 C# 기초를 배운 고등학생 수준에 맞춰 첫 등장 용어, 작은 예와 실행 순서 설명을 보강했다. 실제 코드·수식·조작 모형은 보존하며 [난이도 조정 검증](beginner-manual-validation.md)을 따른다. 로컬 문서 수정·검증이며 커밋·푸시는 하지 않는다.

| 교육서 | 중심 내용 | 실제 프로젝트 |
|---|---|---|
| [WPF](../manual/wpf/index.html) | App·MVVM·바인딩·두 카메라·메인 검사 연결 | TeachingToolWpf / VisionCanvas.MvvmSample |
| [DX12](../manual/dx12/index.html) | 장면·HLSL·command list·fence·업로드 | VisionCanvas / VisionCanvas.Sample |
| [Vulkan](../manual/vulkan/index.html) | swapchain·acquire·render pass·동기화·SPIR-V | VisionCanvas.Vulkan / VulkanSample |
| [wgpu](../manual/wgpu/index.html) | native WebGPU·DX12 backend·WGSL·제출 슬롯 | VisionCanvas.Wgpu / WgpuSample |
| [Skia](../manual/skia/index.html) | CPU bitmap·Gray16 표시·경로/버퍼 재사용 | VisionCanvas.Skia / SkiaSample |

각 편은 공통 코드 지도, 원본 px/DIP/DPI와 맞춤 계산, ROI 미리보기·확정·Undo, 계층·마스크·압축 반복셀, 방향키/잠금/목록 탐색, 타일/최신 프레임, 전용 렌더러와 자원 수명, 확장 위치, 실행 명령, 실습과 풀이를 포함한다. WPF를 독립 GPU 백엔드로 설명하지 않는다. 검사 알고리즘 수식은 기존 종합 실습서와 알고리즘 문서로 연결한다.

## 사용과 관리

해당 `index.html`을 브라우저에서 열면 된다. HTML 본문과 실제 소스 발췌는 파일 안에 저장되어 있으며, 실행 중 C# 파일을 읽거나 네트워크에서 내용을 가져오지 않는다. 공통 CSS/JavaScript는 `manual/assets`에 있으므로 오프라인 이동 시 **manual 전체**를 복사한다. 원본 소스/검증 문서 링크는 저장소 구조를 유지해야 한다. 외부 라이브러리·CDN·폰트·패키지를 추가하지 않았다.

본문은 각 HTML을 직접 수정한다. 실제 코드창은 `data-source`에 HTML 기준 상대 경로, `data-start`/`data-end`에 1부터 시작하는 포함 행 범위를 기록한다. 원본이 바뀌면 설명을 검토하고 HTML 이스케이프한 연속 발췌와 행 정보를 같이 갱신한다. 원본 전체를 복사해 코드창을 불필요하게 늘리지 않는다. 발췌는 완성된 새 앱이 아니므로 주변 namespace/using/클래스가 생략될 수 있다.

화면은 기존 `manual.css`와 `code-blocks.js`를 재사용하고 새 페이지에만 `renderer-guides.css`/`renderer-guides.js`를 적용한다. 코드창·풀이 접기, 복사 및 복사 실패 시 수동 선택, 인쇄 전 코드 펼침/인쇄 후 복원, 좁은 화면의 목차/표 스크롤을 제공한다. JavaScript가 없어도 본문과 실제 코드·풀이를 읽을 수 있다.

교육용 SVG 모형은 선택 A/탐색 B, 원본 1px·Shift10px 이동, 맞춤/전체 보기, Undo, 입력 잠금과 수동 프레임 게시를 설명한다. 키 처리는 SVG에만 붙여 목록/텍스트의 방향키를 가로채지 않는다. **실제 Core·WPF·GPU를 호출하지 않는다.** 회전·부모·마스크·드래그와 완전한 편집 이력은 모형에서 생략했으며 실제 제품의 전체 동작 검증으로 해석하지 않는다.

## 리팩토링 판단

수업 대상의 `EditorSession`, `ImageCoordinator`, `TilePlanner`, Skia의 단계별 메서드와 백엔드 경계를 읽고, 제품 C#의 실행 순서·연산·버퍼·스레드·공개 API는 이번 작업에서 변경하지 않았다. 코드 의미를 한 줄씩 쪼갠 새 추상화나 매 프레임 복사를 추가하지 않고, 이미 존재하는 단계·소유권·선행 조건을 설명했다. 긴 GPU 루프는 단계별 발췌로 읽되 자원 생존 범위는 유지한다.

따라서 이번 작업으로 인한 제품 성능 향상이나 새 벤치마크 수치를 주장하지 않는다. HTML의 실험 코드는 교육 자료에만 포함되며 제품 프로젝트에서 참조하지 않는다. 기존 Skia 성능 수치는 원래 측정 조건과 제한을 명시해 소개했다.

## 검증

| 항목 | 결과 | 범위 |
|---|---|---|
| 실제 소스 발췌 | PASS | 5페이지의 93곳을 원본 연속 행과 대조; HTML 엔티티 복원 후 문자 일치 |
| 내부 링크·구조 | PASS | 187개 링크/자산 경로와 HTML 앵커, 중복 ID·제목 단계·접근성 참조·태그 닫힘 |
| 학습 모형 | PASS | 5페이지 DOM 대역에서 좌표·1/10px·목록/선택·잠금·새 프레임·Undo·경계·다시 맞춤·초기화 |
| 인쇄 상태 전이 | PASS | DOM 대역에서 접힌 항목 펼침과 기존 열림 상태 복원; 실제 출력 레이아웃은 제외 |
| 잠금 변이 실험 | PASS | 메모리 안에서 이동 잠금 guard를 제거하자 잠금 중 X와 Undo 보존 검사가 실패; 원본 파일은 수정하지 않음 |
| JavaScript 구문·diff 공백 | PASS | Node 구문 검사와 git diff --check |
| 실제 브라우저·모바일·인쇄/PDF·보조공학 | NOT_RUN | 아래 도구 정책 제한 |

재현은 저장소 루트에서 Python 3으로 `scripts/verify-renderer-guides.py`, Node로 `scripts/test-renderer-guide-lab.mjs`를 실행한다. 외부 패키지는 필요 없다. `node scripts/test-renderer-guide-lab.mjs --mutation`은 의도적으로 실패해야 하는 실험이다. 결과 파일은 로컬 `artifacts/renderer-guides-static.json`, `artifacts/renderer-guides-lab.json`에 보관했다.

첫 모형 시험에서 B의 맞춤 배율 기대값을 1.656으로 잘못 적어 실패했다. 600×360 화면과 200×160 영역으로 재계산하면 `min(3, 2.25) × .92 = 2.07`이며, 구현의 값이 맞아 시험 기대값을 수정했다. 실제 제품 오류나 성능 기준 완화가 아니다.

실제 브라우저의 `file://` 열기는 도구 URL 보안 정책이 거부했다. 다른 프로토콜·브라우저·우회 실행으로 대체하지 않았다. 따라서 실제 브라우저 시각 배치, 모바일 화면, 인쇄/PDF 및 보조공학 검증은 **NOT_RUN**이다. DOM 대역은 입력 이벤트 전달과 상태 전이를 확인하며 브라우저의 실제 포커스·기본 키 동작·레이아웃을 실행하지 않는다.

제품 코드를 바꾸지 않아 .NET/GPU 회귀 시험과 성능 벤치마크를 재실행하지 않았다. 기존 [공통 입력 검증](canvas-interaction-modes.md)과 각 백엔드 검증 결과는 과거 기록으로 연결했으며 이번 HTML 시험의 결과와 구별한다. 실제 독자 대상 이해도 평가·지속 FPS·장시간 메모리·현장 정확도를 새로 검증한 것은 아니다.
