using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Alignment;

/// <summary>전체(보드) → 모듈(A/B) → 컴포넌트(A-1/A-2/B-1/B-2) → 리프 ROI 및 수억~수십억 셀 배치 테스트 프리셋.</summary>
public static class AlignmentTestPreset
{
    public static (ImageReference Image, ImmutableArray<Roi> Rois, ImmutableArray<FiducialScopeDefinition> Scopes) Create(
        int gridSidePerComponent = 5001,
        int visualGridSidePerComponent = 15)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridSidePerComponent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(visualGridSidePerComponent);

        var image = new ImageReference("sample:8192:8192", "8K 계층 보정 테스트 이미지", 8192, 8192);
        var rois = ImmutableArray.CreateBuilder<Roi>();
        var scopes = ImmutableArray.CreateBuilder<FiducialScopeDefinition>();
        long nextOrder = 0;

        scopes.Add(new FiducialScopeDefinition(
            null,
            "전체 보정 (Global · 보드)",
            null,
            [new(800, 800), new(7400, 800), new(7400, 7400), new(800, 7400)]));

        var board = AddRoi("보드 전체 (Root)", new(new(4096, 4096), 7200, 6800, 0), null, isGroup: true);
        var moduleA = AddModule("모듈 A (좌측)", new(2350, 4096), board.Id);
        var moduleB = AddModule("모듈 B (우측)", new(5850, 4096), board.Id);

        AddComponent("컴포넌트 A-1 (좌상)", new(2350, 2650), moduleA.Id);
        AddComponent("컴포넌트 A-2 (좌하)", new(2350, 5550), moduleA.Id);
        AddComponent("컴포넌트 B-1 (우상)", new(5850, 2650), moduleB.Id);
        AddComponent("컴포넌트 B-2 (우하)", new(5850, 5550), moduleB.Id);

        return (image, rois.ToImmutable(), scopes.ToImmutable());

        Roi AddRoi(string name, RoiGeometry geometry, Guid? parentId, bool isGroup, bool isNameVisible = true, RoiSearchRange? searchRange = null)
        {
            var roi = new Roi(Guid.NewGuid(), name, geometry, parentId, isGroup, ++nextOrder)
            {
                IsNameVisible = isNameVisible,
                SearchRange = searchRange
            };
            rois.Add(roi);
            return roi;
        }

        Roi AddModule(string name, PointD center, Guid parentId)
        {
            var module = AddRoi(name, new(center, 3100, 6000, 0), parentId, isGroup: true);
            ImmutableArray<PointD> marks =
            [
                new(center.X - 1300, center.Y - 2750),
                new(center.X + 1300, center.Y - 2750),
                new(center.X + 1300, center.Y + 2750),
                new(center.X - 1300, center.Y + 2750)
            ];
            scopes.Add(new FiducialScopeDefinition(module.Id, $"모듈 보정 · {name}", null, marks));
            return module;
        }

        void AddComponent(string name, PointD center, Guid moduleId)
        {
            var component = AddRoi(name, new(center, 2500, 2300, 0), moduleId, isGroup: true);
            ImmutableArray<PointD> marks =
            [
                new(center.X - 1100, center.Y - 950),
                new(center.X + 1100, center.Y + 950)
            ];

            AddRoi($"{name} · 좌상 패드", new(new(center.X - 780, center.Y - 620), 260, 180, 0), component.Id, isGroup: false, isNameVisible: true, searchRange: new(20, 20));
            AddRoi($"{name} · 우하 원형마크", new(new(center.X + 780, center.Y + 620), 220, 220, 0, RoiShape.Circle), component.Id, isGroup: false, isNameVisible: true, searchRange: new(20, 20));

            var visualSpan = 1200d;
            var visualPitch = visualSpan / Math.Max(1, visualGridSidePerComponent);
            var cellSize = Math.Max(visualPitch * 0.62, 4);
            var halfSide = (visualGridSidePerComponent - 1) / 2d;
            for (var row = 0; row < visualGridSidePerComponent; row++)
            {
                var y = center.Y + ((row - halfSide) * visualPitch);
                for (var column = 0; column < visualGridSidePerComponent; column++)
                {
                    var x = center.X + ((column - halfSide) * visualPitch);
                    AddRoi(
                        $"{name} 셀 [{row},{column}]",
                        new(new(x, y), cellSize, cellSize, 0),
                        component.Id,
                        isGroup: false,
                        isNameVisible: false);
                }
            }

            var parametricPitch = 1600d / Math.Max(1, gridSidePerComponent);
            var parametricCellSize = Math.Max(parametricPitch * 0.65, 0.01);
            var grid = new ComponentRoiGrid(
                component.Id,
                $"{name} 초고밀도 셀 배열",
                center,
                gridSidePerComponent,
                gridSidePerComponent,
                parametricPitch,
                parametricPitch,
                parametricCellSize,
                parametricCellSize);

            scopes.Add(new FiducialScopeDefinition(component.Id, $"컴포넌트 보정 · {name}", moduleId, marks, grid));
        }
    }
}
