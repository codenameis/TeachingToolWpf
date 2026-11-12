using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Alignment;

/// <summary>현재 문서의 그룹 계층에서 전체·보드·모듈·컴포넌트 보정 스코프와 기본 보정마크·격자를 구성한다.</summary>
public static class DocumentAlignmentScopeBuilder
{
    public const int FallbackImageWidthPixels = 8192;
    public const int FallbackImageHeightPixels = 8192;
    public const double GlobalMarkMinRatio = 0.1;
    public const double GlobalMarkMaxRatio = 0.9;
    public const double GroupMarkHalfSpanRatio = 0.35;
    public const double MinimumGroupMarkOffsetPixels = 4.0;
    public const double ComponentGridSpanRatio = 0.6;
    public const double MinimumComponentPitchPixels = 0.02;
    public const double ComponentCellSizeToPitchRatio = 0.65;
    public const double MinimumComponentCellSizePixels = 0.01;
    public const int ComponentGridMinimumDepth = 2;

    public static PointD ResolveScopeCenter(DocumentState state, Guid? scopeId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (scopeId is { } id && state.Rois.TryGetValue(id, out var roi))
        {
            return roi.Geometry.Center;
        }

        return state.Image is { } image
            ? new(image.Width / 2d, image.Height / 2d)
            : new(FallbackImageWidthPixels / 2d, FallbackImageHeightPixels / 2d);
    }

    public static ImmutableArray<FiducialScopeDefinition> BuildFromDocument(DocumentState state, int gridSide)
    {
        ArgumentNullException.ThrowIfNull(state);
        var width = state.Image?.Width ?? FallbackImageWidthPixels;
        var height = state.Image?.Height ?? FallbackImageHeightPixels;
        var builder = ImmutableArray.CreateBuilder<FiducialScopeDefinition>();
        builder.Add(new FiducialScopeDefinition(
            null,
            "전체 보정 (Global · 전체 화면)",
            null,
            [
                new(width * GlobalMarkMinRatio, height * GlobalMarkMinRatio),
                new(width * GlobalMarkMaxRatio, height * GlobalMarkMinRatio),
                new(width * GlobalMarkMaxRatio, height * GlobalMarkMaxRatio),
                new(width * GlobalMarkMinRatio, height * GlobalMarkMaxRatio)
            ]));

        foreach (var roi in state.Rois.Values
            .Where(r => state.IsGroupRoi(r.Id))
            .OrderBy(r => RoiHierarchy.Depth(state.Rois, r.Id))
            .ThenBy(r => r.Order))
        {
            var depth = RoiHierarchy.Depth(state.Rois, roi.Id);
            var rolePrefix = depth switch
            {
                1 => "보드 그룹 보정",
                2 => "모듈 보정",
                _ => "컴포넌트 보정"
            };
            var bounds = roi.Geometry.Bounds;
            var dx = Math.Max(MinimumGroupMarkOffsetPixels, bounds.Width * GroupMarkHalfSpanRatio);
            var dy = Math.Max(MinimumGroupMarkOffsetPixels, bounds.Height * GroupMarkHalfSpanRatio);
            var center = roi.Geometry.Center;
            ImmutableArray<PointD> marks =
            [
                new(center.X - dx, center.Y - dy),
                new(center.X + dx, center.Y + dy)
            ];

            ComponentRoiGrid? grid = null;
            if (depth >= ComponentGridMinimumDepth)
            {
                var pitch = Math.Max(bounds.Width * ComponentGridSpanRatio / Math.Max(1, gridSide), MinimumComponentPitchPixels);
                var cellSize = Math.Max(pitch * ComponentCellSizeToPitchRatio, MinimumComponentCellSizePixels);
                grid = new ComponentRoiGrid(roi.Id, $"{roi.Name} 반복 셀 배치", center, gridSide, gridSide, pitch, pitch, cellSize, cellSize);
            }

            builder.Add(new FiducialScopeDefinition(roi.Id, $"{rolePrefix} · {roi.Name}", roi.ParentId, marks, grid));
        }

        return builder.ToImmutable();
    }
}
