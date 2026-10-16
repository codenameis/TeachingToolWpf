# ken32.png 첫 로딩 실측과 개선

2026-10-16. [ADR-060](decisions/0060-bit-exact-first-image-load.md). 로컬 수정·검증만 진행했으며 커밋·푸시는 하지 않았다.

같은 날 사용자 요청으로 `artifacts/ken32-first-load`를 삭제했다. 아래 수치와 검증 요약은 측정 당시 기록이며, 해당 경로의 바이너리·원시 보고서·캡처·검증 캐시는 현재 남아 있지 않다. 원본 `ken32.png`와 기존 앱 사용자 캐시는 보존했다.

## 입력과 측정 범위

- AMD Ryzen5 5600H, RAM32GB, Windows x64, .NET10.0.13, 논리 CPU12개, AVX2 지원. WPF 표시는 NVIDIA GeForce RTX3060 Laptop GPU, VRAM5.9GiB, budget5.1GiB.
- `ken32.png`: 169,119,065바이트,17280×30720, RGBA8, 비인터레이스. 저장소 사본과 사용자가 열었던 `Documents/ken32.png`의 SHA256은 모두 `3E482155A9A0C86F5E85821B62D8CD2CBF704B89747C4FCCADF40C0EE21C156A`다.
- 원본 raw2,123,366,400바이트, 원본+축소6단계2,830,982,400바이트. 압축 파일161MiB와 원본 약1.98GiB를 혼동하지 않는다.
- 매번 새 프로세스와 새 **애플리케이션 캐시 폴더**를 사용한다. 전후3회씩 순서는 이전→개선, 개선→이전, 이전→개선이다. 해시 계산은 측정 시간 밖이다.
- **OS 파일 캐시는 비우지 않았고 재부팅하지 않았다.** 실제 냉간 디스크/백신 최초 검사/다른 PC 결과가 아니다. 기존 두 앱의 사용자 캐시는 건드리지 않았다.
- 측정 당시 기존 바이너리는 `artifacts/ken32-first-load/before/streaming/`, 공통 측정 호스트의 전후 바이너리는 `baseline/streaming/`와 `improved/streaming/`를 사용했다. 네이티브 코덱 DLL은 전후 SHA256 `7F4BA5D2C849BB7728360E66F4979300BBB29DF2BE0A9F4F2A4A649CA01`로 같다.
- 비교한 Pyramid DLL은 이전 `4CFB3EF19A56D9BA799A1908070ED39D7B12301C229C35054D8EDE0E54864D88`, 개선 `E80B4522FCA7C640789F1D8F4CE4CD9CD899F2BD0AABF5ED766175FB59294F8B`다. 측정 이후 C# 형식 정리만 적용했다.

## 단계별 결과

첫 탐색 측정은 전체7.264초, PNG 디코딩4.962초, 원본 쓰기/준비0.623초, 축소/게시1.675초였다. 가장 큰 병목은 PNG 디코딩이고, 두 번째는 축소 피라미드 준비였다.

정식3회씩의 중앙값. 단계 중앙값의 합은 전체 중앙값과 정확히 같지 않을 수 있다.

| 단계 | 이전(ms) | 개선(ms) |
|---|---:|---:|
| 코덱 열기/헤더 | 3.4 | 3.5 |
| 원본 PNG ReadRows | 5071.8 | 5059.9 |
| 원본 쓰기 + 캐시 잠금/사전 검증/버퍼 준비 등 | 637.0 | 625.7 |
| 축소1 | 1239.7 | 886.9 |
| 축소2 | 287.1 | 145.8 |
| 축소3 | 75.2 | 43.4 |
| 축소4 | 20.5 | 12.7 |
| 축소5 | 6.5 | 4.8 |
| 축소6 + manifest 게시/소스 닫기 | 25.5 | 33.0 |
| 축소 단계 합계 | **1659.2** | **1126.8** |
| 파일 준비 전체 | **7409.9** | **6831.2** |
| 준비 후 같은 프로세스에서 캐시 재열기 | 9.7 | 9.9 |
| 프로세스 CPU 시간 합계 | 13484.4 | 8765.6 |
| 대여 배열 기준 준비 버퍼 피크 | 21MiB | 21MiB |

파일 준비 중앙값 **7.8%**, 축소 단계 중앙값 **32.1%**, CPU 시간 중앙값 **35.0%** 감소. CPU 시간은 여러 스레드 합계이며 벽시계 시간과 다르다. 버퍼 피크는 native/파일/OS 버퍼와 프로세스 전체 메모리를 포함하지 않는다.

| 회차 | 이전 전체(ms) | 개선 전체(ms) | 이전 축소 합계(ms) | 개선 축소 합계(ms) |
|---|---:|---:|---:|---:|
| 1 | 7409.9 | 6831.2 | 1642.1 | 1126.8 |
| 2 | 7506.2 | 6728.5 | 1777.6 | 1042.4 |
| 3 | 7350.4 | 7174.2 | 1659.2 | 1471.5 |

세 번째 개선 실행의 마지막 단계/게시236.96ms와 재열기32.13ms도 그대로 기록했다. 저장장치/OS 영향을 제거한 순수 계산 벤치마크가 아니다. 원시 결과: `artifacts/ken32-first-load/formal-{baseline|improved}-{1..3}.json`.

## 실제 WPF 표시

`EditorWindow`+실제 `EditorViewModel.OpenCommand`+`ImageLoader`+`Dx12Viewport`를 사용했다. 파일 선택 창은 고정 picker로 대체하고 GPU 초기화 완료 후 명령을 시작했다. 첫 화면은 현재 장면의 overview 타일 준비 후 GPU fence를 기다리는 기존 캡처 경로로 확인했다. 현재 배율의 모든 상세 타일 완료나 원본 전체 해상도의 동시 표시는 이 측정 조건이 아니다.

| 회차 | 이전 명령→캡처(ms) | 개선 명령→캡처(ms) |
|---|---:|---:|
| 1 | 8183.5 | 7786.7 |
| 2 | 7920.0 | 8117.0 |
| 3 | 9562.7 | 11299.7 |
| 중앙값 | **8183.5** | **8117.0** |

전체 표시 중앙값 차이는0.8%로 작고 범위도 겹친다. **WPF 전체 지연이 반복적으로 개선됐다는 증거는 부족하다.** PNG/디스크 준비와 실행 간 편차가 남았다. 세 번째 개선 실행이 더 느린 사실을 제외하지 않았다.

- 로더 완료→문서 반영: 이전15.6~16.6ms, 개선15.4~21.0ms.
- 문서 반영→캡처 파일 완료: 이전99.9~147.3ms, 개선102.9~211.8ms.
- 준비된 캐시로 두 번째 열기→캡처: 이전82.5~116.0ms, 개선131.5~304.9ms. 이 경로는 이번 SIMD를 실행하지 않으며 개선을 주장하지 않는다.
- 창 표시→렌더러 초기화: 이전345~349ms, 개선344~784ms. 비교 대상 SIMD가 실행되기 전 시간이며 파일 열기 시간에 숨겨 넣지 않았다.
- 캡처에는 GPU readback, PNG 파일 기록, 최대20ms 폴링 비용이 포함된다. 화면 표시 지연의 상한 관찰이며 실제 모니터 scan-out 시간은 아니다. 창은 화면 밖에 두고 실제 GPU로 렌더했다.
- `artifacts/ken32-first-load/wpf-{baseline|improved}-{1..3}/result.json`과 `capture-0.png`, `capture-1.png`. 실제 PCB 이미지와 미니맵을 확인했다.

## 변경과 정확성

`TrianglePyramid.FilterVerticalBytes`는8비트 성분을16비트로 넓혀 여러 값을 함께 계산한다. 최대 합2044이므로 넘침이 없다. `ReduceHorizontalBytes`는 RGBA 네 출력 픽셀을 함께 처리하되, 벡터 전체를 안전하게 읽을 수 있는 내부 구간에만 적용한다. 첫/끝 픽셀과 꼬리는 기존 scalar 경로다. CPU 지원 확인도 유지한다.

짝수 길이의 `[1,3,3,1]/8`, 홀수 길이의 `[1,2,1]/4`, 각 축에서의 반올림과 경계 복제를 그대로 유지한다. 원본 level0, Gray16, alpha, 표시 색 처리, ROI와 검사 입력 계약은 바꾸지 않는다. 캐시 형식/식별자도 그대로여서 기존 캐시 재생성을 유발하지 않는다.

- PASS: 정식6개 결과 모두 원본+축소6단계의 **전체 파일 길이와 SHA256이 일치**. 표본 픽셀만 비교한 결과가 아니다. 원본 raw SHA256은 `8CFD78822932CE68A771E2343DDBED1189C85D049D60117F901766155E4D7814`다.
- PASS: 독립 실수 가중치 합성곱96조건으로 Gray8/16/RGBA, 임의 alpha, 폭1~33의 벡터 경계/꼬리,1024/1031, 홀짝 높이 및 행 묶음 경계를 검증.
- PASS: SIMD의 홀짝 조건을 의도적으로 반전했을 때96조건 중16개 실패. 소스 복구 후 전체120개 통과. 오류 주입 결과는 `artifacts/ken32-first-load/mutation.log`.
- PASS: 최종 Release 피라미드120 + 코덱38 + Canvas241 + MVVM 샘플17 + 이미지 이력7 = **423개**. SIMD를 끈 별도 프로세스에서도 피라미드120개 통과. 일반 샘플 실행에서는 수동 대형 파일 측정1개만 명시적으로 skip했다. 전후 WPF 실측6회는 별도 실행해 모두 통과했다.
- PASS: 메인 앱과 샘플/측정 호스트 Release 빌드, 경고0·오류0. 최종 Pyramid DLL은 형식 정리 후 `23A21A8DC4D6444078FD76B2C85B22615B2B6BA82AE90DFCDE3D327AF7410D5B`다.
- PASS: 실제 계산 변경 파일과 신규 측정/테스트 파일4개의 공백·스타일·명명 규칙 검사. `git diff --check` 통과.
- FAIL(기존 범위): `scripts/Format-Code.ps1 -Verify` 전체 검사는69개 파일의 기존 줄바꿈/공백 위반으로 중단됐다. 연결용 기존 `EditorWindowTests`의 두 블록도 포함하며, 이번에는 `partial` 추가 이외의 주변 재포맷을 하지 않았다. `artifacts/ken32-first-load/format-full.log` 참조.

PNG 읽기 버퍼8192→256KiB 확대 탐색은 전체7.379초/디코딩5.042초로 개선이 없어서 되돌렸다. 기존 libpng SSE2 경로는 이미 켜져 있었다. [libpng 공식 소스](https://github.com/pnggroup/libpng)의 읽기 버퍼 API를 사용한 실험이며, CRC 검사 생략·해상도 축소·디코더 교체는 적용하지 않았다. 네이티브 소스/배포 DLL의 최종 동작은 기존과 같다.

## 재현

```powershell
dotnet build experiments/PyramidComparison/Streaming/Streaming.csproj -c Release
dotnet experiments/PyramidComparison/Streaming/bin/Release/net10.0/Streaming.dll profile-load ken32.png artifacts/new-load-cache artifacts/new-load.json

$env:VISIONCANVAS_LOAD_PROFILE = 'artifacts/new-wpf-load'
dotnet test tests/VisionCanvas.MvvmSample.Tests -c Release --filter FullyQualifiedName~MeasureKen32CommandThroughRenderedCapture
$env:VISIONCANVAS_LOAD_PROFILE = $null

dotnet test experiments/VisionImaging.Pyramid.Tests -c Release
$env:DOTNET_EnableHWIntrinsic = '0'
dotnet test experiments/VisionImaging.Pyramid.Tests -c Release --no-build
$env:DOTNET_EnableHWIntrinsic = $null
dotnet test experiments/VisionImaging.Codecs.Tests -c Release
dotnet test tests/VisionCanvas.MvvmSample.Tests -c Release
```

결과/캐시 폴더는 존재하지 않는 경로를 선택한다. WPF 큰 파일 측정은 환경 변수를 지정해야만 실행되며 일반 테스트에서는 명시적으로 skip된다. `ken32.png`는 GitHub 크기 제한 때문에 로컬에만 보존하는 기존 정책을 따른다.

비교 바이너리·원시 보고서·전체 픽셀 검증용 캐시를 포함한 `artifacts/ken32-first-load`는 **44.85GiB**였으며, 측정 완료 후 사용자 요청으로 삭제했다. 재측정에는 위 명령으로 새 결과를 생성해야 한다.

## 카메라 입력의 의미

직접 카메라 픽셀은 `VideoFrame → IFramePresenter.PublishFrame → LatestFrameMailbox → RenderWorker` 경로로 들어간다. 이 경우 PNG 압축 해제와 디스크 피라미드 생성은 거치지 않는다. 카메라 SDK가 PNG/JPEG 파일을 만들고 앱이 그 파일을 다시 열면 해당 비용은 다시 발생한다.

카메라 전송, Bayer/RGB 변환, 메모리 복사, GPU 업로드와 표시에는 별도 비용이 든다. 표시 mailbox는 최신 프레임을 우선하며 누락 없는 검사 프레임 큐를 대신하지 않는다. 샘플의2카메라는 합성이며 실제 카메라 SDK/전송/FPS는 이번에 측정하지 않았다. 따라서 '실제 카메라는 무조건 빠르다/60FPS다'라고 보장하지 않는다.

raw 캐시는 디스크에 영구 저장되므로 **PC 재부팅만으로 지워지지 않는다**. 파일의 경로/길이/수정 시각 또는 디코더/캐시 버전이 바뀌거나 예산에 따른 회수가 일어나면 다시 준비할 수 있다. 이번 변경은 기존 캐시 버전을 유지한다.

## 설계 원칙 검토

| 원칙 | 확인 |
|---|---|
| SRP | 축소 계산은 Pyramid, 디코딩은 Codecs, 측정은 실험/테스트 호스트 |
| OCP | 기존 캐시·타일 API를 유지하고 내부 계산만 개선 |
| LSP | 출력 픽셀·취소·오류·소유권 계약 유지, 미지원 CPU scalar 경로 검증 |
| ISP | 새 공개 인터페이스 없음, 기존 작은 영상 계약 사용 |
| DIP | Canvas/VM에 코덱·측정 의존성 추가 없음 |
| Information Expert | 필터 가중치/경계 규칙은 TrianglePyramid가 계속 소유 |
| Creator | 기존 소스/버퍼 생성·반환 주체 유지 |
| Controller | 명령·이미지 교체·문서 게시 흐름 유지 |
| Low Coupling | SIMD는 내부 구현에 한정, 새 패키지 없음 |
| High Cohesion | 세로 합산/가로 축소를 이름 있는 메서드로 구분 |
| Polymorphism | 실측 디코더/로더는 기존 계약을 감싼 테스트 어댑터 |
| Pure Fabrication | 계측은 외부 호스트에만 존재, 제품용 진단 계층 없음 |
| Indirection | 기존 ImageLoader/IRasterDecoder 경계 재사용 |
| Protected Variations | CPU 지원 여부·가장자리·원본 형식 계약을 테스트로 보호 |
| MVVM | App/VM/View/Core의 기존 책임·바인딩·이력 불변 |
| DRY / Clean Code | 기존 축소 경로를 개선, 수식의 scalar/SIMD 표현은 같은 계약 테스트로 검증. 한국어 주석으로 확대 합산/경계 이유 설명 |
| YAGNI | 새 코덱/캐시 형식/백그라운드 준비 시스템/옵션 프레임워크 없음 |

원칙 예외 없음. 기존 다른 작업의 변경을 보존했다. PNG 약5초, 실제 냉간 부팅과 카메라 수집, WPF 전체 지연의 일관된 개선은 남은 한계다.
