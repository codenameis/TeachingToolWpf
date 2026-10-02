# 실행 검증 · 2026-10-02

## ROI GPU 재사용·이동 preview 후속

Core 68 + Imaging/WPF 38 테스트와 Release 빌드(경고/오류 0)를 통과했다. 1만/32,940개 평면·PCB·완전 중첩의 CPU/GPU 전후 로그, 32,940개 작은 pan/큰 pan/zoom 각 3회, 10만/100만 전체 보기 탐색, 경계 이동과 실제 위치가 계속 바뀌는 다중 이동을 측정했다. [조건·수치·남은 지연](retained-roi-performance.md)을 따른다.

실제 RTX3060 / DPI150% / DX12 debug에서 새 instance pipeline, 이름표 준비 완료·캐시 제거, 32,940개 이동 확정/Undo/Redo/취소와 GPU 캡처를 확인했다. 최종 publish 폴더를 작업 디렉터리로 실행한 한글 태그·선택 핸들·이미지 교체·OpenCV·미니맵·8K 라이브·렌더 재연결 smoke도 종료 0, DX12 warning/error 없이 통과했다. 물리 포인터 전체 E2E와 혼합 DPI·장시간 부하는 별도 미검증이다.

## 대량 복제 프리징·미니맵 후속

Core 60개와 Imaging/WPF 32개 통과, Release 솔루션 빌드 경고/오류 0. 300개부터 마퀴·복사·붙여넣기를 반복해 평면/PCB 계층/완전 중첩 각각 19,200개까지 확인했다. 별도로 7,000개 필터 ROI 복제에서 발생하던 GPU descriptor 고갈을 수정하고 14,000개로 복제·Undo/Redo·렌더 캡처를 완료했다. 비동기 준비 중 UI timer와 지연 취소도 검증했다. 전체 완료 시간과 남은 UI 지연은 [실측 기록](clipboard-minimap-validation.md)을 따른다.

RTX 3060/DX12 debug smoke에서 8K/24K/라이브의 GPU 미니맵 표시, HWND 클릭·드래그, 캡처 상실 취소와 ROI·선택·이력 보존을 확인하고 캡처 화면을 검토했다. 아래의 과거 물리 포인터 검증과는 구분한다. 기존 혼합 DPI/실제 카메라/장시간 부하 미검증 범위는 유지한다.

## 대량 PCB ROI 성능 후속

Core 52개와 Imaging/WPF 29개가 통과했고 Release 솔루션 빌드는 경고/오류 0이다. 1,550/3,550개 전후 각 3회, 100만/200만 평면·PCB 계층의 전체 보기/확대 조작, 백만 개 전체 삭제·복원을 추가 측정했다. 수치·검증 범위·초기 구축과 일괄 작업의 남은 지연은 [대량 ROI 보고서](roi-scalability.md)를 따른다. 아래 8K 영상/파일 수치는 이전 측정이며 수백만 ROI를 함께 실행한 결과가 아니다.

최종 배포본의 RTX 3060/DX12 debug smoke에서 4,600개 ROI 목록의 페이지 밖 선택 해제, pan 시 페이지 유지, 속성 필드 포커스·한글 값 적용/Undo, 기존 편집·필터·8K 라이브·렌더 재시작을 확인했고 종료 코드 0이었다. 로그에 D3D12 경고/오류가 없었으며 앱 캡처의 한글 이름·선택 핸들·속성 표시도 확인했다. 물리 마우스 전체 E2E와 물리 IME 조합 검증은 이번 자동 smoke에 포함하지 않는다.

## ROI별 필터 등록·복제·반복 교체 후속

- Core 44개, Imaging 26개 자동 검증 통과. 필터 등록/해제 Undo, 재선택 자동 표시, ROI별 다른 임계값, 혼합 설정 해제, 자손 포함 복제·작은 이미지 보정·새 ID와 붙여넣기 Undo/Redo를 검사했다.
- 실제 RTX/DX12 debug smoke에서 Ctrl+C/Ctrl+V에 대응하는 ViewModel 키 명령, HWND Del 메시지, 연쇄 삭제/붙여넣기 Undo, 원본/미등록/등록 ROI 필터와 이미지 교체·라이브 갱신을 확인했다.
- 실제 WPF 필터 입력의 `2 → 20 → 200` 중간 문자열이 문서에 기록되지 않으며 바인딩 확정 뒤 한 번의 Undo로 기존 임계값이 복원되는지 검사했다. 포커스 상실 때 확정하는 바인딩도 단언했다.
- 같은 8K RGBA / 24K Gray16 파일을 수정 전 120회, 수정 후 120회 및 600회 교체했다. 오류 없이 완료했으며 마지막 600회 시험에서 Private Bytes 약 307.48 MiB, 관리 힙 22.89 MiB, 핸들 1295였다. 지속 누수 징후를 발견하지 못했다. [조건·범위](memory-replacement-validation.md)를 따른다.

새 기능은 실제 Windows/GPU 앱의 자동 smoke로 확인했다. 이번 변경의 전체 버튼/텍스트 단축키를 물리 입력으로 다시 수행한 E2E 시험은 하지 않았다. 아래 실제 포인터 관찰은 이전 변경 시점의 기록이며, 현재 필터 선택 동작은 [ADR-005](decisions/0005-filter-registration-and-roi-copy.md)를 따른다.

## 후속 수정과 실제 포인터 검증

기존 `STATIC` 자식 창은 `SS_NOTIFY` 없이 `WM_NCHITTEST`에 HTTRANSPARENT를 반환해 실제 포인터를 받지 못했다. 직접 SendMessage를 보내는 이전 smoke만으로 이 문제를 발견하지 못했다. 전용 Win32 자식 창으로 교체했고 HTCLIENT 회귀 검사를 추가했다. 이전 기록의 “native input”은 직접 메시지 전달 시험이며 실제 마우스 E2E와 구분한다.

2026-10-02 Computer Use의 실제 클릭/드래그로 다음을 확인했다.

- ROI 추가 버튼: 즉시 생성, 파란 얇은 선택 윤곽·흰 resize 핸들·원형 회전 핸들 표시.
- 회전 핸들 드래그: 약 75.5°로 회전하고 ROI 안 Binarize 영상도 같은 형상으로 갱신.
- 이미지 오른쪽/아래 바깥까지 드래그: 경계에서 잘린 ROI만 생성. 모두 밖인 드래그는 Core 회귀 검사로 무생성 확인.
- Binarize 선택만으로 원본 유지, 미리보기 클릭 뒤 ROI 내부만 이진화.
- ROI 목록에서 다른 ROI 선택: 이전 영역은 원본, 새 선택에 필터 자동 적용. Edge 선택 시 Canny 결과로 자동 변경.
- 8K → 24K 샘플 버튼: ROI 2개에서 0개, 필터 결과 제거. Undo 버튼으로 8K 이미지와 ROI 2개 및 선택 복원.

OpenCV 테스트는 Gray8/Gray16/RGBA/BGRA의 입력 불변성·이진화·Canny, 회전 90°의 실제 픽셀 위치, 프레임 lease 유지, 24K 회전 영역의 제한된 출력, 선택 경쟁·취소 후 늦은 결과 폐기를 검사한다. 당시 전체 테스트는 Core 36 + Imaging 22 = 58개였다. 화면에서 관찰한 정적 ROI 처리 시간 약 16–38ms는 개별 관찰값이며 성능 보장이나 정식 반복 벤치마크가 아니다.

`FilterSmokeScenario`는 다중 선택 필터, 소스 교체 초기화/Undo, 7680×4320 라이브 경계와 ROI 초기화, 라이브 필터 갱신을 추가 확인한다. `filter-smoke.png`는 앱 자체 DX12 readback 결과다. 최근 ROI/미리보기 변경으로 기존 8K 성능 수치를 재측정한 것으로 표현하지 않는다.

## 완료한 자동 검증

| 검증 | 결과 / 범위 |
|---|---|
| Release 솔루션 빌드 | 경고 0, 오류 0 |
| Core xUnit | 44 passed / 0 failed / 0 skipped |
| Imaging xUnit | 26 passed / 0 failed / 0 skipped |
| 실제 WPF/DX12 smoke | 종료 코드 0, RTX 3060 Laptop, debug layer 활성 |
| 최종 publish 폴더 실행 | 동일 smoke 종료 코드 0; 네이티브 DLL/셰이더 로딩 확인 |
| DX12 진단 | smoke 로그에 warning/error/corruption 메시지 없음 |
| 8K 반복 부하 | Gray8/Gray16/BGRA32, ROI 500, 각 3회, 수치/드롭 별도 기록 |
| 대용량 완료 파일 | 24576×131072 Gray8/Gray16 BigTIFF의 import, 마지막 타일 값, GPU 표시 |

Core 테스트는 회전 포함/큰 좌표 왕복/zoom 앵커/잘못된 형상, 선택·그리기·resize·회전·취소, 부모 중복 변형 방지/연쇄 삭제/그룹 해제, 이력 원자성/예산/Redo 분기, BVH와 brute force의 동일 결과, 프레임 lease·latest mailbox·재연결을 검증한다.

영상 테스트는 실제 24576×513 Gray16 TIFF의 마지막 행과 LOD 픽셀, 24K 폭 PNG/JPEG의 RGBA 채널/가장자리, 8K WebP, 취소된 import의 manifest 미게시, 실패한 이미지 Undo의 문서/이력 유지, 4개 형식의 AVX2/scalar area average 동등성 및 원본 좌표 보존을 검증한다. 처음 추가한 24K WebP fixture 생성은 형식의 최대 크기 제한 때문에 실패했다. 공식 한계에 맞춰 WebP는 8K로 시험하며 24K 지원으로 표시하지 않는다. [공식 한계](https://developers.google.com/speed/webp/faq)

## 실제 앱 smoke의 의미

`Diagnostics/EditingSmokeScenario.cs`는 자신의 HWND에 Win32 마우스 메시지를 전달하고 툴바에 연결된 실제 명령을 실행한다. ROI 2개 생성 → 다중 선택 → 새 부모 생성 → 속성 각도 37° → 0° → Undo/Redo → 부모 연쇄 삭제/복원 → 그룹 해제/복원 → Escape 취소를 검사한다. GPU 재연결 뒤 편집 이력을 유지하는지 확인한다.

`RenderSmoke`는 한글 부모/자식 ROI, GPU back buffer 캡처, 8K 라이브 시작/정지, 최소화/복원과 정지 프레임에서 렌더러 재생성을 실행했다. 마지막 두 창 수명 단계는 종료/오류 로그를 확인한 smoke이며 프레임별 물리 표시를 단언한 테스트는 아니다. 드라이버 device-removal을 강제로 일으킨 시험은 하지 않았다.

결과는 `artifacts/smoke.log`, `viewport-smoke.png`, `live-smoke.png`, `app-smoke.png`다. 앱 캡처는 WPF RenderTargetBitmap과 자체 DX12 back buffer readback을 결합한 이미지이며 데스크톱 전체 캡처가 아니다. 이 시험은 파일 대화상자/실제 사용자의 클릭을 모두 자동화한 end-to-end UI 테스트로 표현하지 않는다.

## 미검증 및 알려진 제한

- 실제 100/200% 및 혼합 DPI 모니터 이동, 드래그 중 DPI 변경, 실제 GPU 제거·복구, 원격 데스크톱.
- 10분 연속 soak, 열/배터리 변화, 물리 입력→scanout 계측, 카메라 SDK의 실제 프레임/stride/처리량.
- 8192×8192 실시간 및 10,000 ROI의 정식 GPU 편집 벤치마크. Core 10,000 ROI 계층 측정은 별도로 존재한다.
- 다중 ROI 일괄 resize/혼합 속성 편집, 레시피 저장·불러오기, 도형 확장, 원본 해상도 검사 알고리즘. Binarize/Canny 시각적 미리보기는 구현/검증했다.
- 색/압축/멀티페이지 TIFF의 모든 조합, Gray16 window/level, BMP/RAW. 컬러는 RGBA8로 정규화한다.
- 타일 gutter와 분수 배율의 미세 seam, 디스크 캐시의 자동 용량 제한·중단 partial 폴더 자동 정리.

실패한 소스 읽기는 현재 정상 문서/이력을 유지한다. GPU 초기화/실행 오류는 상태줄에 표시하고 재연결 버튼을 제공한다. 네이티브 파일 캐시와 GPU/CPU 버퍼 예산을 프로세스 전체 RAM 상한으로 주장하지 않는다. 실측/환경/백분위는 [성능 기록](performance-baseline.md)을 따른다.
