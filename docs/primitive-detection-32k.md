# 일반 도형 검출 — 전체 개수·중첩·32K CPU/CUDA

2026-10-16. `main`의 기존 로컬 변경을 보존하고 일반 도형 검출을 수정했다. [ADR-065](decisions/0065-unlimited-nested-primitive-detection.md). 패턴 학습·매칭과 다른 기능이며 커밋/푸시는 하지 않았다.

후속으로 [PCB fiducial 형상](pcb-fiducial-detection.md)을 추가했다. 아래 표는 기존 6종에 대한 당시 측정이며 마름모 거부 시험은 기존 종류만 선택한 조건이다. 새 마름모 선택에서는 마름모를 검출한다.

## 사용법과 결과 의미

- 일반 도형 검출에서 삼각형·사각형·원·십자가·도넛·사각 테두리를 각각 선택한다. 도넛은 바깥 원과 빈 원형 구멍을 한 개로, 그 안의 떨어진 원은 추가 한 개로 센다. 사각 테두리도 같다. 다중 중첩이 가능하다.
- 고급 설정의 **최대 결과 수 0**이 기본값이다. 모든 결과를 유지하며 목록은 256개씩 이전/다음으로 이동한다. 양수로 지정한 상한은 초과 시 경고한다. 소프트웨어 개수 절단을 제거했으며 무한한 RAM을 제공한다는 뜻은 아니다.
- **GPU 사용** 해제는 CPU 전용, 선택은 CPU+NVIDIA CUDA다. CUDA가 정수 median 전처리를 수행하고 CPU가 동일한 이진화·윤곽·분류를 수행한다. 장치 준비 실패는 명시적 실패다. 작은 영역이나 이번 32K 합성에서는 CPU가 더 빠르다.
- 중심·각도는 외곽 도형 기준이다. 면적은 원본 이진 윤곽의 면적이며 빈 구멍 면적을 뺀다. 실제 픽셀 수나 물리 교정 면적은 아니다. 원/도넛의 각도는 0, 사각형/십자가는 90° 대칭 기준이다.
- 안팎이 각각 원/사각형이면 검출하지만 동심도·두께·안팎 평행 여부의 OK/NG를 판정하지 않는다. 서로 붙은 물체, 부분 가림, 여러 구멍, 혼합 형태 구멍, 일반 선 그림은 지원하지 않는다. 최소/최대 크기는 외곽의 이미지 축 기준 폭·높이 6~512px다. 아주 얇은 선/구멍은 기존 3×3 median에서 소실될 수 있다.
- 자동 명암 방향은 각 창의 작은 명암 영역을 전경으로 택한다. 조명·전경 비율이 불균일한 실제 영상은 명시적 밝은/어두운 방향과 이진화 설정을 검증해야 한다. 이번 변경은 전역 임계값으로 바꾸지 않는다.

CLI도 `primitiveOptions.maximumResults=0`, `useGpu=true`를 지원한다. `matches[].innerContours`에 원본 구멍 윤곽을 추가했고 GPU 진단은 `diagnostics.primitivePreprocessing`에 있다. [CLI 사용법](../src/VisionInspection.Cli/README.md).

## 검증 기준

정답 개수, 누락/중복/종류 오류, 원본 좌표, 각도, 윤곽·점수의 CPU/GPU 동일성을 따로 확인한다. 합성 데이터는 코드의 독립 포함식으로 생성하고 검출 결과에서 정답을 역산하지 않는다. 창 경계와 타일 경계에 걸친 물체도 포함한다.

| 항목 | 증거와 판정 |
|---|---|
| 개수 상한 제거 | 한 창 4,225개 및 서비스 1,600개 전수. 명시 상한 4,000/1개는 불완전 경고 |
| 회전·잡음 | 6종 각각 0~89° 개수·종류, 회전/배율/stride/잡음, 밝은/어두운 전경 |
| 중첩 | 도넛→도넛→원, 회전 사각 테두리→사각형, 빈 구멍 중복 집계 없음 |
| 구멍 마스크 | 깊은 구멍은 바깥 도형 유지, 재료/안쪽 경계 마스크는 제외 |
| 오검출 회귀 | 타원/마름모/T/L/I, 원 안의 사각 구멍, 두 구멍 도형을 모든 0~89°에서 거부 |
| 경계·소유권 | 병렬1/4의 원본 좌표·순서·안팎 윤곽 동일, 최대 크기128/512, 실제 오목 ROI 포함 확인 |
| CUDA | 실제 RTX 3060에서 1px 축·stride·가장자리 포함 독립 정렬 median과 모든 픽셀 일치, 자동/수동·극성·구멍 마스크의 결과 일치 |
| 수명·표시 계약 | 중간 취소·장치 없음·지연 완료 거부, 1,100개 페이지/전체 overlay, 안팎 닫힌 메시와 연결선 없음, CLI JSON 왕복 |

기존 80×54px 사각형/84px 십자가와 새 80×54px 사각 테두리의 전체 회전 각도 기준 0.75°는 유지한다. **32×22px 내부 사각형의 1.5° 시도는 FAIL(최대 2°)**이며 이를 정밀 각도 합격으로 바꾸지 않는다. 작은 내부 사각형 회귀는 전수 개수/위치를 확인하고 각도 오차는 32K 평가에서 별도로 그대로 출력한다. 2차 면적 모멘트 대체는 기존 큰 사각형의 1° 입력에서도 0.895° 오류를 만들어 원복했다.

최종 자동 검증은 **254건 PASS**: 알고리즘36, 검사/CLI/원본 reader63, 실제CUDA11, WPF VM11, Canvas 메시11, 원본 파일/피라미드122. 각 이론 테스트 안의 전체 회전/픽셀 비교는 이 테스트 개수와 별도다. 구멍 개수 조건을 뒤집은 오류 주입은 중첩 원 테스트2건을 실패시켰고 원본을 즉시 복원해 다시 통과했다. 별도 CLI 실행도 CPU/CUDA 각각 중첩4개, 전체 결과 JSON 동일이었다.

변경 C#26파일의 공백/명명/스타일 검사는 PASS. `pwsh -File scripts/Format-Code.ps1 -Verify`는 이번 변경 밖의 기존52파일 형식 위반으로 FAIL이며 변경 파일 위반은0이다. 관련 Release 빌드와 앱 XAML 컴파일은 통과했다. 자동 검사 증거와 측정 원시는 [검증 묶음](data/primitive-detection-32k-validation.zip)에 보존한다.

## 성능 측정

기준 장비: Ryzen 5 5600H / RAM32GB / NVIDIA GeForce RTX3060 Laptop GPU6GB, Windows x64, Release .NET10. 고정 입력의 워밍업0회차와 이후3회 원시 JSON을 보존한다. 서비스 내부 시간은 원본 소스 열기·창 읽기·CPU/GPU 준비·검출·병합을 포함하며 반환 직전에 잰다. 반환 후 reader/GPU 해제와 WPF 결과 변환/목록/렌더/물리 scan-out은 포함하지 않는다. 따라서 전체 명령 지연의 달성 근거로 쓰지 않는다. CLI의 `totalMilliseconds`는 해제를 포함한다. `DetectionWorkMilliseconds`는 병렬 worker 시간의 합이므로 벽시계와 더하지 않는다. 소스 계측 래퍼는 선택형 직접 Gray8 인터페이스도 보존한다. 파일 캐시는 기존 캐시를 사용하며 OS 냉간 부팅 조건은 아니다.

합성32K는 **32,768×32,768=1,073,741,824픽셀 전체**를 읽는다. 정답 위치를 검색 ROI로 공급하지 않는다. 검사 중 타일의 독립 수식으로 픽셀을 생성하며 이 생성 시간도 읽기/총 시간에 포함된다. 기본 최대 크기256에서1,849개 원본 창, 여백 포함 약29.82억 픽셀을 평가한다. 2 Mpx/창과 최대4창 입력 버퍼를 유지한다.

| 32K 전체 입력 | 정답/검출 | CPU 중앙값(범위) | CPU+CUDA 중앙값(범위) | 위치·각도 |
|---|---:|---:|---:|---|
| 축 정렬 사각형 | 16,384/16,384 | 3.614초(3.580~3.887) | 4.981초(4.624~5.374) | 중심0px, 각도 수치 오차 수준 |
| 회전 삼각형·사각형·원·십자가 | 16,384/16,384 | 8.046초(7.891~8.118) | 10.270초(10.175~10.290) | 최대 축별 중심0.090143px, 사각형/십자가0.550657° |
| 도넛/원·사각 테두리/내부 사각형 | 32,768/32,768 | 8.943초(8.625~9.252) | 11.781초(11.364~11.825) | 중심0px, 작은32×22px 사각형 최대2° |

모든 최종 반복에서 누락/중복/종류 오류0, 잘림0, 출력 상한 경고 없음이며 CPU/GPU의 전체 결과 해시가 같다. 동일한 최종 사각형 알고리즘을 한 창씩 실행하면 중앙값6.904초(6.773~6.941), 네 창 병렬은3.614초로 약48% 감소했다. 이 비교는 동일한 점수·위치·각도·윤곽 해시를 유지한다. 혼합/중첩 입력은 픽셀 생성만 약3~4초대가 포함되어 단순 사각형 입력과 시간을 직접 비교하지 않는다.

관리 할당/프로세스 최대 working set(CPU/CUDA)은 사각형465.5/478.9MiB 및132.6/266.2MiB, 중첩2159.1/2172.5MiB 및490.0/715.3MiB였다. 관리 할당에는 서비스뿐 아니라 평가기의 정답 비교/전체 결과 해시 생성도 포함되며, working set은 프로세스 최대값이다. 이것을 GPU VRAM 사용량이나 검출기의 동시 배열 크기로 표현하지 않는다.

최종 전체 해시: 사각형 `E18C0F4EF0B80637AB9A7813B1211929292972705A502A574DAF019EF25C0FF6`, 혼합 `BB40A186F5F4245CD2CE5403D716B18E66D82D14A5499B5CAA07149F7A51E1D8`, 중첩 `E77B050C15BB1BC458848572BA8B3DC2849264913573DBABFCE2EC0E0E7A43BB`.

변경 전 결과 기본값은256개, 지정 가능한 최대값은1,000개였다. 비교 실행에서는 상한을1,000개로 지정해16,384개 중1,000개만 반환했고 중앙값 약5.52초였다. 상한만 제거한 직렬 버전은16,384개 전수·약7.49초, 할당 약6.10GB/회였다. 순수 최적화 단계는 같은 전체 결과 해시를 유지했고, 이후 정확도 수정은 의도적으로 점수/각도를 개선했으므로 최종 결과가 변경 전과 모두 같다고 주장하지 않는다. 원본 배열 직접 전달·최대4창 병렬·버퍼 재사용을 적용했다. RawLevel 맵 크기 확대 실험은 유의한 개선이 없어 원복했다.

GPU 시간에는 전송/동기화가 남는다. CUDA를 사용한다는 사실을 GPU 실가속 달성으로 표현하지 않는다. `ken32.png`는17,280×30,720이고 정답 미라벨이며 경계 잘림 경고를 유지한다. 도형 결과가 MLCC 템플릿 매칭의 전수 정답을 뜻하지 않는다.

최종 `ken32.png` 원본 전체 확인은 CPU/CUDA 모두159개(원73/사각형86), 잘림420개로 **불완전 경고**를 유지했다. 전체 결과 해시 `400FA8C28351AD12911E534730027CD82C746B451962EA298DDF48506E43AB2C`는 두 장치와 직전 구현에서 같다. 최종 코드 첫 호출 확인은 CPU9.078초/CUDA11.218초였으며 반복 중앙값으로 표현하지 않는다. CPU에서 원본 읽기가7.825초로 큰 비중이었다. 이 결과는 현장 정답 검출률이 아니다.

## 재현

```powershell
dotnet build tools/PrimitiveDetection.Evaluation -c Release --artifacts-path artifacts/primitive-validation
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3 --serial
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3 --mixed
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3 --hollow
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3 --hollow --gpu
dotnet artifacts/primitive-validation/bin/PrimitiveDetection.Evaluation/release/PrimitiveDetection.Evaluation.dll 32768 0 3 --ken32
```

`--gpu`는 각 입력과 조합 가능하다. `--hollow`는 도넛/원과 사각 테두리/사각형 총32,768개, `--mixed`는 채워진4종16,384개다. 출력은 종류·위치·점수·면적·각도·안팎 윤곽 길이/점을 포함한 SHA256과 누락 종류/각도 분포를 제공한다. 해시 형식을 확장하기 전 이력과 최종 해시는 직접 비교하지 않는다.

## 원칙 검토·남은 검증

| 원칙 | 적용 |
|---|---|
| SRP / 정보 전문가 / 높은 응집도 | extractor는 OpenCV/계층, classifier는 도형, service는 읽기·병합, VM은 옵션·결과 페이지 |
| OCP / 다형성 | 기존 CPU 경로와 선택형 median 공급자, 동일한 반환 계약 |
| LSP | CPU/CUDA의 같은 픽셀·마스크·소유권 계약을 실제 장치로 비교 |
| ISP / 낮은 결합도 | GPU 계약은 median과 진단뿐. Canvas에 알고리즘/CUDA 참조 없음 |
| DIP / 간접화 | 앱/CLI 조립 지점이 장치 팩토리를 주입 |
| Creator | 검사 작업이 reader/장치/버퍼를 생성하고 종료 시 해제 |
| Controller | VM은 서비스에 실행을 위임하며 View는 바인딩/입력만 담당 |
| Pure Fabrication | 기존 service·reader 경계를 재사용. 새 범용 프레임워크/외부 패키지 없음 |
| Protected Variations | 장치 실패·취소·입력 세대 변경·양수 상한을 명시적으로 처리 |

사용자의 Computer Use 재시도 요청에 따라 도형 검증 Release 빌드(`artifacts/primitive-validation`, 실행 파일 수정 시각2026-10-16 18:50 KST)를 실제 WPF/DX12 창에서 조작했다. 640×480 중첩 합성 PNG를 파일 선택창으로 열고 CPU/CUDA 각각 **도넛·내부 원·사각 테두리·내부 사각형 총4개**와 동일한 표시 위치/점수/면적을 확인했다. 도넛/사각 테두리의 안팎 닫힌 윤곽과 내부 독립 도형도 화면에 따로 표시됐다. 최대 결과 수0·최소 점수0.8을 확인했으며, CUDA에서 도넛만 끄면 내부 원을 포함한3개, 사각 테두리까지 끄면 내부 원/사각형2개가 남았다. 설정 변경 시 이전 결과를 지우고, 위치 표시를 껐다 켜도4개 결과를 보존했다. 종료 상태는 여섯 종류 선택·CPU·윤곽 표시 켬이다. [실제 UI 관찰 기록](data/primitive-ui-validation.json).

UI에 표시된 서비스 시간은 첫 CPU500.9ms/CUDA651.9ms, CPU 재실행6.2ms였다. 단일 호출 관찰이며 버튼→화면 표시 시간이나 반복 성능 통계로 표현하지 않는다. 이번 확인은 위 검증 빌드 기준으로, 이후 동시에 진행 중인 렌더러 변경은 포함하지 않는다. **32K 대량 결과의 버튼→표시 시간은 NOT_RUN**이다. 현장 라벨 기반 누락/오검출, 미세 각도 정밀도, 장시간/다른 GPU/카메라 FPS는 미검증이다.

참고: [OpenCV 윤곽 계층](https://docs.opencv.org/4.13.0/d9/d8b/tutorial_py_contours_hierarchy.html), [medianBlur 경계](https://docs.opencv.org/4.11.0/d4/d86/group__imgproc__filter.html), [ILGPU 버퍼 수명](https://ilgpu.net/docs/03-advanced/01-memory-buffers-and-views/).
