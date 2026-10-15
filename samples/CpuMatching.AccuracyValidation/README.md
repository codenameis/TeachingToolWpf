# 수정 후 추가 CPU 정확도 검증

Kensa89×52 부품 crop을 독립 OpenCV WarpAffine로 합성한3개 장면이다. Chamfer 보정을 마친 뒤 생성했고 이 세 장면에 맞춰 점수/상수를 다시 조정하지 않았다. 같은 원본 템플릿을 공유하므로 독립 현장 촬영 데이터나 범용 검출률 데이터로 해석하지 않는다.

|장면|조건|
|---|---|
|blurred-dim-two|512×512, -23.4°/1.05배와12.7°/0.95배 두 대상, Gaussian3×3·sigma0.65, 밝기0.75배+10, seed20261017의±4 잡음|
|smaller-rotated|256×256,11.4°/0.8배 한 대상|
|8k-local-two|8192×8192 실제 PNG,7.6°와-8.4° 두 대상, 검색 범위는(5888,3968,512,512)|

**8K 전체 검색 시험이 아니다.** 큰 영상의 원본 좌표를 보존하면서 제한 영역을 검사한다. 각도±30°/10°, 배율0.75~1.25/0.1, 최소 점수0.75를 사용한다. 위치1px·각도1°·배율0.03 이내, 누락/오검출0과 예산 잘림 없음이 통과 조건이다. 정답은 `cases.json`의 픽셀 인덱스 좌표다.

```powershell
dotnet run --project tools/CpuMatching.Evaluation -c Release -- run samples/CpuMatching.AccuracyValidation artifacts/cpu-accuracy-validation.json all 3
```

재생성은 `accuracy-validation <새 폴더> kensa.png`로 한다. 기존 폴더를 덮어쓰지 않는다. CPU3종의 완전 정답 장면이 실패하면 평가 도구가 종료 코드2를 반환한다. OpenCV 비교 실패는 별도 행에 기록한다. [실측·한계](../../docs/cpu-matching-accuracy.md).
