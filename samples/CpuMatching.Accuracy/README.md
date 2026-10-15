# Kensa 제한 영역 정확도 회귀

원본 `kensa.png`(1024×1536)의 변형 없는 crop이다. `template.png`는 원본(156,158,89,52) 메모리 부품이다. `kensa-parts.png`는 원본(17,1,367,366), `kensa-other-components.png`는 원본(120,270,220,100)이다. 첫 영상의 검색 기준점은 crop 안의(56,55,256,256)으로 제한한다. 이 여백은 회전한 템플릿의 원본 검증을 위한 WPF 읽기 영역과 대응한다.

`cases.json`은 픽셀 인덱스 중심/시계 방향 각도/배율을 저장한다. WPF 좌표는 각각 X/Y에0.5를 더한다. `kensa-parts-truth.png`의 네 사각형은 수작업으로 확인한 같은 부품이며 첫 중심은 정확한 학습 crop, 나머지는 약±1px의 수작업 정답이다. 다른 부품 영상에는 대상 메모리 패키지가 없다. `GroundTruthComplete=true`는 이 두 제한 영역의 대상 개수에 대한 표시이며 현장 전체의 정밀 정답 보장이 아니다.

공통 점수0.75, 각도±30°/간격10°, 배율1, 후보2048, NMS31.2px를 유지했다. Chamfer 수정 전 양성8개(오검출4)/음성4개, 수정 후 세 방식 모두 양성4개/음성0개다. 이것은 결함 유형 분류나 제품 OK/NG 시험이 아니다.

저장소 루트에서 실행한다.

```powershell
dotnet run --project tools/CpuMatching.Evaluation -c Release -- run samples/CpuMatching.Accuracy artifacts/cpu-accuracy-local.json all 5
```

재생성은 `accuracy-data <새 폴더> kensa.png`로 한다. 기존 폴더는 덮어쓰지 않는다. [보정 원인·전체 검증·성능 한계](../../docs/cpu-matching-accuracy.md).
