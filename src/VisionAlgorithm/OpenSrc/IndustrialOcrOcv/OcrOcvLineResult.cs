namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Complete text-line OCR/OCV inspection result including decoded string, per-character diagnostics, and overall pass/fail verdict.
/// Implements <see cref="VisionAlgorithm.OpenSrc.Common.IVisionToolResult"/> for SPC logging and per-character box replay on <c>VisionCanvas</c>.
/// </summary>
public readonly record struct OcrOcvLineResult(
    string DecodedText,
    bool IsPass,
    OcvDefectFlags DefectFlags,
    float MinCharScore,
    float MinConfusionMargin,
    float MinOcvQualityScore,
    float MinContrast,
    CharVerifyResult[] CharacterResults) : VisionAlgorithm.OpenSrc.Common.IVisionToolResult
{
    public VisionAlgorithm.OpenSrc.Common.FixturePose2D LineWorldPose { get; init; } = VisionAlgorithm.OpenSrc.Common.FixturePose2D.Identity;
    public short MatchedFontModelIndex { get; init; }

    public VisionAlgorithm.OpenSrc.Common.VisionResultHeader Header =>
        string.IsNullOrEmpty(DecodedText) && (CharacterResults is null || CharacterResults.Length == 0)
            ? VisionAlgorithm.OpenSrc.Common.VisionResultHeader.NotFound(VisionAlgorithm.OpenSrc.Common.VisionToolCategory.OcrVerification)
            : VisionAlgorithm.OpenSrc.Common.VisionResultHeader.SucceededJudge(
                category: VisionAlgorithm.OpenSrc.Common.VisionToolCategory.OcrVerification,
                isPass: IsPass,
                qualityScore: Math.Clamp(MinCharScore, 0.0f, 1.0f),
                primaryMetric: MinOcvQualityScore,
                matchedModelIndex: MatchedFontModelIndex,
                matchedModelId: MatchedFontModelIndex + 1,
                defectFlags: (uint)DefectFlags);

    public VisionAlgorithm.OpenSrc.Common.FixturePose2D WorldPose =>
        LineWorldPose.IsValid ? LineWorldPose : VisionAlgorithm.OpenSrc.Common.FixturePose2D.Identity;

    public VisionAlgorithm.OpenSrc.Common.VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        int totalChars = CharacterResults?.Length ?? 0;
        int passChars = 0;
        if (CharacterResults is not null)
        {
            for (int i = 0; i < CharacterResults.Length; i++)
            {
                if (CharacterResults[i].IsPass)
                {
                    passChars++;
                }
            }
        }

        float marginRatio = VisionAlgorithm.OpenSrc.Common.VisionSpcRecord.ComputeSpecMarginRatio(MinOcvQualityScore, specLsl, specUsl);

        return new VisionAlgorithm.OpenSrc.Common.VisionSpcRecord(
            NodeId: nodeId,
            RoiId: roiId,
            Header: Header,
            TotalModelCount: 1,
            RunnerUpModelIndex: VisionAlgorithm.OpenSrc.Common.VisionResultHeader.UnmatchedModelIndex,
            RunnerUpScore: Math.Clamp(MinCharScore - MinConfusionMargin, 0.0f, 1.0f),
            WorldX: WorldPose.CenterX,
            WorldY: WorldPose.CenterY,
            WorldAngleDeg: WorldPose.AngleRad * (180.0f / MathF.PI),
            Scale: WorldPose.Scale,
            DeltaX: 0.0f,
            DeltaY: 0.0f,
            DeltaAngleDeg: 0.0f,
            PrimaryMetric: MinOcvQualityScore,
            SpecLsl: specLsl,
            SpecUsl: specUsl,
            SpecMarginRatio: marginRatio,
            MeanGrayValue: 0.0f,
            ContrastOrStdDev: MinContrast,
            RmsFitErrorPixels: 0.0f,
            AuxMetric1: MinCharScore,
            AuxMetric2: MinConfusionMargin,
            AuxMetric3: MinContrast,
            HitCount: passChars,
            TotalSampleCount: totalChars,
            DecodedText: DecodedText ?? string.Empty);
    }

    public VisionAlgorithm.OpenSrc.Common.VisionReplayPayload ToReplayPayload()
    {
        int count = CharacterResults?.Length ?? 0;
        var subRegions = count > 0 ? new VisionAlgorithm.OpenSrc.Common.ReplaySubRegion[count] : [];
        const float DefaultCharPitch = 18.0f;
        const float DefaultCharWidth = 14.0f;
        const float DefaultCharHeight = 24.0f;
        float startX = WorldPose.CenterX - ((count - 1) * DefaultCharPitch * 0.5f);

        for (int i = 0; i < count; i++)
        {
            CharVerifyResult ch = CharacterResults![i];
            subRegions[i] = new VisionAlgorithm.OpenSrc.Common.ReplaySubRegion(
                CenterX: startX + (i * DefaultCharPitch) + ch.SubpixelShiftX,
                CenterY: WorldPose.CenterY + ch.SubpixelShiftY,
                Width: DefaultCharWidth,
                Height: DefaultCharHeight,
                AngleRadians: WorldPose.AngleRad,
                ScoreOrMetric: ch.OcvQualityScore,
                TagChar: ch.DecodedChar,
                IsPass: ch.IsPass);
        }

        string status = IsPass ? "OK" : $"NG:{DefectFlags}";
        string label = $"OCR/OCV Font#{MatchedFontModelIndex} \"{DecodedText}\": MinQ={MinOcvQualityScore:F2} Margin={MinConfusionMargin:F2} [{status}]";

        return new VisionAlgorithm.OpenSrc.Common.VisionReplayPayload(
            ShapeKind: VisionAlgorithm.OpenSrc.Common.ReplayShapeKind.OrientedBox,
            WorldPose: WorldPose,
            WidthOrRadius: MathF.Max(40.0f, count * DefaultCharPitch),
            HeightOrSpan: DefaultCharHeight + 8.0f,
            SegmentAStart: System.Numerics.Vector2.Zero,
            SegmentAEnd: System.Numerics.Vector2.Zero,
            SegmentBStart: System.Numerics.Vector2.Zero,
            SegmentBEnd: System.Numerics.Vector2.Zero,
            SubRegions: subRegions,
            InlierPointsWorld: ReadOnlyMemory<System.Numerics.Vector2>.Empty,
            OutlierPointsWorld: ReadOnlyMemory<System.Numerics.Vector2>.Empty,
            SummaryLabel: label);
    }
}
