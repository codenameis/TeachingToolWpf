# Sub-NCC 축소 위상 회귀 데이터

8K 전체 데이터의8개 물체 주변을4픽셀 정렬로 잘랐다. 두 번의2×2 평균이 전체 영상과 같은 위치 위상을 갖는다. 정답과 고정 조건은 `cases.json`에 있다. 점수0.75/후보2048 그대로 CPU/CUDA의 누락·오검출·위치·각도·배율을 비교한다.

이 작은 회귀 시험은8K 전체 검색을 대신하지 않는다. 전체 범위는 [FullImage](../CpuMatching.FullImage/README.md)에서 별도 검증한다. CLI 재현:

```powershell
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe run samples/CpuMatching.PhaseValidation artifacts/phase-check.json all 1 compare
```
