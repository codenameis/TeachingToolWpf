# Kensa 전체·기본8K 반복 모듈 데이터

`module-data <새 폴더> kensa.png`로 생성한다. 원본 Kensa는1024×1536이며8K는 앱의 독립 합성 원본이다. 둘을 같은 촬영 이미지로 표현하지 않는다.

입력: [Kensa 원본](kensa.png) · [8K 원본](8k-modules.png) · [Kensa 템플릿](kensa-template.png) · [핀 템플릿](pin-template.png). [검색 조건과 정답](cases.json), [Kensa 독립 자세 기준](kensa-reference.json), [기준 출처](reference-provenance.json).

| 사례 | 입력 | 대상 | 기준 |
|---|---|---:|---|
| kensa-full-modules | kensa.png |48| 수동 식별한 부품 주변의 독립 OpenCV ECC 정합 |
| 8k-full-modules | 8k-modules.png ·8192×8192 |256| 기존 ProceduralTileSource 생성기 정의 |

Kensa 기준은 모든 부품의 위치·각도를 포함하지만 교정 장비로 측정한 물리 정답은 아니다. `reference-provenance.json`에 원본 해시와 정합 설정이 있다. 기준 좌표는 검색 함수에 전달하지 않고 완료 뒤 비교한다. `kensa-reference.json`의 좌표는 WPF 기준, `cases.json`은0.5px를 뺀 라이브러리 픽셀 인덱스 기준이다.

점수0.75,각도-30~30°/10°,배율1,피라미드3,출력256,후보 묶음2048을 사용한다. 한 묶음을 넘는 후보도 모두 검사한다. 회전·배율 검증은 기존 `CpuMatching.Data`, `CpuMatching.AccuracyValidation`, `CpuMatching.PhaseValidation`에 별도로 유지한다.

```powershell
artifacts/sidebar-verified/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe run samples/CpuMatching.Modules artifacts/modules-cli.json all 3 compare
artifacts/sidebar-verified/bin/TeachingToolWpf/release/TeachingToolWpf.exe --cpu-modules-comparison
```

CLI는 첫 실행·10회 준비·3회 측정을 포함하므로 큰 원본에서 오래 걸릴 수 있다. 버튼→표시는 WPF 진단으로 측정하며 CLI 수치와 구별한다. 기본 데이터는 합성 반복성이 강하므로 실제 촬영8K의 검출률이나 속도를 보장하지 않는다.
