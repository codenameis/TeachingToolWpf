using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>GPU 및 CPU ROI 파이프라인들이 공통으로 사용하는 가시 영역·LOD 계산과 공간 인덱스 순회 보조.</summary>
public static class RoiSpatialTraversal
{
    public const double ViewportPaddingDip = 8;
    public const double SummarySizeDip = 6;

    public static RoiLodMetrics ComputeLodMetrics(RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var view = scene.Document.Viewport;
        var topLeft = view.ScreenToImage(new(-ViewportPaddingDip, -ViewportPaddingDip));
        var bottomRight = view.ScreenToImage(new(scene.Width + ViewportPaddingDip, scene.Height + ViewportPaddingDip));
        var visible = new BoundsD(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        var level = (int)Math.Floor(Math.Log2(SummarySizeDip / view.Scale));
        var aggregateSize = Math.Pow(2, level);
        return new RoiLodMetrics(visible, level, aggregateSize);
    }

    public static void ForEachIndex(RenderScene scene, Action<RoiSpatialIndex, bool, RigidTransform2D, bool> drawIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(drawIndex);
        if (scene.Transform is { } preview)
        {
            drawIndex(preview.Selection.Stationary, false, default, false);
            if (preview.Selection.Affected.Count != scene.Document.Selection.Count)
            {
                drawIndex(preview.Selection.Moving, false, preview.Transform, false);
            }

            drawIndex(scene.Document.SelectedSpatial, true, preview.Transform, false);
            drawIndex(preview.Selection.StationarySearch, false, default, true);
            drawIndex(preview.Selection.MovingSearch, false, preview.Transform, true);
        }
        else if (scene.Alignment is { HasAnyAlignment: true } alignment)
        {
            if (!alignment.HasGroupAlignments)
            {
                var overall = alignment.OverallTransform;
                if (scene.Document.Selection.Count != scene.Document.Rois.Count)
                {
                    drawIndex(scene.Document.Snapshot.Spatial, false, overall, false);
                }

                if (scene.Document.Selection.Count > 0)
                {
                    drawIndex(scene.Document.SelectedSpatial, true, overall, false);
                }

                drawIndex(scene.Document.Snapshot.SearchSpatial, false, overall, true);
                return;
            }

            foreach (var partition in scene.Document.Snapshot.AlignmentPartitions)
            {
                var scopeTransform = alignment.GetScopeTransform(partition.ScopeId);
                drawIndex(partition.Spatial, false, scopeTransform, false);
                if (partition.HasSearchRanges)
                {
                    drawIndex(partition.SearchSpatial, false, scopeTransform, true);
                }
            }

            if (scene.Document.Selection.Count == 1)
            {
                var selectedRoi = scene.Document.Rois[scene.Document.Selection.Single()];
                drawIndex(scene.Document.SelectedSpatial, true, alignment.GetEffectiveTransform(selectedRoi), false);
            }
            else if (scene.Document.Selection.Count > 1)
            {
                drawIndex(scene.Document.SelectedSpatial, true, alignment.OverallTransform, false);
            }
        }
        else
        {
            if (scene.Document.Selection.Count != scene.Document.Rois.Count)
            {
                drawIndex(scene.Document.Snapshot.Spatial, false, default, false);
            }

            if (scene.Document.Selection.Count > 0)
            {
                drawIndex(scene.Document.SelectedSpatial, true, default, false);
            }

            drawIndex(scene.Document.Snapshot.SearchSpatial, false, default, true);
        }
    }

    public static void DrawCachedBlocks<TBlock, TSurface>(
        RoiBlockCache<TBlock> cache,
        RenderScene scene,
        TSurface surface,
        RoiBlockCache<TBlock>.UploadBlockDelegate uploadBlock,
        Action<TBlock, TSurface> uploadRoleChanges,
        Action<TBlock, RoiDrawConstants> drawBlock)
        where TBlock : RoiCachedBlockBase
        where TSurface : IRenderSurface
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(uploadBlock);
        ArgumentNullException.ThrowIfNull(uploadRoleChanges);
        ArgumentNullException.ThrowIfNull(drawBlock);

        cache.BeginFrame();
        var lod = ComputeLodMetrics(scene);
        ForEachIndex(scene, DrawIndex);
        cache.EvictInactive(static memoryBytes => memoryBytes, surface.Retire);

        void DrawIndex(RoiSpatialIndex index, bool selected, RigidTransform2D transform, bool searchRange)
        {
            foreach (var region in index.Blocks(transform.InverseTransformBounds(lod.VisibleBounds)))
            {
                var block = cache.GetOrUpload(
                    region,
                    lod.Level,
                    lod.AggregateSize,
                    scene.Document,
                    uploadBlock,
                    b => uploadRoleChanges(b, surface));
                if (searchRange)
                {
                    drawBlock(block, new RoiDrawConstants(scene, block.Origin, transform, selected, RoiDrawPass.SearchRange));
                }
                else
                {
                    drawBlock(block, new RoiDrawConstants(scene, block.Origin, transform, selected, RoiDrawPass.Shadow));
                    drawBlock(block, new RoiDrawConstants(scene, block.Origin, transform, selected, RoiDrawPass.Stroke));
                }
            }
        }
    }
}
