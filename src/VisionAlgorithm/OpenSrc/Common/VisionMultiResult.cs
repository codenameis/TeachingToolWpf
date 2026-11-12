using System.Numerics;
using System.Text;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Standardized immutable multi-item vision inspection result container implementing both <see cref="IVisionToolResult"/>
/// (aggregate summary + <see cref="ReplaySubRegion"/> boxes for single-envelope consumers) and
/// <see cref="IMultiVisionToolResult{TItem}"/> (lossless per-item <see cref="VisionInspectionEnvelope"/> export).
/// </summary>
public readonly record struct VisionMultiResult<TItem> : IMultiVisionToolResult<TItem>
    where TItem : IVisionToolResult
{
    private const float RadiansToDegrees = 180.0f / MathF.PI;
    private const char DefaultTagChar = '#';

    private readonly VisionToolCategory _category;
    private readonly ReadOnlyMemory<TItem> _items;

    private VisionMultiResult(VisionToolCategory category, ReadOnlyMemory<TItem> items)
    {
        _category = category;
        _items = items;
    }

    public static VisionMultiResult<TItem> Empty(VisionToolCategory category)
        => new(category, ReadOnlyMemory<TItem>.Empty);

    public static VisionMultiResult<TItem> Create(VisionToolCategory category, ReadOnlyMemory<TItem> items)
        => new(category, items);

    public int Count => _items.Length;

    public bool HasAnyDecoded => !_items.IsEmpty;

    public ReadOnlyMemory<TItem> Items => _items;

    public string DecodedText
    {
        get
        {
            ReadOnlySpan<TItem> span = _items.Span;
            if (span.IsEmpty)
            {
                return string.Empty;
            }

            if (span.Length == 1)
            {
                return span[0].ToSpcRecord().DecodedText ?? string.Empty;
            }

            var sb = new StringBuilder(capacity: span.Length * 16);
            for (int i = 0; i < span.Length; i++)
            {
                string? itemText = span[i].ToSpcRecord().DecodedText;
                if (!string.IsNullOrEmpty(itemText))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(" | ");
                    }

                    sb.Append(itemText);
                }
            }

            return sb.ToString();
        }
    }

    public int PassCount
    {
        get
        {
            ReadOnlySpan<TItem> span = _items.Span;
            int pass = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].IsPass)
                {
                    pass++;
                }
            }

            return pass;
        }
    }

    public VisionResultHeader Header
    {
        get
        {
            ReadOnlySpan<TItem> span = _items.Span;
            if (span.IsEmpty)
            {
                return VisionResultHeader.NotFound(_category);
            }

            bool allPass = true;
            float sumQuality = 0.0f;
            for (int i = 0; i < span.Length; i++)
            {
                VisionResultHeader itemHeader = span[i].Header;
                if (!span[i].IsPass)
                {
                    allPass = false;
                }

                sumQuality += itemHeader.QualityScore;
            }

            float meanQuality = sumQuality / span.Length;
            return VisionResultHeader.SucceededJudge(
                category: _category,
                isPass: allPass,
                qualityScore: meanQuality,
                primaryMetric: span.Length);
        }
    }

    public FixturePose2D WorldPose => _items.IsEmpty ? FixturePose2D.Identity : _items.Span[0].WorldPose;

    public Vector2 ReferencePointWorld => _items.IsEmpty ? Vector2.Zero : _items.Span[0].ReferencePointWorld;

    public VisionInspectionEnvelope[] ToInspectionEnvelopes(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        ReadOnlySpan<TItem> span = _items.Span;
        if (span.IsEmpty)
        {
            return [];
        }

        var envelopes = new VisionInspectionEnvelope[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            envelopes[i] = span[i].ToInspectionEnvelope(nodeId, roiId, specLsl, specUsl);
        }

        return envelopes;
    }

    public VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN)
    {
        ReadOnlySpan<TItem> span = _items.Span;
        if (span.IsEmpty)
        {
            return new VisionSpcRecord(
                NodeId: nodeId,
                RoiId: roiId,
                Header: Header,
                TotalModelCount: 0,
                RunnerUpModelIndex: VisionResultHeader.UnmatchedModelIndex,
                RunnerUpScore: 0.0f,
                WorldX: 0.0f,
                WorldY: 0.0f,
                WorldAngleDeg: 0.0f,
                Scale: 1.0f,
                DeltaX: 0.0f,
                DeltaY: 0.0f,
                DeltaAngleDeg: 0.0f,
                PrimaryMetric: 0.0f,
                SpecLsl: specLsl,
                SpecUsl: specUsl,
                SpecMarginRatio: 0.0f,
                MeanGrayValue: 0.0f,
                ContrastOrStdDev: 0.0f,
                RmsFitErrorPixels: 0.0f,
                AuxMetric1: 0.0f,
                AuxMetric2: 0.0f,
                AuxMetric3: 0.0f,
                HitCount: 0,
                TotalSampleCount: 0,
                DecodedText: string.Empty);
        }

        VisionSpcRecord primarySpc = span[0].ToSpcRecord(nodeId, roiId, specLsl, specUsl);
        if (span.Length == 1)
        {
            return primarySpc;
        }

        var joinedText = new StringBuilder(capacity: span.Length * 16);
        int passCount = 0;
        for (int i = 0; i < span.Length; i++)
        {
            VisionSpcRecord itemSpc = span[i].ToSpcRecord(nodeId, roiId, specLsl, specUsl);
            if (span[i].IsPass)
            {
                passCount++;
            }

            if (!string.IsNullOrEmpty(itemSpc.DecodedText))
            {
                if (joinedText.Length > 0)
                {
                    joinedText.Append(" | ");
                }

                joinedText.Append(itemSpc.DecodedText);
            }
        }

        FixturePose2D primaryPose = WorldPose;
        float marginRatio = VisionSpcRecord.ComputeSpecMarginRatio(span.Length, specLsl, specUsl);
        return primarySpc with
        {
            Header = Header,
            TotalModelCount = (short)Math.Min(span.Length, short.MaxValue),
            WorldX = primaryPose.CenterX,
            WorldY = primaryPose.CenterY,
            WorldAngleDeg = primaryPose.AngleRad * RadiansToDegrees,
            PrimaryMetric = span.Length,
            SpecLsl = specLsl,
            SpecUsl = specUsl,
            SpecMarginRatio = marginRatio,
            HitCount = passCount,
            TotalSampleCount = span.Length,
            DecodedText = joinedText.ToString()
        };
    }

    public VisionReplayPayload ToReplayPayload()
    {
        ReadOnlySpan<TItem> span = _items.Span;
        if (span.IsEmpty)
        {
            return VisionReplayPayload.Empty;
        }

        VisionReplayPayload primaryReplay = span[0].ToReplayPayload();
        if (span.Length == 1)
        {
            return primaryReplay;
        }

        var subRegions = new ReplaySubRegion[span.Length];
        var summaryBuilder = new StringBuilder(capacity: span.Length * 24);
        summaryBuilder.Append($"[{PassCount}/{span.Length}] ");

        for (int i = 0; i < span.Length; i++)
        {
            VisionReplayPayload itemReplay = span[i].ToReplayPayload();
            VisionResultHeader itemHeader = span[i].Header;
            VisionSpcRecord itemSpc = span[i].ToSpcRecord();
            char tag = !string.IsNullOrEmpty(itemSpc.DecodedText) ? itemSpc.DecodedText[0] : DefaultTagChar;

            subRegions[i] = new ReplaySubRegion(
                CenterX: itemReplay.WorldPose.CenterX,
                CenterY: itemReplay.WorldPose.CenterY,
                Width: itemReplay.WidthOrRadius,
                Height: itemReplay.HeightOrSpan,
                AngleRadians: itemReplay.WorldPose.AngleRad,
                ScoreOrMetric: itemHeader.QualityScore,
                TagChar: tag,
                IsPass: span[i].IsPass);

            if (i > 0)
            {
                summaryBuilder.Append(" | ");
            }

            summaryBuilder.Append(!string.IsNullOrEmpty(itemSpc.DecodedText) ? itemSpc.DecodedText : $"#{i + 1}");
        }

        return primaryReplay with
        {
            SubRegions = subRegions,
            SummaryLabel = summaryBuilder.ToString()
        };
    }
}
