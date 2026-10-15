using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.MvvmSample.Models;

// record는 값을 담는 형식이다. 알고리즘은 여기서는 설정 데이터이며 실제 검사 결과가 아니다.
// Canvas가 알고리즘 이름을 알 필요는 없다. ROI의 ID로 샘플 모델과 Canvas 문서를 연결한다.
public sealed record AlgorithmSettings(string Name, string Parameters);

public sealed record CameraModel(string Name, ImageReference Image, ImmutableArray<Roi> Rois,
    ImmutableDictionary<Guid, AlgorithmSettings> Algorithms);

public sealed record SampleInspectionModel(string Name, CameraModel FirstCamera, CameraModel SecondCamera)
{
    public static SampleInspectionModel Create() => new("부품 검사 예제 모델 · 2 cameras",
        CreateCamera("카메라 1 · 상면", "demo:camera:1", 0),
        CreateCamera("카메라 2 · 하면", "demo:camera:2", 500));

    private static CameraModel CreateCamera(string name, string key, double offset)
    {
        var rois = ImmutableArray.CreateBuilder<Roi>();
        var algorithms = ImmutableDictionary.CreateBuilder<Guid, AlgorithmSettings>();
        // 좌표와 크기는 화면 픽셀이 아니라 8192 × 8192 원본 이미지 기준이다.
        // 부모를 먼저 정의하고 그 ID를 자식에 전달하여 실제 ROI 계층을 만든다.
        var board = Add("보드 그룹", new(new(4000, 3800), 6500, 6000, 0), null, true);
        var connector = Add("커넥터 그룹", new(new(2500 + offset, 2400), 2400, 1900, 0), board, true);
        Add("핀 밝기", new(new(2000 + offset, 2100), 500, 650, 0), connector, false,
            new("평균 밝기", "허용 범위: 60–200 / Gray8"));
        Add("접점 유무", new(new(3000 + offset, 2400), 650, 650, 0, RoiShape.Circle), connector, false,
            new("이진화 면적", "임계값: 128 / 최소 면적: 1,500 px²"));
        Add("문자 위치", new(new(5100, 2400 + offset), 1500, 700, 12), board, false,
            new("패턴 매칭", "최소 점수: 0.80 / 각도: −15°~15°"));
        Add("외곽 확인", RoiGeometry.Polygon([new(4200, 4400), new(6000, 4200), new(6400, 5400), new(5000, 5700)]), board, false,
            new("에지 개수", "낮은 임계값: 40 / 높은 임계값: 100"));
        Add("독립 홀", new(new(7200 - offset, 7400), 650, 650, 0, RoiShape.Circle), null, false,
            new("원 검출", "반지름: 250–350 px / 최소 원형도: 0.90"));
        return new(name, new(key, name + " · 8K 합성 이미지", 8192, 8192), rois.ToImmutable(), algorithms.ToImmutable());

        Guid Add(string roiName, RoiGeometry geometry, Guid? parent, bool group, AlgorithmSettings? algorithm = null)
        {
            var id = Guid.NewGuid();
            rois.Add(new(id, roiName, geometry, parent, group, rois.Count + 1));
            if (algorithm is not null)
            {
                algorithms.Add(id, algorithm);
            }
            return id;
        }
    }
}
