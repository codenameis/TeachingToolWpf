// File: tests/VisionAlgorithm.Tests/PatMaxStep2Tests.cs
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.Tests;

public sealed class PatMaxStep2Tests
{
    private const int MicroWidth = 48;
    private const int MicroHeight = 32;
    private const int PaddedStride = 64;
    private const byte BackgroundGray = 25;
    private const byte ForegroundGray = 225;

    [Theory]
    [InlineData(10.0f, 8.0f, 28.0f, 22.0f, 1.5f, 0.0f)]
    [InlineData(12.25f, 6.5f, 34.75f, 24.5f, 0.0f, -1.25f)]
    [InlineData(14.5f, 9.25f, 32.5f, 21.75f, -1.75f, 1.0f)]
    public void PatMaxTrainer_PaddedMicroRectangle_BuildsMultiScaleDipolesAndExactAttractionVectors(
        float rectLeft,
        float rectTop,
        float rectRight,
        float rectBottom,
        float probeOffsetX,
        float probeOffsetY)
    {
        var buffer = new byte[MicroHeight * PaddedStride];
        RenderAntiAliasedBox(buffer, MicroWidth, MicroHeight, PaddedStride, rectLeft, rectTop, rectRight, rectBottom);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 3.0f);

        using var model = PatMaxTrainer.Train(buffer, MicroWidth, MicroHeight, PaddedStride, in trainParams);

        Assert.True(model.IsTrained);
        Assert.Equal(3, model.LevelCount);
        Assert.True(model.Stability.IsStable);
        Assert.False(model.Stability.HasApertureWarning);
        Assert.True(model.FineLevel.Dipoles.Count > model.CoarseLevel.Dipoles.Count);

        var fineDipoles = model.FineLevel.Dipoles;
        var sampleDipole = fineDipoles[fineDipoles.Count / 2];

        var queryX = sampleDipole.X + probeOffsetX;
        var queryY = sampleDipole.Y + probeOffsetY;

        Assert.True(model.FineAttractionField.TrySampleNearest(queryX, queryY, out var cell));
        Assert.True(cell.HasNearestDipole);

        var nearestDipole = fineDipoles[cell.NearestDipoleIndex];
        var quantizedCellX = model.FineAttractionField.OriginX
            + (MathF.Round((queryX - model.FineAttractionField.OriginX) * model.FineAttractionField.InvCellSize) * model.FineAttractionField.CellSize);
        var quantizedCellY = model.FineAttractionField.OriginY
            + (MathF.Round((queryY - model.FineAttractionField.OriginY) * model.FineAttractionField.InvCellSize) * model.FineAttractionField.CellSize);

        Assert.InRange(MathF.Abs(cell.DeltaX - (nearestDipole.X - quantizedCellX)), 0.0f, 1e-4f);
        Assert.InRange(MathF.Abs(cell.DeltaY - (nearestDipole.Y - quantizedCellY)), 0.0f, 1e-4f);
        Assert.InRange(MathF.Abs(cell.Distance - MathF.Sqrt((cell.DeltaX * cell.DeltaX) + (cell.DeltaY * cell.DeltaY))), 0.0f, 1e-4f);
    }

    [Fact]
    public void PatMaxTrainer_WithExcludedMask_OmitsMaskedBoundaryDipolesAndProjectsPreview()
    {
        var buffer = new byte[MicroHeight * PaddedStride];
        RenderAntiAliasedBox(buffer, MicroWidth, MicroHeight, PaddedStride, 10.0f, 8.0f, 36.0f, 24.0f);

        var mask = new byte[MicroWidth * MicroHeight];
        for (var y = 0; y < MicroHeight; y++)
        {
            for (var x = 0; x < MicroWidth / 2; x++)
            {
                mask[(y * MicroWidth) + x] = byte.MaxValue;
            }
        }

        var trainParams = PatMaxTrainParams.Default;
        using var fullModel = PatMaxTrainer.Train(buffer, MicroWidth, MicroHeight, PaddedStride, in trainParams);
        using var maskedModel = PatMaxTrainer.Train(buffer, MicroWidth, MicroHeight, PaddedStride, in trainParams, excludedMask: mask);

        Assert.True(maskedModel.FineLevel.Dipoles.Count < fullModel.FineLevel.Dipoles.Count);
        Assert.All(maskedModel.FineLevel.Dipoles.X.ToArray(), relX => Assert.True(relX >= -0.5f));

        Span<EdgeDipole> previewBuffer = stackalloc EdgeDipole[16];
        var projectedCount = maskedModel.ProjectDipolesToSpan(0, maskedModel.OriginX, maskedModel.OriginY, 0.0f, 1.0f, previewBuffer);
        Assert.Equal(16, projectedCount);
        Assert.InRange(MathF.Abs(previewBuffer[0].X - (maskedModel.FineLevel.Dipoles[0].X + maskedModel.OriginX)), 0.0f, 1e-4f);
    }

    private static void RenderAntiAliasedBox(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float left,
        float top,
        float right,
        float bottom)
    {
        for (var y = 0; y < height; y++)
        {
            var py = y + 0.5f;
            var covY = Math.Clamp(py - top + 0.5f, 0.0f, 1.0f) * Math.Clamp(bottom - py + 0.5f, 0.0f, 1.0f);
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var px = x + 0.5f;
                var covX = Math.Clamp(px - left + 0.5f, 0.0f, 1.0f) * Math.Clamp(right - px + 0.5f, 0.0f, 1.0f);
                var coverage = covX * covY;
                var gray = BackgroundGray + ((ForegroundGray - BackgroundGray) * coverage);
                buffer[rowOffset + x] = (byte)MathF.Round(gray);
            }
        }
    }
}
