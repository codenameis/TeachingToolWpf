# 대량 ROI 반복 렌더·이동 개선 · 2026-10-02

1만~32,940개에서 앱이 버벅이는 문제를 CPU 단계별로 측정했다. ROI마다 장면을 한 번씩 게시하는 문제는 아니었다. **한 장면을 그릴 때마다 전체 윤곽 정점·이름표를 다시 만들고, 경계 이동에서는 전체 형상을 반복 복제**하는 비용이었다. [ADR-008](decisions/0008-retained-roi-rendering.md)대로 DX12 instance buffer 재사용과 불변 이동 preview를 적용했다. 구현 커밋은 `a15147f`, `b980a9b`다. CUDA는 사용하지 않는다.

## 조건과 읽는 방법

- Ryzen 5 5600H, RAM 32GB, RTX 3060 Laptop, DXGI VRAM 5.9GiB / 당시 budget 5.1GiB. Windows x64, .NET 10 Release, 디버거 미연결. 성능 실행은 DX12 debug off, 기능 smoke는 on이다.
- 8192×8192 합성 정적 이미지, 뷰포트 877.33×540 DIP / 1316×810 물리 px / DPI 150%, 화면 밖 실제 HWND. 카메라/8K 라이브 동시 부하가 아니다. 실제 모니터 표시 지연은 측정하지 않았다.
- 고정 배치: 평면 격자, PCB 부모+패드, 같은 형상이 완전히 겹치는 ROI. 정확한 생성기는 `RoiInteractionRun.Fixture`와 `RoiRenderRun`에 있다.
- 입력별 1초 대기, 선택 전환 후 300ms 대기, 미선택/전체 선택 각 180회 이동과 16ms `Task.Delay`. 로그 앞 2초 및 ROI 0개를 제외한다. 작은 pan은 ±30×20 DIP, wide는 ±600×300 DIP, zoom은 사인 곡선으로 기준 배율의 1/8~8배다.
- 기존 `6167d98` 렌더 경로는 각 배치 한 번, 최종 32,940개 평면 pan/wide/zoom은 각각 **3회** 실행했다. 다른 스트레스 배치는 한 번이다. B1의 10초 워밍업+60초×3 조건을 충족한 공식 수용 시험은 아니다. 기존 느린 렌더는 입력을 합치며 프레임을 건너뛰므로, 같은 180회 입력이어도 측정 프레임 수가 다르다. 기존 32,940개 미선택 pan은 12프레임, 최종 첫 반복은 180프레임이다.
- 백분위는 nearest-rank다. 초기 조사에서 짝수 표본의 상위 중앙값을 사용했던 약 292ms는 이 집계의 p50 207.24ms와 정의가 다르다. 전후 표는 일관된 집계의 p95를 사용한다.

`paint`는 CPU 오버레이/이름표 준비, `submit`은 CPU draw 명령/instance cache 준비다. 전체 렌더 CPU 시간은 fence 대기·이미지 준비까지 포함한다. GPU는 timestamp query다. 이 값을 FPS나 물리 포인터→모니터 지연으로 바꾸어 부르지 않는다. 정지 상태의 추가 이름표 준비 프레임도 기록에 포함한다.

[반복별 집계 CSV](data/retained-roi-render.csv), [원시 CSV/JSON/캡처 묶음](data/retained-roi-measurements.zip)에 표본 수·p50/p95/p99·할당·GPU·프레임 간격·private bytes가 있다.

## 작은 pan 전후

아래는 **paint p95 ms**다. 32,940개 평면의 최종 값은 3회 중 가장 느린 값이며 다른 셀은 한 번의 측정이다.

| ROI / 배치 | 미선택 기존 → 최종 | 전체 선택 기존 → 최종 |
|---|---:|---:|
| 10,000 평면 | 206.27 → 0.15 | 81.11 → 2.30 |
| 10,000 PCB | 91.17 → 0.22 | 83.77 → 2.46 |
| 10,000 완전 중첩 | 33.87 → 0.06 | 20.09 → 0.03 |
| 32,940 평면 | 482.52 → 0.40 | 158.47 → 2.56 |
| 32,940 PCB | 273.35 → 0.17 | 180.87 → 2.39 |
| 32,940 완전 중첩 | 97.77 → 0.07 | 80.19 → 0.03 |

32,940개 평면의 미선택 평균 렌더 할당은 약 **32.31MiB/프레임 → 33.9~35.2KiB/프레임**, 전체 선택은 약 **31.67MiB → 27.4~27.8KiB**다. 초기 업로드 후 작은 pan 구간의 ROI instance 재업로드는 0바이트였다. 같은 데이터의 선택 색과 카메라 상수만 바꾼다.

전체 렌더 CPU p95는 미선택 519.18ms → 0.73~0.78ms, 전체 선택 203.80ms → 13.75~14.13ms였다. 전체 선택의 GPU p95는 48.25ms → 8.12~8.26ms였다. CPU paint가 작아져도 GPU 실행·fence·Present 시간이 사라지는 것은 아니다.

## 큰 pan·연속 zoom

작은 이동 캐시만으로 결과를 좋게 보이지 않도록 재배치와 LOD 변경이 반복되는 경로를 추가했다. 중간 구현에서는 zoom의 paint p95가 177.87ms였고, 이름표 전체 후보 조회/텍스처 생성이 원인이었다. 화면 밀도 후보 검색, 텍스트 캐시, 프레임별 생성 예산을 적용한 결과다.

| 32,940 평면 / 3회 범위 | 미선택 paint p95 ms | 전체 선택 paint p95 ms |
|---|---:|---:|
| wide pan | 3.92–4.00 | 2.81–2.84 |
| 연속 zoom | 6.09–6.60 | 3.96–4.34 |

이름표는 조밀할 때 대표 후보/겹침 제거를 적용하고, 새 일반 이름표는 점진적으로 준비한다. 단일 선택 이름표와 선택 윤곽은 즉시 표시한다. 모든 이름은 목록/속성에서 읽을 수 있다. 도메인 ROI, 필터, 선택 ID를 삭제하거나 검사 정확도를 낮추지 않았다.

## 드래그와 편집 명령

경계에 걸린 전체 이동을 따로 재현했다. 1만 개, 같은 방향 5회 입력에서 기존 이동 경로는 입력당 **548–993ms**, 평균 **320.18MiB**를 할당했다. 40번의 경계 탐색에서 전체 형상과 불변 사전을 반복 만들었다. 수정 후 첫 제한 계산은 1.61ms/5,112바이트, 뒤의 동일 제한 위치 반복은 0.02–0.04ms/88바이트였다. 뒤의 값은 실제 위치가 바뀌지 않는 경우이므로 일반 이동 성능으로 과장하지 않는다.

실제 계속 움직이는 검증은 격자를 이미지 중앙 절반 크기로 배치하고 매번 다른 위치로 100회 이동했다. UI 호출과 `UpdateLayout`을 포함하며 DX12 debug on이었다. 첫 준비 입력을 제외한 값이다.

| 전체 선택 수 | 입력 p50 / p95 ms | 평균 UI 할당 / 입력 | 마우스 놓기 확정 |
|---|---:|---:|---:|
| 10,000 | 0.25 / 0.48 | 약 3.3KiB | 70.38ms |
| 32,940 | 0.34 / 0.59 | 약 3.6KiB | 221.63ms |

확정·Undo·Redo의 ROI 값 동일성, 취소 후 문서 보존을 검사했다. 별도 최종 GPU 캡처 실행의 32,940개 확정은 199.28ms였다. **확정 시 약 200ms 지연은 남아 있다.** 이동 중 빠르게 보이도록 이력 생성이나 계층 재판정을 생략한 것이 아니다.

등록 필터가 많아도 UI가 매 샘플 전체 형상을 복제하지 않도록 필터 요청에 같은 불변 preview를 전달한다. 자동 테스트는 등록 ROI 1만 개의 50회 preview 갱신에서 UI 할당이 1MiB 이내인지, 원위치 종료 시 필터 버튼이 다시 활성화되는지 확인한다. 필터 연산 자체는 기존 단일 작업자/100ms 요청 합치기/약 4Mpx 미리보기 제한을 유지한다.

## 10만·100만 탐색과 남은 지연

| 평면 ROI / 작은 pan | 미선택 paint p95 | 전체 선택 paint p95 | GPU p95 (미선택/선택) | private peak |
|---|---:|---:|---:|---:|
| 100,000 | 0.34ms | 2.19ms | 8.13 / 8.86ms | 370MiB |
| 1,000,000 | 2.86ms | 2.33ms | 7.57 / 7.38ms | 1,259MiB |

100만 개 전체 보기에서는 작은 집합을 요약해 실제 GPU instance 최대 38,078개를 사용했다. **백만 개를 모두 개별 선으로 그린 결과가 아니다.** 원본 ID·정확한 형상은 보유하며 확대 시 개별 도형으로 전환한다. 이 실행의 가시 ROI 캐시 예산 계상은 276.14MiB(실제 GPU allocation + 유지되는 CPU ROI 추정)였다. 가시 버퍼를 보호하므로 비활성 캐시 목표 128MiB를 넘을 수 있으며 순수 VRAM 값이 아니다.

전체 보기 pan이 빨라져도 모든 명령의 지연이 해결된 것은 아니다. 32,940개 분산 위치 선택 시험에서는 목록 페이지 전환을 포함한 평면 선택 p95 26.69ms, PCB 11.96ms였다. 같은 실행의 전체 선택은 평면 118.30ms/PCB 31.28ms, 삭제·Undo/Redo·이미지 상태 복원은 약 18–173ms였다. 이 시험은 페이지 전환이 적은 기존 선택 시험과 입력 조건이 다르므로 전후 배속을 계산하지 않는다. 동일한 기존 위치 순회 재측정도 원시 묶음의 `roi-32940-final-local.json`에 보관했다.

수백만 초기 생성/계층 구축, 전체 선택·삭제·복원, 대량 회전/resize, 실제 PCB의 복잡한 중첩, 8K 라이브+대량 필터 동시 부하, 혼합 DPI/10분 이상 안정성은 별도 과제다. 이번 결과는 모든 기능의 무지연 또는 수백만 ROI의 60FPS 보장이 아니다.

## 기능 검증과 재현

Core 68 + Imaging/WPF 38 통과, Release 경고/오류 0. 실제 RTX/DX12 debug에서 선택 핸들·한국어 태그·OpenCV Binarize/Edge·이미지 교체/Undo·8K/24K/라이브 미니맵·렌더 재연결을 확인했다. 새 레이블 캐시는 연속 줌에서 254회 제거를 거쳤고, 최종 512개/32MiB로 정리되며 대기 이름표가 완료된 후 캡처했다. 이동 중 장면/취소 캡처도 확인했다. 물리 마우스 전체 E2E를 새로 수행한 것은 아니다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test TeachingToolWpf.slnx -c Release --no-restore
& ./bin/Release/net10.0-windows/TeachingToolWpf.exe --roi-render-benchmark --rois 32940 --layout flat --motion wide --report artifacts/render.json --render-profile artifacts/render.profile.csv --metrics artifacts/render.frames.csv
& ./bin/Release/net10.0-windows/TeachingToolWpf.exe --roi-drag-benchmark --rois 32940 --interior --capture-drag --gpu-debug --report artifacts/drag.json
& ./bin/Release/net10.0-windows/TeachingToolWpf.exe --roi-render-benchmark --rois 32940 --motion zoom --capture-labels --gpu-debug --report artifacts/labels.json --render-profile artifacts/labels.profile.csv
./scripts/Summarize-RoiPerformance.ps1 -Profiles artifacts/render.profile.csv -Output artifacts/render-summary.csv
```

`--layout`은 flat/pcb/overlap, `--motion`은 pan/wide/zoom이다. `--interior`를 빼면 경계 제한 이동 5회+취소 시험이다. 진단 플래그가 없는 일반 실행에는 CSV 계측을 켜지 않는다. 보관한 원시 묶음을 `artifacts`에 풀고 기본 집계 스크립트를 실행하면 공개 표를 다시 계산할 수 있다.
