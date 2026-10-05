# 두 형상 방식의 정합성·속도 재검증

2026-10-04~05. 기준 장비와 기존 의존성은 AGENTS.md를 따른다. 현재 사용자의 실제 입력은 확보되지 않았으며 앱이 닫힌 상태에서 조사했다. 아래는 원본 PNG를 열어 검사한 통제 합성 입력이다.

## 실패와 수정

8192×8192 Gray8 PNG의 512px 학습 ROI에 배경 블록 잡음 ±20, 동일한 대상 16개의 회전 0/17/−23/31도와 배율 변화가 있다. 이전 코드에서 두 방식 CPU1/4 모두 원본 후보 16개가 올라왔지만 최종은 1개였다. 후보·출력 예산 경고는 없었다. 증거: `artifacts/shape-regression-before.json`, `artifacts/shape-correlated-noise-regression-red.log`.

넓은 약한 배경이 학습 특징을 차지하는 문제를 학습 대비 후보 선택으로 수정했다. 추가 윤곽 감점은 gradient 에너지와 대응 윤곽의 조명비를 사용하며 기존 전체 윤곽의 개수 제한은 보존했다. 저대비 실제 세부도 제외할 수 있으므로 비율을 낮추거나 0으로 재학습하는 조정과 도움말을 제공한다. [결정과 한계](decisions/0025-shape-contrast-and-clutter-energy.md).

## 검증 상태

- PASS: 초기 집중 64개. 회전·배율·위상·얇은 윤곽 144조건, 정답8/오답88 장면, 두 방식 잡음·명암8건, 추가 윤곽2건, 자기일치 원시 개수 제한, 대비0 세부 보존 및 기존 품질 회귀. `artifacts/shape-quality-tests/shape-quality-median-gain.trx`.
- PASS: 국소 결함 밝기·작은 밝은 결함·마스크 구멍·마스크 밖 근접선 회귀. 캐시 실험을 포함한 첫 전체778개 PASS 후, 효과 없는 캐시를 원복하고 최종 suite를 재검증 중이다.
- PASS: 실제 원본 PNG 두 사례 × 두 방식 × CPU1/4 × 첫1/반복3회. 최종32회 모두 완전 검색, CPU1/4 점수·자세·반환 순서 정확 일치. 회전 잡음의 중복·누락·오검출0, 최대 위치오차 .054567px/각도 .006467도/배율 .00006762. [전후 요약](data/shape-regression-comparison-20261005.json), [이전32회](data/shape-regression-before-20261005.json), [최종32회](data/shape-regression-after-20261005.json).
- 진행 중: 새 배포본 통합 smoke와 실제 OS 앱 화면.
- NOT_RUN: 사용자의 문제 이미지, 실촬영 검출률, 실제 Linux 실행, 이번 수정의 24K 비교와 실시간 FPS. 이전 작은 핀의 24K 측정으로 이번 검증을 대체하지 않는다.

## 전체 검사 시간

동일 설정에서 첫 검색은 따로 남기고 반복3회 중앙값을 비교했다. 파일 생성·가져오기·학습을 제외하고 검색 호출의 원본 읽기·변환·계산·해제를 포함한다. GPU는 사용하지 않았다. 단위 ms.

| 입력 | 방식 | CPU | 이전 | 최종 | 검출 수 이전→최종 |
|---|---|---:|---:|---:|---:|
| 칩1600 ROI / 8K | 논문 | 1 | 2558.1 | 2500.6 | 16→16 |
| 칩1600 ROI / 8K | 논문 | 4 | 2201.4 | 2174.2 | 16→16 |
| 칩1600 ROI / 8K | 기하 | 1 | 2535.7 | 2470.3 | 16→16 |
| 칩1600 ROI / 8K | 기하 | 4 | 2250.4 | 2169.2 | 16→16 |
| 회전·배율·잡음512 ROI / 8K | 논문 | 1 | 1789.9 | 1811.1 | 1→16 |
| 회전·배율·잡음512 ROI / 8K | 논문 | 4 | 1242.6 | 1228.3 | 1→16 |
| 회전·배율·잡음512 ROI / 8K | 기하 | 1 | 1805.8 | 1819.0 | 1→16 |
| 회전·배율·잡음512 ROI / 8K | 기하 | 4 | 1271.5 | 1227.5 | 1→16 |

전체 시간은 사실상 유지 수준이다. CPU1 잡음 입력은 약1% 더 느리며 새 검증·정밀화가 16개 대상으로 수행된다. 일부1~4% 차이를 큰 가속으로 주장하지 않는다. 후보 단계도 기존과 거의 같다. 이번 수정은 검출 회복이며 고성능 목표나 실시간 검사 달성으로 표시하지 않는다.

변환 캐시 실험은 전체 시간 이득이 재현되지 않아 제품과 전용 중복 테스트를 원복했다. [유효한 강제 Rebuild 비교](data/shape-verifier-cache-rejected-20261005.json)의 off/on 결과는 정확히 같았다. 초기 off/on SHA256가 동일한 실험은 `artifacts/shape-verifier-invalid-experiment.json`에 무효로 남겼다.

## 재현 방법

`TeachingToolWpf.Matching.Benchmarks`의 `shape-regression` 실행은 원본 PNG 생성·가져오기·학습과 검색 시간을 분리한다. `FIRST_SEARCH_FOR_SETTING`은 이미 가져온 파일과 준비된 모델에서 해당 설정의 첫 검색이며 OS 캐시가 비어 있는 최초 디코드 시간이 아니다. `WARM_SEARCH`는 같은 reader와 모델에서 반복한 검색이다.

```powershell
dotnet benchmarks/TeachingToolWpf.Matching.Benchmarks/bin/Release/net10.0-windows/TeachingToolWpf.Matching.Benchmarks.dll artifacts/shape-regression-after.json shape-regression 3 gray8-chip1600,gray8-rotation-noise512
```

입력16개와 출력의 위치·각도·배율을 일대일 대응한다. 위치1px·각도1도·배율.01 기준이며 중복·미할당을 통과로 숨기지 않는다. PCB 반복 대칭 형상은 위치만 검증하므로 회전 잡음 입력과 구별한다. 병렬 단계 진단 합은 겹치는 worker 경과 시간이며 전체 wall time의 분해가 아니다.
