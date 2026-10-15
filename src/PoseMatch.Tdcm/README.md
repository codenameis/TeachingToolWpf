# PoseMatch.Tdcm

.NET10 CPU/CUDA ONNX 추론 라이브러리. 공식 사전 학습 가중치와 MIT 고지를 포함한다. `VisionAlgorithm`이 ProjectReference로 참조한다. WPF·Canvas·OpenCV·Python을 실행 의존성으로 요구하지 않는다.

```csharp
using PoseMatch.Tdcm;

using var matcher = new TdcmMatcher(threads: 4);
// CUDA: new TdcmMatcher(threads: 4, device: TdcmDevice.Cuda)
var image = new PixelImage(grayPixels, 512, 512, 512);
var template = new PixelImage(templatePixels, 72, 72, 72);
var result = matcher.Find(image, template, new MatchOptions
{
    MinimumScore = .5f,
    MaximumMatches = 256,
    MaximumCandidates = 4096,
    SuppressionRadius = 18,
    CenterRadius = 15,
    RefinePose = false
}, cancellationToken);
```

위 저수준 API의 검색 영상은36~1024의4배수이며 **검색 영상의 대상 자체가18~72px** 범위여야 한다. 큰 원본 템플릿만 넘기면 검색 영상을 자동 축소하지 않는다. 큰 원본 이미지·회전 ROI 등록·원본 좌표 복원은 `VisionInspection/Matching/TdcmMatchingService`를 사용한다. `PixelImage`는 Gray8 또는BGR24를 지원하며 caller는 호출이 끝날 때까지 메모리를 변경하지 않는다. 세션은 명시적으로 소유하고 진행 중 호출이 모두 끝난 후 Dispose한다.

`Predict`는 다섯 원시 지도를 반환하고 `Find`는 여러 극대점/중복 억제/선택형 자세 보정을 적용한다. 각도는 영상 좌표 시계방향, 폭/높이는 독립 값이다. ONNX CPU 스레드 수는 세션 생성 시 고정한다. 큰 수로 바꾼다고 빨라진다고 보장하지 않는다.

[사용법·제약·실측](../../docs/posematch-tdcm.md), [공식 원본](https://github.com/ZhouJ6610/PoseMatch-TDCM), [MIT 고지](Models/LICENSE). 다른 앱에 배포할 때 모델 고지와 ONNX Runtime의 LICENSE/ThirdPartyNotices도 유지한다.

CUDA 런타임 준비, 앱 실행 및 정확도/성능 미달 범위는 [CUDA 검증](../../docs/posematch-tdcm-cuda.md)을 따른다. 원본 FP32 출력 head 결합은 가중치·전체 검색 범위를 보존한다. CPU/CUDA의 부동소수 출력이 비트 단위로 같다고 보장하지 않는다.
