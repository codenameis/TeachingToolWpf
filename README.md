# WPF · DX12 Vision Teaching Sample

**오른쪽 패널 개편:** 검사/영역/필터와 기본/상세 설정/결과/로그로 나누고 검사 버튼을 하단에 고정했습니다. 경고에는 원인과 수정할 설정을 안내합니다. [사용법·화면·검증](docs/inspection-sidebar.md)을 참고하세요.

**GPU 없는 CPU 매칭 비교:** LINE-2D / Sub-NCC / Directional Chamfer를 독립 C# 프로젝트로 구현하고 기존 WPF의 매칭 방식 선택에 추가했습니다. [WPF 사용법](docs/cpu-matching-wpf.md), `CpuMatching.slnx`와 [독립 실행](docs/cpu-template-matching.md)을 사용하세요. 정확도를 우선해 같은 점수0.75에서 Kensa 제한 영역의 Chamfer 추가 오검출을 수정하고 계산 재사용을 적용했습니다. [보정 원인·추가 데이터·실측](docs/cpu-matching-accuracy.md)을 확인하세요. 현장 전수 정확도와 검사 표시125ms는 미달·미검증 범위가 남아 있습니다.

PoseMatch는 **TorchSharp C# CPU/CUDA 학습·추론**과 기존 ONNX 비교 경로를 제공합니다. 앱에서 엔진과 학습한 가중치 파일을 선택하고, 별도 C# CLI에서 가중치를 학습할 수 있습니다. **현재 Kensa/8K 누락·위치·각도와125/80ms 목표는 미달**입니다. [사용법·실측·미완료 범위](docs/posematch-tdcm-torchsharp.md)를 확인하세요.

**진단 없는 독립 WPF 앱은 [VisionCanvas MVVM 샘플](samples/VisionCanvas.MvvmSample/README.md)을 사용하세요.** 이미지·ROI 편집과 이력만 포함하고 기존 티칭 앱을 참조하지 않습니다. `dotnet run --project samples/VisionCanvas.MvvmSample/VisionCanvas.MvvmSample.csproj -c Release`로 실행합니다.

기존 ROI 그룹에 바깥 독립 ROI를 그룹화하면 부모 테두리가 확장됩니다. 필요한 경우 사각형으로 바뀌며 ROI 위치·형상은 유지합니다. 확장 결과가 상위 모듈·루트 또는 이미지를 벗어나면 전체 작업을 거부합니다. 최초 입력·편집 확정 지연의 후속 개선과 남은60FPS 한계는 [검증 기록](docs/group-expansion-validation.md)을 따릅니다.

2026-10-15 후속: 주황색 복수 선택의 회전을 제거하고, 부모 경계에서 ROI가 벽을 따라 미끄러지도록 변경했습니다. 단일 ROI·실제 그룹 회전은 유지합니다. 4만 ROI 입력 성능의 측정 조건·개선·60FPS 판정은 [검증 기록](docs/group-interaction-validation.md)을 따릅니다.

**C# 문법 다음 단계부터 배우려면 [신입 개발자 HTML 매뉴얼](manual/index.html)을 여세요.** 영상처리 기초·좌표와 수식·SOLID/GRASP·ROI 이력·타일·DX12·검사·직접 구축 실습을 13개 페이지로 설명합니다. 오프라인 실험 5개, 본문에서 읽는 실제 코드16곳, 실행 가능한 C# 실습 2개의 전체 코드를 포함합니다. [타일링 설명과 이동 실험](manual/06-streaming.html), [이번 정리와 검증 범위](docs/beginner-manual-validation.md)를 함께 확인하세요.

Windows x64용 비전 티칭 샘플입니다. WPF는 조작 화면을, Direct3D 12는 이미지·ROI·선택 핸들·한글 태그를 그립니다. 8K 합성 영상과 24K급 완성 이미지 파일을 다룹니다.

상단 **1억 ROI 테스트**는 **현재 메인 편집기의 이미지 중심**에100,020,001개 반복 사각형을 추가합니다. 기존 ROI와 함께 마퀴 부분 선택·복사·붙여넣기·삭제·이동·단일 셀 회전·통합 Undo/Redo를 지원하고, 더블클릭한 개별 셀의 위치·크기·각도를 기존 속성 패널에서 편집합니다. 목록은 반복 배치 한 행으로 표시합니다. 일반 ROI30만 개의15만/20만 부분 선택도 개선했습니다. **엄격한60FPS 이상 유지는 아직 미달**이며 반복 셀의 그룹·이름/검색 범위·필터·마스크·검사는 지원 범위에 포함되지 않습니다. [사용법·실측·범위](docs/main-editor-repeated-roi-validation.md)를 확인하세요.

후속으로 DXGI 표시 준비를 기다린 뒤 최신 입력을 가져오도록 개선했습니다. 그리기 시작 시 장면 경과 평균20~22ms가3~7ms로 줄었고, 제출 간격 p95도18ms대에서17ms대로 줄었습니다. [동일 조건 전후 비교와 남은 프레임 예산](docs/dx12-frame-pacing.md)을 확인하세요.

ROI 속성에서 **일반/검색 범위 타입**, 가로·세로 검색 여유, 이름과 캔버스 이름 표시를 설정할 수 있습니다. 검색 범위는 빨간 점선이며 부모 ROI는 그룹 전용입니다. [사용법과 검증](docs/roi-search-range.md)을 참고하세요.

오른쪽 **마스킹**에서 ROI 안의 사각형·타원 제외 영역을 그릴 수 있습니다. 이동·회전·크기·복사·Undo/Redo를 따르며 원본 학습과 선택 검사에 연결됩니다. [마스크 사용법과 적용 규칙](docs/roi-masking.md)을 참고하세요.

화면 표시는 **GPU 우선, 사용 가능한 DX12 하드웨어가 없으면 CPU(WARP)로 자동 전환**합니다. 별도 GPU가 없는 Windows PC도 같은 회전·ROI·타일 기능을 사용합니다. 현재 모드는 상태 표시의 `GPU`/`CPU (WARP)`로 확인할 수 있습니다. GPU가 있는 PC에서 Canvas의 CPU 표시를 시험하려면 앱 또는 독립 샘플에 `--software-rendering`을 지정하세요. 실제 성능과 검증 범위는 [GPU/CPU 렌더링 검증](docs/software-rendering-validation.md)을 따릅니다.

**현재 성능 수용 상태(2026-10-05): 대량 ROI 작업 전체 구간의 2D 60FPS는 FAIL입니다.** 271,255개 ROI의 그룹화·마우스 회전·Esc 취소와 반복 확대를 개선했지만, 최초 형상 준비·일부 GC·게시·이력 처리에서 약 16.67ms 프레임 예산을 넘습니다. [그룹화·회전 후속 검증](docs/group-freeze-validation.md), [프레임 예산 검증](docs/frame-budget-validation.md)에 측정 조건과 남은 지연을 기록했습니다. 실제 사용자 문서·물리 입력과 모니터 표시·장시간 부하·별도 저사양 PC 검증은 남아 있습니다.

논문 형상 검사의 **CPU 125ms 이하 / CUDA 80ms 미만 목표도 FAIL**이며 2D 프레임 예산과 별개입니다. [검사 후속 검증](docs/paper-shape-125ms-validation.md)과 [작업 상태](tasks/todo.md)를 기준으로 판단하세요. 아래 500ms 시험은 이전 조건의 실측이며 현재 목표 달성을 뜻하지 않습니다.

검사 도구는 **학습 패턴 찾기 / 일반 도형 찾기**로 선택합니다. **도형 샘플 → 도형 검출**은 티칭 없이 회전된 삼각형·사각형·십자가와 원을 찾습니다. 채워지고 분리된 도형이 대상이며 테두리만 있는 도형/가림은 지원하지 않습니다.

논문 기반 형상은 학습 영역 안의 추가 윤곽을 최종 점수에서 감점합니다. 검출 누락이 컸던 **빠르게** 프리셋은 제거하고 **기본 후보 예산 / 확대 후보 예산**을 제공합니다. 가까운 자세 후보가 검증 예산을 독점하지 않도록 순서를 고쳐 동일 40개 합성 패턴에서 기본 설정 검출이 11개에서 40개로 늘었습니다. 합격 점수와 예산은 유지했습니다.

검사는 DX12를 사용하지 않습니다. 기본은 CPU이고 선택 가능한 **GPU 후보 계산 + CPU 정밀 검사 · CUDA**는 방향 응답과 템플릿 후보 점수를 GPU에서 배치 계산합니다. 전처리·연속 방향 검증·원본 최종 판정은 CPU입니다. CUDA 장치는 앱 세션에서 재사용하고 최초 준비 시간을 별도로 표시합니다. WPF 화면의 DX12 렌더링은 유지합니다. [사용자 파라미터](docs/matching-parameters.md), [현재 실행 구조](docs/decisions/0026-8k-shape-search-latency.md)를 참고하세요.

형상 검사는 원본 reader 한 개와 불변 crop의 CPU 병렬 계산을 연결합니다. 새 형상 설정은 논리 프로세서 수(최소1/최대16, 기준 장비12)를 사용하며 1~16으로 조절할 수 있습니다. **이전 500ms 시험의 v4 일반 앱에서 8K PCB 256핀을 첫 CPU 459.0 ms, 준비 후 CUDA 375.0 ms에 검출했습니다.** 당시 실제 확대 화면에서 깨끗한 핀 1.000/사선 핀 .957을 확인했습니다. 별도 서비스 benchmark 32/32회도 500 ms 이내이며 반복 중앙값은 **논문 CPU 336.9 / CUDA 305.6 ms**, **기하 CPU 321.8 / CUDA 298.8 ms**였습니다. 전체 사선 그룹과 깨끗한 그룹의 최소 점수 차이 .0295와 CPU/GPU 결과 동등성을 유지했습니다.

이전 시험에서 학습·최초 화면 준비·CUDA 초기화는 별도입니다. 수동 앱의 GPU 준비는 470.6 ms였으며, **정상 표시 WPF 진단의 준비 포함 첫 GPU 명령은 907.7 ms로 500 ms 미달**입니다. 준비 후 명령 15회는 통과했지만 숨김 창 진단은 서비스 밖 대기로 명령이 모두 500 ms를 넘었습니다. 다른 부하나 임의 입력의 지연 보장이 아니며 파일 입력·큰 학습 영역까지 충족한 것은 아닙니다. 이전 앱 첫 522.5/536.8 ms 실패도 보존했습니다. 당시 자동 테스트 878 PASS/skip 0, 실제 앱 기능 smoke PASS였습니다. [측정 조건·실제 화면·한계](docs/shape-500ms-validation.md), [당시 수용 기준](tasks/shape-500ms-plan.md)을 확인하세요. 숫자 입력은 `0.72`처럼 소수점을 한 글자씩 입력해도 유지됩니다. 배경 잡음·회전·배율 정합성의 이전 수정은 [정합성 재검증](docs/shape-regression-validation.md)에 기록했습니다.

`VisionImaging`과 `VisionInspection`은 WPF/DirectX 없이 원본 파일을 읽고 검사합니다. 같은 서비스를 사용하는 [.NET 10 검사 CLI](src/VisionInspection.Cli/README.md)와 [JSON 레시피 예제](samples/inspection)를 추가했습니다. Windows x64와 Linux x64 native 패키지를 배포 대상에 맞춰 선택합니다. Linux 실기 실행은 아직 검증하지 않았습니다.

코드를 처음 읽는다면 [코드 읽기 안내](docs/code-guide.md)에서 작업별 시작 파일, 입력 → 편집 → 렌더 흐름과 GPU 용어를 확인하세요.

교육·릴리즈 전 확인한 수정, 전체 컨벤션·회귀 결과와 선택형 분석기의 잔여 진단은 [최종 코드 품질 점검](docs/release-quality-review.md)에 정리했습니다.

외부 프로그램에서 기존 `Inspection` 레이어에 추가하거나 사용자 레이어를 만들려면 [표시 레이어 사용 가이드](docs/drawing-layers-guide.md)를 참고하세요. 앱 하단 **크로스헤어 켜기/끄기**는 이미지 중심을 지나는 캔버스 전체 크기의 십자선을 전환합니다. 상단 **설정** 버튼은 색상·두께·가이드·영상 표시를 모은 [ConfigPage](docs/configuration-guide.md)를 엽니다. ROI 생성·다중 선택 중에는 현재 마우스 사각 영역만 반투명하게 표시합니다.

ROI는 **사각형·원·삼각형·폴리곤**을 지원합니다. 왼쪽 **추가할 도형**에서 종류를 선택한 뒤 **ROI 추가** 또는 **그리기**를 누르세요. 사각형·원·삼각형은 즉시 추가 또는 드래그로 만들고, **폴리곤은 추가 버튼도 빈 점 입력을 시작합니다.** 이미지에서 꼭짓점을 차례로 클릭한 뒤 **완료 / Enter / 첫 점 클릭**으로 닫으세요. **새 선분이 기존 선과 교차하면 첫 교차점에서 자동 완성**하며, 그 지점부터 이어진 닫힌 영역만 ROI가 됩니다. 교차점 이전의 열린 꼬리와 교차점을 지나친 부분은 제외됩니다. **점 취소 / Backspace**는 마지막 점, **취소 / Esc**는 초안을 지웁니다. 부모·자식 추가에 같은 규칙을 적용합니다.

도형별 실제 영역으로 선택·포함·필터 표시를 판정하며, 이동·크기·회전·복사·그룹·연쇄 삭제·Undo/Redo에서도 종류와 윤곽을 유지합니다. 원은 너비와 높이를 같은 지름으로 적용합니다(두 값 모두 수정하면 너비 우선). 폴리곤은 오목 도형을 허용하며, 그리기의 교차는 닫힘으로 처리합니다. 저장되는 윤곽은 자기 교차·중복 점·0 면적이 없는 단순 폴리곤입니다. 생성 후 개별 꼭짓점 편집, 타원, 구멍이 있는 복합 도형은 아직 지원하지 않습니다. [도형 설계](docs/decisions/0012-roi-shapes.md), [점 입력·교차점 자동 완성](docs/decisions/0028-polygon-point-drawing-and-crossing.md)

## 고전적 2D 패턴 학습·검사

추가된 **PoseMatch-TDCM · ONNX**는 별도 C# 프로젝트의 공식 사전 학습 모델을 사용하는 CPU/CUDA 실험 모드입니다. 사각형 ROI18~2048px를 내부36×36으로 정규화하며 회전·독립 X/Y크기를 추정합니다. 장치/창/스레드/예산/명암 자세 보정 파라미터를 패널에서 설정합니다. 기존 고전적 방식과 별도로 전체 탐색하며, 신뢰도를 제품 OK로 판정하지 않습니다. **CPU125ms/CUDA80ms 및 현장 정밀 검출 요구는 아직 미달입니다.** [사용법·크기별 실측](docs/posematch-tdcm.md), [CUDA 실행·RTX3060 측정](docs/posematch-tdcm-cuda.md), [독립 API](src/PoseMatch.Tdcm/README.md).

상단 **패턴 샘플**로 전용 이미지를 바로 열 수 있습니다. 왼쪽 도형 전체를 ROI로 학습하세요. 오른쪽의 20° 회전·1.2배 도형까지 찾으려면 **형상 매칭 → 학습 설정 → 균일 스케일 검색**을 켜고 **최대 배율을 1.2 이상**으로 설정한 뒤 학습하세요. 기본 8K 칩을 학습할 때는 평평한 내부만 잡지 말고 **테두리·핀을 포함**하세요. 윤곽 부족은 학습 실패로 알립니다. 실행 중/실패 상태는 캔버스와 패널 상단에서 확인할 수 있습니다.

오른쪽 **패턴 학습 · 검사**에서 기존 ROI를 하나 선택하고 **선택 ROI로 모델 학습**을 누르세요. 학습 옵션의 각도 범위는 학습 ROI 방향에 더할 회전 편차이며, 결과 각도는 이미지 기준 절대각으로 반환합니다. 같은 세션에서는 이미지를 바꿔도 등록 모델이 남습니다. **선택 ROI 안에서 기준점 검색**을 선택하면 ROI 내부 기준점만 허용합니다. 선택하지 않으면 현재 이미지 전체를 검색합니다. 라이브 영상은 정지한 뒤 실행하세요.

**매칭 방식**에서 **OpenCV · 템플릿 매칭**, **논문 기반 · 형상 매칭**을 선택합니다. OpenCV는 `CCOEFF_NORMED`로 학습 당시 방향·크기를 고정하고 위치를 찾습니다. 논문 기반은 방향 특징으로 회전·배율을 검색하고 원본 윤곽과 세부 획을 검증합니다. 각각 모델·학습 ROI·설정을 별도로 보관합니다. 학습 조건을 바꾸면 재학습 전 검사를 막습니다. [방식·파라미터 상세](docs/matching-parameters.md)

형상 매칭은 원본 타일로 만든 검사 피라미드에서 후보를 찾고, 후보 주변의 원본 픽셀로 각도·배율·위치를 정밀하게 검증합니다. 표시 LOD나 필터 미리보기는 검사에 사용하지 않습니다. 원본 도형 마스크·기준점·좌표를 유지하며 결과 표시는 ROI 문서와 Undo/Redo를 변경하지 않습니다. 작업 영상과 후보 수에는 [명시적인 상한](docs/decisions/0016-template-matching-and-pyramid-search.md)이 있습니다.

검사 후 **위치 십자선·방향 화살표·검출 경계·번호·점수·X/Y/각도**를 표시합니다. **검출 위치로 화면 맞춤**으로 결과를 찾고, **특징점 표시**를 켜서 검출 자세로 변환된 학습 윤곽점을 확인하세요. **검사 위치 표시**로 결과 전체를 숨길 수도 있습니다. 옵션은 재검사나 편집 이력을 만들지 않습니다.

판정은 **정상 LimeGreen**, **불량 Tomato**, **경고 Orange**, **사용자 확인 Gold**입니다. 완전한 검색의 미검출은 불량, 검색 불완전·오류·취소는 경고, 검출 자세 정밀화나 회전각이 불확실하면 사용자 확인으로 표시합니다. 현재 판정은 패턴 존재·자세에 대한 검사이며 별도 치수/공차/기대 개수 판정은 아닙니다. [판정·표시 계약](docs/decisions/0015-inspection-result-display.md)

기본 8K의 칩 전체 ROI(1600×1120) 검색은 후속 최적화로 3회 중앙값 **4.57초→3.46초**, 매회 16개 검출이었습니다. 실제 UI 드래그 ROI에서는 약 5.3초였으며 즉시 검사 수준은 아닙니다. [칩 재현·실측](docs/decisions/0017-default-chip-inspection.md)을 참고하세요. 이전 중앙 교차선 512×512 ROI 시험(칩 내부가 아님)은 형상 30.83초→3.26초, OpenCV 2.81초/9개였습니다. [이전 측정](docs/matching-performance-2026-10-04.md)과 [논문 대응 및 차이](docs/decisions/0016-template-matching-and-pyramid-search.md)를 구분하며 실제 촬영 데이터·24K 검사 시간·상용 matcher 동등성은 확인하지 않았습니다.

패턴 검사 화면의 **점선은 검색 범위**, **실선은 검출 자세로 이동한 학습 ROI 도형**입니다. 회전 사각형·원·삼각형·오목 폴리곤을 유지하고, 캔버스에는 번호·점수를 간결하게 표시합니다. 상세 X/Y·각도·배율은 오른쪽 결과에서 확인하세요. 패턴의 표시 경계는 학습 영역이며, 일반 도형 찾기는 검출한 실제 외곽 윤곽을 표시합니다. [표시 경계 계약](docs/decisions/0018-inspection-boundaries.md)


두 방식의 **앱 최대 결과 수 기본값은 각각 256개**입니다. 결과가 더 있는데 상한에 걸리면 생략 사실을 경고합니다. 후보 예산 부족과 출력 개수 제한은 구분해서 표시합니다. 사용자 재현의 핀 하나 학습에서 OpenCV와 논문 기반 형상 모두 최대 결과 수 16을 1000으로 변경하니 같은 모델로 256개를 검출했습니다. 논문 모드의 8K 합성 핀 검사는 원본 타일 재사용으로 중앙값 5.83초에서 4.39초로 줄었으며 검출 결과는 동일했습니다. [원인·실측·한계](docs/decisions/0021-shape-output-and-original-tile-reuse.md)

## 실행

배포 스크립트로 생성하는 실행 파일은 `artifacts/publish/TeachingToolWpf.exe`입니다. 빌드 결과물·복원 패키지·대용량 테스트 이미지는 Git에 포함하지 않으므로 처음 복제한 뒤에는 아래 명령으로 빌드하세요. 실행에는 **.NET 10 Desktop Runtime x64**와 OpenCV 네이티브 실행용 **Visual C++ 2022 x64 Runtime**이 필요합니다. 네이티브 구성과 고지는 [의존성 고지](THIRD-PARTY-NOTICES.md)를 확인하세요. DX12 GPU를 사용할 수 없으면 Windows WARP로 CPU 표시하며, 현재 장비의 자동 선택은 RTX 3060 Laptop입니다. 배포 폴더 전체를 함께 사용하세요.

소스에서 실행하려면 .NET 10 SDK를 설치한 뒤:

```powershell
dotnet restore .\TeachingToolWpf.slnx
dotnet build .\TeachingToolWpf.slnx -c Release --no-restore
dotnet run --project .\src\TeachingToolWpf\TeachingToolWpf.csproj -c Release --no-build
```

실행 폴더를 다시 만들 때는 restore 후 `scripts/Publish-Sample.ps1`을 실행합니다. 기존 실측 자료가 있으면 설명서와 함께 복사합니다.

다음 명령은 이전 500ms 검증 당시와 같은 기준 장비용 배포 설정으로 **현재 소스**를 게시합니다. 과거 v4 바이너리를 복원하는 명령은 아닙니다. .NET 10의 AVX2 ReadyToRun과 tiered compilation 해제를 이 배포에만 적용하며 시스템 환경변수는 바꾸지 않습니다. 일반 Debug/Release 실행의 최초 JIT 시간은 위 성능과 다릅니다. 다른 CPU·Linux 실기 성능은 미검증입니다.

```powershell
pwsh -File scripts/Publish-Sample.ps1 -InspectionLatency -OutputDirectory artifacts/publish-shape-500ms-v4
.\artifacts\publish-shape-500ms-v4\TeachingToolWpf.exe
```

처음에는 8192×8192 검사 패턴이 열립니다. 상단의 **이미지 열기 / 교체**로 PNG/JPEG/WebP/TIFF/BigTIFF를 불러오거나 **24K 샘플**을 눌러 바로 시험할 수 있습니다. 최초 파일 열기는 확대/축소용 디스크 캐시를 생성하므로 시간이 필요합니다. 진행 상태와 읽기 취소 버튼을 제공합니다.

## 조작

| 작업 | 버튼 / 입력 |
|---|---|
| 확대·축소 | 휠(커서 중심), −/+, 배율 입력 후 적용 |
| 화면 맞춤 / 원본 배율 | 화면 맞춤(F), 1:1. 1:1은 원본 1px = 물리 화면 1px |
| 화면 이동 | 이동 도구(H)에서 드래그, 중간 버튼, Space+드래그, 방향 이동 버튼 |
| 미니맵 | 우하단 전체 이미지에서 클릭/드래그로 이동. 파란 사각형은 현재 화면 위치. 배율은 유지하며 휠로 확대·축소 가능 |
| ROI 생성 | 부모 추가/부모 그리기(R)는 독립 ROI. 부모로 쓸 ROI 하나를 선택한 뒤 자식 추가/자식 그리기로 연결. 그리기 성공 후 선택 도구 복귀 |
| 선택 | 선택(V), Ctrl+클릭 토글, Shift+클릭 추가, 빈 곳 드래그 범위 선택 |
| 겹친 ROI 선택 | 같은 위치에서 Alt+클릭 반복 |
| 크기 조절 | 단일 선택 ROI의 8개 핸들, 또는 속성 패널의 너비/높이 |
| 이동 / 회전 | ROI 내부 드래그 / 상단 회전 핸들. Shift로 이동 축 또는 회전 15° 고정 |
| 다중 편집 | 여러 ROI 이동·삭제. 복수 선택의 회전은 제공하지 않으며 실제 그룹 하나의 회전은 유지. 부모와 자식을 함께 선택해도 자손 이동은 한 번 |
| 수치 편집 | 오른쪽 이름·중심·크기·절대 각도 입력 후 속성 적용 |
| 각도 초기화 | 0° 초기화(0). 이미지 기준 절대 각도 |
| 그룹화 | 2개 이상의 독립 ROI 선택 → 부모로 그룹화(Ctrl+G) |
| 그룹 해제 | 생성된 그룹 부모 선택 → 그룹 해제(Ctrl+Shift+G). 자식은 보존 |
| 복사 / 붙여넣기 | 왼쪽 복사 / 붙여넣기 버튼, Ctrl+C / Ctrl+V. 선택 ROI와 자손·필터 설정을 함께 복제 |
| 삭제 | 삭제(Delete). 부모를 삭제하면 모든 자손도 삭제 |
| Undo / Redo | 상단 버튼, Ctrl+Z / Ctrl+Y 또는 Ctrl+Shift+Z |
| 편집 취소 | Esc로 진행 중 드래그·대량 붙여넣기 취소. 포인터 캡처 상실 시 드래그 취소 |
| 정밀 이동 | 선택 후 방향키 1 이미지 px, Shift+방향키 10px |

기본 팔레트에서 부모 ROI는 **핑크**, 자식은 **초록**, 독립 ROI는 **회색**입니다. 단일 선택은 역할별 강조색, 다중 선택은 공통 **주황색**으로 묶어 표시하며 색상을 각각 변경할 수 있습니다. 마지막 자식이 삭제되면 기존 부모도 현재 관계에 맞는 색으로 돌아갑니다. 자식이면서 부모인 중간 ROI는 부모 색이 우선입니다. 선택은 흰색 사각 핸들·원형 회전 핸들·각도 배지로 구분합니다. 왼쪽 목록에서도 다중 선택할 수 있습니다. 겹침·포함·이동만으로 부모가 바뀌지 않습니다. 부모 이름표에 직접 자식 개수가 표시됩니다. 겹치는 태그는 일부 생략하지만 전체 이름은 목록에 남습니다.

상단 **설정 → ROI 색상과 선 두께**에서 역할별 색상과 **0.5~8 DIP** 두께를 입력하고 `ROI 표시 적용`을 누르세요. 색상은 `#RRGGBB` 또는 `#AARRGGBB`입니다. 선택 윤곽에는 0.5 DIP가 추가되며 확대율과 무관하게 유지됩니다. `ROI 기본값`은 색상과 두께를 모두 복원합니다. 다중 선택 외곽선은 일반 ROI의 다중 선택 색을 따르며 회전 핸들은 표시하지 않습니다. 표시 설정은 현재 앱 세션에 적용되고 ROI 편집 이력과 별도로 관리됩니다.

대량 회전은 드래그 중 기존 GPU 도형에 공통 회전값을 적용하고, 놓을 때 형상·관계·이력을 한 번 확정합니다. ±15° 버튼도 같은 경로를 사용합니다. Shift는 15° 단위이며 이미지 경계가 우선합니다. 큰 편집 확정 중에는 화면 이동·확대와 Esc 취소가 가능합니다. [4만~40만 ROI 회전 실측과 남은 비용](docs/rotation-performance.md)을 참고하세요. 작은 화면에 조밀한 ROI는 기존 화면 밀도 표시를 사용하며 확대하면 세부 윤곽을 표시합니다.

복사본은 새 ID를 가지며 붙여넣기마다 화면 기준 20 DIP씩 옮깁니다. 이미지 경계에서는 이동을 제한하고, 더 작은 이미지로 붙여넣으면 전체 복사 집합을 균일 축소합니다. 복사한 하위 트리의 내부 부모 관계는 새 ID로 매핑해 유지합니다. 복사 루트는 독립 루트이며 기존 ROI에 자동 연결하지 않습니다. 앱 내부 클립보드를 사용하며 앱 종료 시 비워집니다. 이름·숫자 입력칸에서는 Ctrl+C/Ctrl+V/Del이 텍스트 편집으로 동작합니다.

대량 붙여넣기는 작업 스레드에서 준비하며 상태 표시줄에 진행 상태를 표시합니다. **Esc로 취소**할 수 있고, 성공한 붙여넣기는 한 번의 Undo로 되돌립니다. 300개부터 전체 선택·복제로 19,200개까지 늘리는 시험과 7,000개 필터 ROI 복제 결과는 [검증 기록](docs/clipboard-minimap-validation.md)을 참고하세요. 전체 선택과 결과 확정에는 짧은 UI 지연이 남아 있습니다.

미니맵은 정적 8K/24K와 라이브 영상에 연동됩니다. 전체 이미지와 현재 가시 영역을 보여주며 이동은 편집 이력을 추가하지 않습니다. 미니맵 드래그 중 Esc를 누르면 이전 위치로 돌아갑니다. 라이브 개요 영상은 최대 초당 2회 갱신합니다. 아주 작은 뷰포트에서는 미니맵을 숨깁니다.

**이미지 교체에 성공하면 ROI와 선택이 초기화됩니다.** Undo로 이전 이미지·ROI·선택·뷰를 함께 복원할 수 있습니다. ROI 그리기가 이미지 밖으로 나가면 이미지와 겹치는 크기만 생성합니다. 이동·크기·회전·수치 편집도 이미지 경계에서 제한하며, 여러 ROI는 함께 가능한 범위까지만 움직입니다.

0도 초기화 시 경계를 넘게 된다면 선택/자손을 함께 이동하고 필요하면 균일 축소해 정확한 0도와 이미지 포함을 유지합니다. Undo로 원래 형상을 복원할 수 있습니다.

## ROI 필터 미리보기

1. ROI를 하나 이상 선택합니다.
2. 오른쪽 **ROI 필터**에서 기존 **Binarize / Canny** 또는 새 **Gaussian·형태학·원본 임계값·연속 처리 프리셋**을 선택합니다. 새 엔진은 **CpuOnly / CpuGpu**와 반경·sigma·임계값을 설정합니다. [사용법·실기 검증](docs/industrial-roi-filters.md).
3. **설정 적용**으로 선택 ROI에 등록하고 **미리보기 ON/OFF**로 표시를 제어합니다. 회전한 ROI도 해당 영역에 맞춰 표시합니다.
4. **ON일 때 등록된 ROI를 다시 선택하면 자동으로 표시합니다.** OFF는 선택을 바꿔도 유지합니다. 미등록 ROI는 원본을 표시하며 다중 선택은 ROI마다 저장된 설정을 사용합니다.
5. ON 상태에서 ROI 이동/회전/크기와 파라미터 조절 도중에도 결과를 갱신합니다. 숫자 입력은 포커스를 잃을 때 확정합니다. 슬라이더는 움직이는 동안 임시 미리보기, 놓을 때 한 번의 이력으로 확정하고 Esc로 취소합니다.
6. **등록 해제** 또는 **없음 · 원본**은 선택 전체의 설정을 지웁니다. 별도 MVVM 컨트롤 프로젝트의 [사용법·검증](docs/filter-controls.md), [재사용 방법](src/VisionCanvas.Filters.Wpf/README.md)을 참고하세요.

필터 등록·변경·해제는 Undo/Redo 대상이며 복사·붙여넣기와 삭제/이미지 교체 Undo에서도 복원됩니다. 계산된 미리보기 픽셀은 이력에 저장하지 않으며 원본 파일을 변경하지 않습니다. 설정은 현재 문서 안에서 유지되며 파일로 레시피를 저장하는 기능은 아직 없습니다.

기존 Binarize/Canny는 OpenCV 4.13이며 Gray16을 8비트로 매핑한 뒤 처리합니다. 새 IndustrialVision 필터는 **Gray16 명암을 연산 중 유지**하고 화면 출력만 8비트로 변환합니다. CUDA에서는 연속 필터 사이에 영상을 유지하며 실제 장치·CPU 전환 이유·시간·전송량을 표시합니다. 컬러 입력은 회색조로 변환합니다. 대용량에서는 필요한 타일만 읽고 ROI당 긴 축 최대 **1,024px**, 선택 전체 약 **4Mpx** 범위의 미리보기로 계산합니다. 이 결과는 원본 해상도 검사 입력이 아닙니다. 라이브 필터는 최신 프레임으로 최대 **초당 5회** 갱신하며 처리 중 요청을 쌓지 않습니다. 라이브 영상 자체의 표시 주기와는 별개입니다.

부모 이동·회전은 자손에게 적용합니다. 부모 resize는 자식의 크기와 상대 위치를 유지하고, 부모 중심이 이동한 만큼 자식도 이동합니다. 편집 확정 후 포함에서 벗어나면 유효 부모 또는 루트로 재배치합니다. 그룹화는 실제 새 부모 사각형을 만들며, 선택하지 않은 ROI까지 포함되면 기존 포함 규칙이 적용됩니다.

각 편집/드래그는 하나의 이력입니다. 이름, 그룹, 연쇄 삭제, 이미지 교체까지 복원합니다. 이력은 최대 200개·추정 1 GiB이며 ROI 변경 값과 이미지 참조만 보관합니다. 이미지 Undo 시 원본 파일이 삭제되거나 변경되었으면 현재 문서와 이력을 유지하고 오류를 표시합니다. 단순 선택·zoom/pan·라이브 프레임 도착은 이력에 넣지 않습니다.

## 8K 실시간과 대용량 이미지

**8K 실시간**은 7680×4320, 목표 60 FPS의 합성 CPU 프레임입니다. Gray8/Gray16/RGBA32/BGRA32를 고를 수 있습니다. 정지는 마지막 프레임을 유지하므로 확대해서 확인할 수 있습니다. 파일/샘플을 열면 라이브 미리보기를 닫습니다. 실제 카메라 SDK 연결·검사·녹화는 포함하지 않았습니다.

라이브 시작도 소스 교체이므로 ROI를 초기화하고 7680×4320 경계를 적용합니다. 라이브 소스로 Undo/Redo하면 해당 소스를 다시 시작하며 과거 시점의 프레임을 재생하지 않습니다.

전체 원본 프레임은 유지하고, 화면에 필요한 영역과 해상도만 area average로 준비해 DX12로 업로드합니다. 작은 화면에서 전체 8K RGBA를 매번 복사하는 비용을 줄였으며, 1:1에서는 해당 영역의 원본 픽셀을 사용합니다. 표시가 늦으면 대기 중인 예전 프레임을 버리고 드롭 수를 표시합니다. 이 정책은 카메라 수집/검사 데이터의 무손실 보장을 뜻하지 않습니다.

정적 대용량 파일은 자체 스트리밍 코덱과 C# 엔진으로 디스크 피라미드를 만든 후 512px 타일로 읽습니다. Gray8/Gray16은 단일 채널을 유지합니다. Gray16 표시 범위는 0–65535이며 별도 window/level 조절은 아직 없습니다. 컬러는 RGBA8로 정규화합니다. **24576×131072 Gray8/Gray16 BigTIFF(디코드 후 3/6 GiB)**를 실제 생성·열기·마지막 타일 검증했습니다. 초기 로드는 파일 압축과 저장장치에 따라 달라집니다.

캐시 위치는 `%LOCALAPPDATA%\TeachingToolWpf\ImageCache`입니다. 디코드 크기의 약 1.5배 이상 여유를 확인합니다. 캐시의 자동 디스크 용량 정리는 아직 없으므로 앱을 닫은 뒤 이 전용 캐시 폴더를 비워 재생성할 수 있습니다. OS 파일 매핑 때문에 프로세스 working set은 관리 버퍼 크기보다 커질 수 있습니다.

형식별 크기 제한도 적용됩니다. 24K 폭은 PNG/JPEG/TIFF로 검증했고 WebP는 8K로 검증했습니다. WebP의 최대 축은 16,383px이므로 24K 파일 용도로 사용할 수 없습니다. [WebP 공식 FAQ](https://developers.google.com/speed/webp/faq)

## 구조와 확장 지점

| 위치 | 책임 |
|---|---|
| `src/VisionGeometry` | double 좌표, 실제 ROI 도형·포함 판정, 강체·뷰 변환 |
| `src/VisionImaging.Abstractions` | 이미지 식별·규격, 프레임·타일·픽셀 버퍼의 소유권 계약 |
| `src/VisionCanvas.Core` | 불변 ROI 문서, BVH, 계층·선택·편집·이력·제스처 |
| `src/VisionImaging` | 기본 스트리밍 파일 로더 진입점, 코덱·디스크 피라미드 연결 |
| `src/VisionCanvas` | WPF/Win32 입력·DPI, DX12 장치·표시·캐시, ROI·필터·오버레이 |
| `src/VisionInspection`, `src/VisionAlgorithm` | 편집 코어와 화면에 의존하지 않는 원본 검사 실행·알고리즘 |
| `src/TeachingToolWpf` | 패널·MVVM, 이미지 교체·레시피의 통합 이력, 검사 조립·제품 판정 |
| `samples/VisionCanvas.Sample` | TeachingToolWpf·검사 참조 없이 공개 Canvas API만 사용하는 WPF 호스트 |
| `benchmarks` | GPU/CPU CSV, 대용량 파일 시험, 편집·검사 측정 |

Canvas를 다른 앱에서 사용하려면 [독립 샘플](samples/VisionCanvas.Sample/README.md)을 참고하세요. `VisionCanvas`만 참조하면 NetVips 없이 호스트가 공급한 프레임·타일을 표시할 수 있습니다. 파일을 여는 샘플은 `VisionImaging`과 자체 `VisionImageCodecs` native 파일을 사용합니다. NetVips 제거와 코덱 라이선스는 [전환 기록](docs/netvips-removal.md)을 따릅니다. ROI 문서·입력·이력은 `VisionCanvas.Core`, 순수 형상은 `VisionGeometry`, 이미지 수명과 `IImageLoader` 계약은 `VisionImaging.Abstractions`에 있습니다. 기존 namespace의 변경과 소스 재빌드 범위는 [분리 결정](docs/decisions/0031-reusable-canvas-boundaries.md), [선택형 디코더 결정](docs/decisions/0032-optional-canvas-file-decoder.md), [검증 기록](docs/canvas-reuse-validation.md)을 참고하세요.

```powershell
dotnet run --project .\samples\VisionCanvas.Sample -c Release
```

카메라 연결은 `IFrameSource.Frames(CancellationToken)`을 구현하고 `MainWindow` 조립 지점의 팩터리를 교체합니다. `VideoFrame` 게시 시 소유권을 넘기고 소비자가 해제하기 전에 버퍼를 재사용하지 마세요. 현재 `PixelBuffer`는 tightly packed CPU 버퍼이며 카메라 stride padding은 어댑터에서 행 복사로 정규화합니다. GPU 공유 메모리/zero-copy는 구현하지 않았습니다. 파일 소스는 `ITileSource`, 표시 준비는 `IFramePreparer`로 확장합니다.

## 검증과 재현

대량 ROI에서는 왼쪽 목록은 4,096개까지 한 가상화 목록으로, 그 이상은 512개씩 표시합니다. 캔버스에서 하나를 새로 선택하면 해당 페이지로 이동합니다. 목록 페이지 이동은 전체 선택을 보존하며, 일반 클릭으로 선택을 교체하고 Ctrl로 추가/해제합니다. 축소 화면의 매우 작은 ROI 집합은 요약 표시되고 확대하면 개별 윤곽이 나타납니다. 이름·숫자 필드는 클릭/Tab으로 그대로 편집할 수 있습니다. 실제 100만·200만 ROI 조건과 초기 구축 비용은 [대량 ROI 성능](docs/roi-scalability.md)을 참고하세요.

ROI 윤곽은 DX12 instance buffer를 재사용하고, 전체 이동 중에는 이동량만 전달합니다. 조밀한 이름표는 화면 밀도에 맞춰 겹침을 줄이고 새 텍스처를 점진적으로 준비합니다. 단일 선택 이름표는 바로 표시합니다. 조건별 GPU 수치와 100만 개 요약 표시의 범위는 [ROI 렌더 성능 기록](docs/retained-roi-performance.md)을 참고하세요.

대량 이동 확정·선택·삭제·붙여넣기·Undo/Redo는 작업 스레드에서 불변 결과를 준비해 한 번에 반영합니다. 준비 중에도 이동 도구/중간 버튼·확대/축소·미니맵을 사용할 수 있으며 Esc로 취소할 수 있습니다. 다른 편집은 준비 완료 후 실행합니다. 목록 페이지는 표시 요소를 재사용합니다. 단일 논리 코어·32,940개 선택의 중앙값은 약 4–6ms였으며 대량 연산 완료까지는 시간이 필요합니다. 10만 개 시험과 남은 간헐 지연은 [CPU 제약 성능 보고서](docs/low-spec-performance.md)에 공개했습니다.

```powershell
dotnet test .\TeachingToolWpf.slnx -c Release --no-restore
dotnet test .\tests\TeachingToolWpf.Imaging.Tests -c Release --no-restore
.\artifacts\publish\TeachingToolWpf.exe --smoke --gpu-debug
.\artifacts\publish\TeachingToolWpf.exe --benchmark --format Bgra32 --rois 500 --seconds 70 --metrics artifacts\benchmark.csv
.\artifacts\publish\TeachingToolWpf.exe --large-image --gpu-debug
.\artifacts\publish\TeachingToolWpf.exe --roi-benchmark --roi-counts 3550 --roi-samples 50 --roi-distributed --roi-report artifacts\roi.json --metrics artifacts\roi-frames.csv
.\artifacts\publish\TeachingToolWpf.exe --roi-benchmark --roi-counts 2000000 --roi-samples 30 --roi-distributed --roi-single-batch --roi-detail --roi-report artifacts\roi-detail.json
.\artifacts\publish\TeachingToolWpf.exe --memory-check --gpu-debug --memory-cycles 300 --memory-report artifacts\memory-replacement.json --metrics artifacts\memory-replacement-frames.csv
dotnet run --project .\benchmarks\TeachingToolWpf.Benchmarks -c Release -- artifacts\core.json
```

진단 실행은 화면 밖 테스트 창을 사용하고 종료 후 `artifacts`에 결과를 남깁니다. `--gpu-debug`는 Windows Graphics Tools가 설치되어 있으면 DX12 debug layer를 켭니다. 벤치마크는 10초 워밍업 후 60초 구간을 비교합니다. `--large-image`는 디스크 캐시를 약 12 GiB 생성하므로 여유 공간을 확보하세요. 성능 목표를 측정 결과와 혼동하지 않도록 [실측 보고서](docs/performance-baseline.md)와 [검증 범위](docs/runtime-validation.md)를 함께 보세요.

`--memory-check`는 8K RGBA PNG와 24K Gray16 TIFF를 번갈아 열고 매번 ROI 필터를 처리합니다. 300주기는 이미지 600회 교체이며, 이번 측정 약 219초 동안 지속 증가하는 누수 징후를 발견하지 못했습니다. 조건·수치·미검증 범위는 [이미지 교체 메모리 검증](docs/memory-replacement-validation.md)을 참고하세요.

미검증/후속 항목은 혼합 DPI 모니터 이동, 실제 장치 제거, 실제 카메라·촬영 파일, 8192² 실시간, 장시간 부하, 분수 배율 타일 gutter입니다. ROI 저장/불러오기, 타원, 생성 후 폴리곤 꼭짓점 편집, 다중 ROI 일괄 resize는 이번 샘플에 포함하지 않았습니다. [품질 검토](docs/quality-review.md), [설계 결정](docs/decisions/0003-implementation-and-measured-optimization.md), [의존성 고지](THIRD-PARTY-NOTICES.md)를 참고하세요.

## 프로젝트 구조와 명시적 ROI 계층 (2026-10-05)

- `src/TeachingToolWpf`: 실행 앱, 패널·ViewModel, 검사 연결, 진단 시나리오.
- `src/VisionCanvas`: 재사용 가능한 WPF DX12 캔버스, HWND 입력, 렌더링·셰이더, 타일·영상·필터 표시. VisionCanvas.Core, VisionImaging.Abstractions와 IndustrialVision.Filters.Pipeline을 참조한다. 파일 로더와 필터 CUDA 구현은 호스트가 연결한다.
- `src/VisionCanvas.Core`: GPU 없는 ROI·계층·입력·이력. 순수 기하와 이미지 계약은 VisionGeometry/VisionImaging.Abstractions로 분리한다.
- `src/VisionAlgorithm`: 독립 알고리즘. 캔버스와 상호 참조하지 않는다.
- `tests`, `benchmarks`, `samples`: 각각 프로젝트 이름별 폴더. 독립 사용 예제는 VisionCanvas.Sample이다.

부모·자식은 위치가 아니라 추가 명령으로 연결합니다. **겹치거나 포함돼도 자동 부모가 되지 않습니다.** 자식은 부모 밖으로 움직여도 관계를 유지하며, 부모 이동·회전·삭제는 그 자식에게 계속 적용됩니다. 자식을 선택해 그 아래에 자식을 추가할 수도 있습니다. 자식 추가는 선택이 정확히 하나일 때 활성화됩니다.

그룹화는 선택한 ROI와 그 자손만 새 부모에 연결합니다. 그룹 해제는 제거되지 않은 조상으로 연결하고, 그룹화 이전의 정확한 관계와 선택은 Undo로 복원합니다. [구체적 정책·검증](docs/decisions/0014-canvas-and-explicit-hierarchy.md).
