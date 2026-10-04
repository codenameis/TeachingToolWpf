# Shape matching 구현 검증 기록

최신 후속(2026-10-04): OpenCV 기본 템플릿 매칭 추가와 큰 ROI 검색 구조 변경의 8K 반복 실측은 [별도 기록](matching-performance-2026-10-04.md), 원문 대조/설계는 [ADR-016](decisions/0016-template-matching-and-pyramid-search.md)에 있다. 아래 소형 benchmark와 scalar 기본값 설명은 변경 전 당시 결과로 보존한다.

기록 시각: 2026-10-04 (KST). 설계와 논문 대응은 [구현 계획](../tasks/pattern-matching-plan.md), 현재 상태는 [진행 목록](../tasks/todo.md), 결정은 [ADR-013](decisions/0013-vision-algorithm-and-inspection.md)을 따른다.

같은 날 검사 표시 후속: 640×400 합성 PNG 파일을 실제 reader로 학습/검사하고 WPF 명령·특징점 체크박스·DX12 결과 픽셀을 검증했다. 네 가지 판정, 결과 수명, 371개 전체 테스트와 증거는 [ADR-015](decisions/0015-inspection-result-display.md) 및 todo의 검사 표시 항목을 따른다. 아래 표는 최초 알고리즘 구현 당시 기록이며, 물리 포인터 수동 시험과 현장 8K/24K 검증은 여전히 NOT_RUN이다.

## 빌드와 테스트

| 검증 | 결과 |
|---|---|
| `pwsh -File scripts/Format-Code.ps1 -Verify` | **PASS**, 종료 코드 0 |
| `dotnet build TeachingToolWpf.slnx -c Release --no-restore` | **PASS**, 경고 0 / 오류 0. Windows SDK 메타데이터 읽기 권한 검토 후 실행 |
| `dotnet test TeachingToolWpf.slnx -c Release --no-build --no-restore` | **PASS**, 총 331/331: VisionAlgorithm 25, Core 174, Imaging/WPF 132. skip 0 |
| `TeachingToolWpf.exe --smoke --gpu-debug` | **PASS**, 종료 코드 0. 기존 DX12/ROI smoke이며 신규 패널 버튼을 화면에서 조작한 smoke는 아님 |
| WPF 오른쪽 패턴 패널 수동 조작 | **NOT_RUN**. 현재 CUA 세션은 브라우저만 제공해 WPF native 창에 입력할 수 없음 |
| 실촬영/실제 검사 자료의 8K·24K 정확도, 지연, 메모리 | **NOT_RUN**. 평가 영상과 정답 라벨이 없음 |

## Release CPU 실측

원시 데이터: [shape-matching-benchmark-2026-10-04.json](data/shape-matching-benchmark-2026-10-04.json). 실행 명령:

```powershell
dotnet run --project .\benchmarks\VisionAlgorithm.Benchmarks\VisionAlgorithm.Benchmarks.csproj -c Release --no-restore -- .\docs\data\shape-matching-benchmark-2026-10-04.json
```

환경은 AMD Ryzen 5 5600H, Windows 10.0.26200, .NET 10.0.12, 12 논리 프로세서다. 같은 641×385 Gray8 합성 양성 장면(대상 3개)과 일정 밝기 음성 장면을 사용했다. 주력 모델은 160/85/34개 피라미드 특징, 52개 회전·배율 템플릿이다. 주력 구성은 워밍업 3회와 측정 5회, 비교 경로는 비교용 6개 템플릿 설정 또는 BOLD 11개 선분 모델로 워밍업 1회/측정 3회다. 비교 경로의 위치 검출 판정은 정답 중심에서 10px 이내다. p95는 3회/5회 표본의 최댓값이다. 전체 benchmark 프로세스 peak working set은 약 207.4 MiB이며 E1 거리맵 비교 실행도 포함한다.

주력 LINE-2D 계열 결합 경로 결과:

| 경로 | 평균 / p50 / p95 (ms) | 평균 할당량 | 검출 / 위치 오차(px) | 최대 각도 / scale 오차 | 양성 FP / 음성 검출 |
|---|---:|---:|---:|---:|---:|
| 방향 후보만 | 36.84 / 36.68 / 39.45 | 11.24 MiB | 3/3, 2.843·2.025·2.640 | 2.968° / 0.0600 | 0 / 0 |
| 연속 검증 | 84.37 / 83.47 / 89.23 | 11.15 MiB | 3/3, 0.206·0.285·0.180 | 0.470° / 0.02875 | 0 / 0 |
| 스칼라 정밀화 | 103.72 / 103.24 / 105.03 | 17.44 MiB | 3/3, 0.003·0.001·0.004 | 0.0102° / 0.000254 | 0 / 0 |
| SIMD 정밀화 | 108.51 / 105.21 / 118.17 | 17.44 MiB | 스칼라와 동일 pose | 0.0102° / 0.000254 | 0 / 0 |
| 그룹화 정밀화 | 103.63 / 103.42 / 106.53 | 17.67 MiB | 스칼라와 동일 pose | 0.0102° / 0.000254 | 0 / 0 |
| 2-way 검증 정밀화 | 96.20 / 96.39 / 97.93 | 17.44 MiB | 스칼라와 동일 pose | 0.0102° / 0.000254 | 0 / 0 |
| tiled preprocessing 정밀화 | 112.34 / 110.94 / 125.16 | 23.75 MiB | 스칼라와 동일 pose | 0.0102° / 0.000254 | 0 / 0 |

E1/E2/E3 비교 측정:

| 비교 경로 | 학습(ms) | 평균 / p50 / p95 (ms) | 검색 할당량 | 검출 / 위치 오차(px) | 양성 FP / 음성 검출 |
|---|---:|---:|---:|---:|---:|
| E1 directional chamfer | 25.05 | 605.96 / 593.93 / 639.50 | 128.76 MiB | 2/3, 0.003·0.001·미검출 | 0 / 0 |
| E2 gray-gradient fusion | 10.28 | 382.33 / 384.58 / 387.15 | 12.64 MiB | 3/3, 0.003·0.001·0.004 | 0 / 0 |
| E3 BOLD | 21.51 | 19.83 / 19.84 / 20.20 | 23.99 MiB | 3/3, 1.747·3.303·5.511 | 0 / 0 |

BOLD는 이 합성 장면에서 11개 model segment, 49개 scene segment, 44개 descriptor correspondence와 최대 consensus 8개를 기록했고 가설/scene budget 초과는 없었다. 이 비교기는 주력 경로의 point-to-line 최종 정밀화를 하지 않으며 결과 scale 최대 오차는 0.1307, 최대 각도 오차는 3.881°였다. 주력 정밀화 경로보다 빨랐지만 같은 품질이라고 볼 수 없다. E1은 2개 대상만 반환했고, E2와 BOLD는 모두 찾았다. 이 표본 하나로 일반 검출률이나 운영 처리량을 추정하지 않는다.

이 fixture에서 SIMD, template grouping, tiled preprocessing은 scalar보다 빨라지지 않아 기본 경로로 선택하지 않았다. 비교 E1은 약 0.61초, E2 약 0.38초, BOLD 약 20ms였다. E1/E2/E3는 각각 OpenFDCM의 FDCM 원문, Winograd FNM, BOLD 원문 전체 구현이 아니다. 현재 비교 로직과 용량 제한은 [Comparisons 안내](../src/VisionAlgorithm/OpenSrc/Comparisons/README.md)를 참고한다.

## 정확성·통합 한계

새 회귀 테스트는 회전 학습 ROI에서 이미지 기준 절대 각도 반환, 희소 edge에서 학습 mask 외접 경계 보존, BOLD descriptor의 회전/이동/배율 불변성, BOLD 단일/다중 인스턴스 및 음성 장면, 3개 인스턴스 benchmark 장면 검출, 취소·pixel budget을 포함한다. 전체 자동 테스트는 총 331개 통과했다.

알고리즘 입력은 Gray8 원본 픽셀이다. 앱 어댑터는 Gray16을 고정 상위 8bit, RGB(A)는 고정 luminance로 변환하며 이미지별 자동 보정은 하지 않는다. 주력 앱 matcher는 최대 2 Mpx window 단위로 큰 파일을 처리한다. BOLD와 E1 비교기는 최대 262,144px 입력 제한이 있어 8K·24K 전체 영상을 처리하지 않는다. 이 benchmark는 모두 641×385 합성 입력이며 실촬영 결과가 아니다.

SOLID/GRASP 리뷰에서 matcher/trainer, BOLD descriptor/pose estimator, 앱 원본 픽셀 어댑터, ViewModel, 읽기 전용 renderer marker의 책임과 의존 방향을 확인했다. 비교 경로는 앱의 기본 검사 UI에 결합하지 않는다. 상용 Cognex/HALCON SDK, 원 코드 포팅과 결과 비교, RTX adapter 기록, WPF 패널 수동 입력은 수행하지 않았다. 미구현/미검증 항목을 통과로 표시하지 않는다.
