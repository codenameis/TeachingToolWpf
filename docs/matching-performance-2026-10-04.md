# 패턴 매칭 개선 실측 — 2026-10-04

사용자 재현 설명: ROI 하나 추가 후 학습·검사, ROI는 512 이상 추정. 입력은 실제 현장 영상 대신 앱 기본 8192×8192 ProceduralTileSource다. CPU AMD Ryzen 5 5600H, Windows x64, .NET 10 Release, 동일 원본 reader, 전체 이미지 검색, 모델 특징 최대 160, 형상 회전 편차 ±180°/요청 간격 10°, 배율 1, 최종 점수 .72/지지율 .6/최대 16개다. 원본 ROI 중심은 (4096,4096)이다. OpenCV는 같은 ROI의 고정 방향·크기 검색이다.

시간은 reader 열기부터 전체 검색 완료까지로 학습/화면 갱신을 제외한다. 각 경로는 새 프로세스에서 연속 3회 실행하며 별도 워밍업 없이 첫 실행도 기록했다. 경로들은 동시에 실행하지 않았다. peak working set은 해당 측정 프로세스 전체의 값이다. 원본 생성/읽기와 GC 비용을 포함한다.

| 경로, 512×512 ROI | 1 / 2 / 3회 (ms) | 중앙값 (ms) | 전처리 중앙값 (ms) | peak working set | 검출 / 잘림 |
|---|---:|---:|---:|---:|---|
| 변경 전 형상 | 30,422 / 30,830 / 30,966 | 30,830 | 20,479 | 394.4 MiB | 매회 9 / 없음 |
| 개선 형상 | 3,455 / 3,258 / 3,195 | 3,258 | 943 | 136.3 MiB | 매회 9 / 없음 |
| OpenCV CCOEFF_NORMED | 2,855 / 2,776 / 2,807 | 2,807 | 별도 수집 안 함 | 264.4 MiB | 매회 9 / 없음 |

이 입력에서 형상 검색 중앙값은 약 **9.46배**, **89.4% 감소**했다. 전후 검출 중심 9개는 모두 같은 원본 위치이며 최대 중심 차이는 0.001px 미만이다. 개선 형상은 축소 검색 창 16개와 원본 후보 crop을 처리한다(이전 원본 검색 창 225개). OpenCV는 템플릿 top-left 위치를 나눈 16개 창을 처리한다. 두 방식은 회전·배율 지원과 점수 의미가 달라 기능이 같은 비교가 아니다.

추가 1회 시험은 형상 768×768 ROI **5,004ms / 9개 / 잘림 없음 / 173.8MiB**, 기본 ROI 추가 크기에 해당하는 2621×1966 ROI **5,945ms / 9개 / 잘림 없음 / 990.3MiB**였다. 후자의 학습은 492ms다. 큰 ROI에서는 원본 정밀화 crop/GC가 커져 메모리가 증가한다. 두 추가 사례는 단일 실행이므로 중앙값이나 보장 지연으로 표현하지 않는다.

## 재현과 원시 자료

```powershell
dotnet build benchmarks/TeachingToolWpf.Matching.Benchmarks -c Release
dotnet benchmarks/TeachingToolWpf.Matching.Benchmarks/bin/Release/net10.0-windows/TeachingToolWpf.Matching.Benchmarks.dll artifacts/shape.json 8192 512 3
dotnet benchmarks/TeachingToolWpf.Matching.Benchmarks/bin/Release/net10.0-windows/TeachingToolWpf.Matching.Benchmarks.dll artifacts/opencv.json 8192 512 3 512 opencv
```

변경 전 바이너리는 수정 전에 `artifacts/matching-baseline-bin`에 별도로 빌드했다. 현재 소스 재빌드는 개선 경로를 측정한다. 원시 자료: [변경 전](data/matching-baseline-8192-512.json), [개선 형상](data/matching-optimized-8192-512.json), [OpenCV](data/matching-opencv-8192-512.json), [768 ROI](data/matching-optimized-8192-768.json), [기본 크기 ROI](data/matching-default-roi.json).

## 정확도와 검증 범위

- 원본 level-0 타일만 읽는 reader의 홀수 크기/stride/타일 경계 축소 값이 학습 피라미드와 일치한다.
- 큰 패턴의 비정수 이동·샘플 중간 각도 3.4°/-6.3°·배율 1.03 검출을 독립 역변환 합성 영상으로 검증했다. 중심 오차 <0.5px, 각도 <0.3°, 배율 <0.005 기준을 유지했다.
- 2049×1153 원본의 큰 패턴 두 개와 지름 2px 검색 원에서 최종 원본 좌표/실제 원 포함 조건을 확인했다.
- OpenCV 마스크 밖 명암 변화, flat template 거부, NaN/Inf 응답 제외, stride, 취소, 큰 backing memory의 작은 view, 창 경계 NMS를 확인했다.
- 회귀 테스트 변이: OpenCV 창별 후보 수집을 창별 NMS로 되돌리면 경계 테스트가 예상 2개/실제 1개로 FAIL. 즉시 복원했다. 로그 `artifacts/matching-mutation.log`.
- 독립 코드 리뷰에서 내부 seed 잘림도 경고로 보고하도록 수정했다. 형상/상관 후보·원본 정밀화 입력에는 상한이 있으며 native 호출 도중 자원을 먼저 반환하지 않는다.
- 전체 385개 회귀 테스트와 C# 스타일 Verify가 PASS다. 실제 GPU smoke의 640×400 PNG는 두 형상을 검출했고 OpenCV는 학습 방향/크기와 같은 한 개를 찾았다. 형상 두 번째 대상은 배율 상한 1.2에서 정밀화 Rejected로 반환되어 사용자 확인 색을 유지한다. 검출을 정밀화 성공으로 바꾸지 않았다. 위치 오차는 0.18px 미만, 각도 오차는 약 0.16°였다.

전체 테스트·스타일·배포 GPU 검증의 최신 상태는 [todo](../tasks/todo.md)의 이번 항목에 기록한다. 논문 대조와 입력 예산은 [ADR-016](decisions/0016-template-matching-and-pyramid-search.md)을 따른다.

실제 사용자 영상의 검출률, 실촬영 8K/24K 처리 시간, 실시간 검사 FPS, 상용 도구 비교는 **NOT_RUN**이다. 이번 수치는 합성 샘플 한 종류의 재현 결과다. 기존 표시 성능/필터 미리보기/과거 소형 matcher benchmark와 구분한다.
