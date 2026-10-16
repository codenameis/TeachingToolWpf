# 이상 감지 합성 영상

실제 영상 디코더와 정상 학습·알람 경로를 재현하기 위한 자체 생성 데이터다. 현장 CCTV나 실제 불량 제품 데이터가 아니다.

| 파일 | 내용 |
|---|---|
| `normal.avi` | 640×360, MJPEG AVI, 10FPS, 240프레임/24초. 컨베이어의 정상 이동 |
| `inspection.avi` | 같은 장면. 11~12.5초 붉은/검은 외관 변화, 16~19.5초 이동 속도 증가 |
| `normal-model.json` | `normal.avi`에서 3프레임마다 수집한 80프레임의 정상 모델. 전체 영역, 동봉 특징 모델에만 대응 |

`tests/VisionAnomaly.Tests/VideoFixtures.cs`가 영상을 생성한다. `VideoSequenceTests`는 정상 영상으로 모델을 만든 후 별도 검사 영상을 순차 분석한다. 정상 영상 일부는 외관 임계값 보정에만 쓰고 패치 메모리에 넣지 않는다. 학습용 정상 영상과 검사 영상은 같은 합성 장면이므로 현장 일반화 성능의 증거로 사용하지 않는다.

```powershell
dotnet test tests/VisionAnomaly.Tests -c Release
```

재생성은 저장소 루트에서 실행한다. UI 실행 순서는 [앱 안내](../../src/AnomalyMonitor/README.md)를 따른다. 샘플을 열어 둔 다른 앱이 있으면 파일 쓰기 때문에 재생성을 먼저 완료한다.
