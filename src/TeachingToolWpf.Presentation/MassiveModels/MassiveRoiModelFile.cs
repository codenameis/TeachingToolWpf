// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiModelFile.cs
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionGeometry;

namespace TeachingToolWpf.MassiveModels;

/// <summary>디스크에서 로드된 1억 개 랜덤 ROI 모델(.vmodel). 사전 캐싱 없이 파일의 바이너리 섹터 테이블로부터 임의 ROI를 O(1)에 복원한다.</summary>
public sealed class MassiveRoiModelFile
{
    private const ulong GuidNamespaceTag = 0x564D_444C_3130_304DuL; // [Domain Why]: Deterministic 64-bit tag ("VMDL100M") preventing collision with interactive user-drawn GUIDs.
    private const ulong SplitMixGamma = 0x9E37_79B9_7F4A_7C15uL; // [Math Why]: Weyl sequence golden-ratio constant for 64-bit SplitMix64 mixing.
    private const ulong SplitMixMultiplierA = 0xBF58_476D_1CE4_E5B9uL; // [Math Why]: Stafford Mix13 first avalanche multiplier.
    private const ulong SplitMixMultiplierB = 0x94D0_49BB_1331_11EBuL; // [Math Why]: Stafford Mix13 second avalanche multiplier.
    private const int Shift30 = 30; // [Math Why]: Stafford Mix13 first xorshift bit count.
    private const int Shift27 = 27; // [Math Why]: Stafford Mix13 second xorshift bit count.
    private const int Shift31 = 31; // [Math Why]: Stafford Mix13 final xorshift bit count.
    private const double UnitDoubleScale = 1.0 / (1uL << 53); // [Math Why]: Maps upper 53 bits of UInt64 to [0.0, 1.0) IEEE-754 double precision.
    private const int FloatMantissaShift = 11; // [Math Why]: 64 - 53 = 11 bit right shift to extract 53-bit mantissa.
    private const double MarginPixels = 64.0; // [Domain Why]: Keeps random ROI centers inside the image interior away from the 0..64px border.
    private const double MinRoiWidth = 18.0; // [Domain Why]: Minimum random ROI width in pixels so zoomed-in inspection targets remain visually clear.
    private const double MaxRoiWidthSpan = 46.0; // [Domain Why]: Random ROI width span [18, 64] pixels.
    private const double MinRoiHeight = 16.0; // [Domain Why]: Minimum random ROI height in pixels.
    private const double MaxRoiHeightSpan = 44.0; // [Domain Why]: Random ROI height span [16, 60] pixels.
    private const double MaxRotationDegrees = 360.0; // [Math Why]: Full 360-degree orientation range for random ROIs.
    private const double HalfFactor = 0.5; // [Math Why]: Midpoint multiplier for half-extent and symmetric angle centering.
    private const double BipolarMultiplier = 2.0; // [Math Why]: Maps [0, 1) unit interval to [-1, 1) bipolar span.
    private const double MinimumSpanPixels = 1.0; // [Math Why]: Lower bound preventing zero-span modulo division.
    private const int CoordinateRoundingDigits = 2; // [Domain Why]: Sub-pixel 0.01 px coordinate quantization for deterministic model storage.
    private const int DimensionRoundingDigits = 1; // [Domain Why]: 0.1 px width/height/angle quantization.
    private const ulong ShapeJitterMask = 0x7uL; // [Perf Why]: Low 3-bit mask for per-ROI shape variation.
    private const int AlgorithmMixShift = 40; // [Perf Why]: Extracts upper entropy bits for per-ROI algorithm slot dispersion.
    private const int GuidModelNumberShift = 32; // [Perf Why]: Places 32-bit model number in upper half of first 64-bit GUID word.
    private const ulong Low32BitMask = 0xFFFF_FFFFuL; // [Math Why]: Masks lower 32 bits of master seed.
    private const int SeedHighMaskShift = 16; // [Perf Why]: Shifts seed bits into upper 16 bits of second 64-bit GUID word while keeping lower 48 bits for 100M index.
    private const ulong Upper16BitMask = 0xFFFF_0000_0000_0000uL; // [Math Why]: Masks bits [48..63] so bits [0..47] hold up to 281 trillion ROI indices without collision.
    private const int GuidByteLength = 16; // [Domain Why]: Standard 128-bit GUID byte count.
    private const int HalfGuidByteLength = 8; // [Domain Why]: 64-bit half-word byte length within a 16-byte GUID.
    public const int TotalAlgorithmCatalogCount = 27; // [Domain Why]: Total number of distinct vision algorithms across Pattern(5), Shapes(1), FewShot(1), and Suite(20).
    public const int ShapeKindModulus = 3; // [Domain Why]: Cycles across Rectangle, Circle, and Triangle shapes per random ROI.

    private static readonly (InspectionToolKind Tool, PatternMatchingMethod? Pattern, InspectionSuiteAlgorithmKind? Suite, string Badge, string Accent, string Summary)[] AlgorithmCatalog =
        BuildAlgorithmCatalog();

    private readonly MassiveRoiSectorRecord[] _sectors;
    private readonly MassiveRoiEntry[] _canvasAnchors;

    public MassiveRoiModelFile(
        string filePath,
        MassiveRoiModelHeader header,
        MassiveRoiSectorRecord[] sectors,
        MassiveRoiEntry[] canvasAnchors,
        double loadElapsedMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!header.IsInitialized)
        {
            throw new ArgumentException("초기화되지 않은 MassiveRoiModelHeader(default)입니다.", nameof(header));
        }

        ArgumentNullException.ThrowIfNull(sectors);
        ArgumentNullException.ThrowIfNull(canvasAnchors);
        if (sectors.Length == 0)
        {
            throw new ArgumentException("섹터 테이블이 비어 있습니다.", nameof(sectors));
        }

        FilePath = filePath;
        Header = header;
        _sectors = sectors;
        _canvasAnchors = canvasAnchors;
        LoadElapsedMilliseconds = loadElapsedMilliseconds;
    }

    public string FilePath { get; }

    public MassiveRoiModelHeader Header { get; }

    public IReadOnlyList<MassiveRoiEntry> CanvasAnchors => _canvasAnchors;

    public double LoadElapsedMilliseconds { get; }

    public Guid GetRoiId(long index)
    {
        if ((index < 0) || (index >= Header.TotalRoiCount))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "ROI 인덱스가 모델 범위를 벗어났습니다.");
        }

        return CreateDeterministicGuid(Header.ModelNumber, Header.MasterSeed, index);
    }

    public bool TryResolveIndex(Guid id, out long index)
    {
        return TryExtractIndexFromGuid(id, Header.ModelNumber, Header.MasterSeed, Header.TotalRoiCount, out index);
    }

    public MassiveRoiEntry GetEntry(long index)
    {
        if ((index < 0) || (index >= Header.TotalRoiCount))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "ROI 인덱스가 모델 범위를 벗어났습니다.");
        }

        if (index < _canvasAnchors.Length)
        {
            return _canvasAnchors[index];
        }

        return ComputeEntryFromSectors(Header, _sectors, index);
    }

    public RoiRows BuildPageRows(
        int pageIndex,
        int pageSize,
        IReadOnlyDictionary<Guid, RoiAlgorithmAssignment>? overriddenAssignments = null)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "페이지 번호는 0 이상이어야 합니다.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "페이지 크기는 1 이상이어야 합니다.");
        }

        long startIndex = (long)pageIndex * pageSize;
        if (startIndex >= Header.TotalRoiCount)
        {
            return new RoiRows([]);
        }

        int count = (int)Math.Min(pageSize, Header.TotalRoiCount - startIndex);
        RoiRow[] rows = new RoiRow[count];
        bool hasOverrides = (overriddenAssignments is not null) && (overriddenAssignments.Count > 0);

        for (int i = 0; i < count; i++)
        {
            MassiveRoiEntry entry = GetEntry(startIndex + i);
            RoiRow row = entry.ToRoiRow();
            if (hasOverrides && overriddenAssignments!.TryGetValue(entry.Id, out RoiAlgorithmAssignment? custom))
            {
                row = row with
                {
                    AlgorithmBadge = custom.AlgorithmBadge,
                    AlgorithmAccent = custom.AlgorithmAccent,
                    VerdictBadge = custom.VerdictBadge,
                    VerdictAccent = custom.VerdictAccent,
                    PatternThumbnail = custom.PatternThumbnail
                };
            }

            rows[i] = row;
        }

        return new RoiRows(rows);
    }

    internal static MassiveRoiEntry ComputeEntryFromSectors(
        in MassiveRoiModelHeader header,
        ReadOnlySpan<MassiveRoiSectorRecord> sectors,
        long index)
    {
        int sectorIndex = (int)((ulong)index % (ulong)sectors.Length);
        MassiveRoiSectorRecord sector = sectors[sectorIndex];

        ulong mix0 = Mix64(sector.SectorSeed ^ ((ulong)index * SplitMixGamma) ^ header.MasterSeed);
        ulong mix1 = Mix64(mix0 + SplitMixGamma);
        ulong mix2 = Mix64(mix1 + SplitMixGamma);

        double uX = (ToUnitDouble(mix0) * BipolarMultiplier) - MinimumSpanPixels;
        double uY = (ToUnitDouble(mix1) * BipolarMultiplier) - MinimumSpanPixels;
        double uSize = ToUnitDouble(mix2);
        double uAngle = ToUnitDouble(Mix64(mix2 ^ (ulong)index));

        double width = Math.Round(MinRoiWidth + (uSize * MaxRoiWidthSpan), DimensionRoundingDigits);
        int shapeSelector = (int)((sector.ShapeVariant + (ulong)index + (mix1 & ShapeJitterMask)) % ShapeKindModulus);
        RoiShapeKind shapeKind = shapeSelector switch
        {
            1 => RoiShapeKind.Circle,
            2 => RoiShapeKind.Triangle,
            _ => RoiShapeKind.Rectangle
        };

        double height = shapeKind == RoiShapeKind.Circle
            ? width
            : Math.Round(MinRoiHeight + (ToUnitDouble(mix0 ^ mix2) * MaxRoiHeightSpan), DimensionRoundingDigits);

        double minX = MarginPixels + (width * HalfFactor);
        double maxX = Math.Max(minX + MinimumSpanPixels, header.ImageWidth - MarginPixels - (width * HalfFactor));
        double minY = MarginPixels + (height * HalfFactor);
        double maxY = Math.Max(minY + MinimumSpanPixels, header.ImageHeight - MarginPixels - (height * HalfFactor));

        double rawX = sector.AnchorX + (uX * sector.SpreadRadius);
        double rawY = sector.AnchorY + (uY * sector.SpreadRadius);

        // Wrap-around inside interior bounds so every single ROI has a distinct valid random coordinate inside the image
        double spanX = Math.Max(MinimumSpanPixels, maxX - minX);
        double spanY = Math.Max(MinimumSpanPixels, maxY - minY);
        double normX = (((rawX - minX) % spanX) + spanX) % spanX;
        double normY = (((rawY - minY) % spanY) + spanY) % spanY;
        double centerX = Math.Round(minX + normX, CoordinateRoundingDigits);
        double centerY = Math.Round(minY + normY, CoordinateRoundingDigits);

        double angle = shapeKind == RoiShapeKind.Circle
            ? 0.0
            : Math.Round((uAngle * MaxRotationDegrees) - (MaxRotationDegrees * HalfFactor), DimensionRoundingDigits);

        RoiGeometry geometry = new RoiGeometry(new PointD(centerX, centerY), width, height, angle, ResolveRoiShape(shapeKind));

        int algorithmSlot = (int)((sector.BaseAlgorithmSlot + (ulong)index + (mix0 >> AlgorithmMixShift)) % TotalAlgorithmCatalogCount);
        var alg = AlgorithmCatalog[algorithmSlot];

        Guid id = CreateDeterministicGuid(header.ModelNumber, header.MasterSeed, index);
        string name = $"M{header.ModelNumber}-ROI #{index + 1:000,000,000}";

        return MassiveRoiEntry.Create(
            index,
            id,
            name,
            geometry,
            alg.Tool,
            alg.Pattern,
            alg.Suite,
            alg.Badge,
            alg.Accent,
            alg.Summary);
    }

    internal static Guid CreateDeterministicGuid(int modelNumber, ulong masterSeed, long index)
    {
        Span<byte> bytes = stackalloc byte[GuidByteLength];
        ulong high = GuidNamespaceTag ^ ((ulong)(uint)modelNumber << GuidModelNumberShift) ^ (masterSeed & Low32BitMask);
        ulong low = (ulong)index ^ ((masterSeed >> SeedHighMaskShift) & Upper16BitMask);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[..HalfGuidByteLength], high);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[HalfGuidByteLength..GuidByteLength], low);
        return new Guid(bytes);
    }

    internal static bool TryExtractIndexFromGuid(Guid id, int modelNumber, ulong masterSeed, long totalRoiCount, out long index)
    {
        Span<byte> bytes = stackalloc byte[GuidByteLength];
        id.TryWriteBytes(bytes);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[..HalfGuidByteLength]);
        ulong expectedHigh = GuidNamespaceTag ^ ((ulong)(uint)modelNumber << GuidModelNumberShift) ^ (masterSeed & Low32BitMask);
        if (high != expectedHigh)
        {
            index = -1;
            return false;
        }

        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(bytes[HalfGuidByteLength..GuidByteLength]);
        ulong rawIndex = low ^ ((masterSeed >> SeedHighMaskShift) & Upper16BitMask);
        if (rawIndex >= (ulong)totalRoiCount)
        {
            index = -1;
            return false;
        }

        index = (long)rawIndex;
        return true;
    }

    internal static ulong Mix64(ulong z)
    {
        z = (z ^ (z >> Shift30)) * SplitMixMultiplierA;
        z = (z ^ (z >> Shift27)) * SplitMixMultiplierB;
        return z ^ (z >> Shift31);
    }

    internal static double ToUnitDouble(ulong value)
    {
        return (value >> FloatMantissaShift) * UnitDoubleScale;
    }

    internal static RoiShape ResolveRoiShape(RoiShapeKind kind)
    {
        return kind switch
        {
            RoiShapeKind.Circle => RoiShape.Circle,
            RoiShapeKind.Triangle => RoiShape.Triangle,
            _ => RoiShape.Rectangle
        };
    }

    private static (InspectionToolKind Tool, PatternMatchingMethod? Pattern, InspectionSuiteAlgorithmKind? Suite, string Badge, string Accent, string Summary)[] BuildAlgorithmCatalog()
    {
        List<(InspectionToolKind, PatternMatchingMethod?, InspectionSuiteAlgorithmKind?, string, string, string)> list = new(TotalAlgorithmCatalogCount);

        PatternMatchingMethod[] patternMethods =
        [
            PatternMatchingMethod.Shape,
            PatternMatchingMethod.PatMax,
            PatternMatchingMethod.CpuLine2D,
            PatternMatchingMethod.CpuSubNcc,
            PatternMatchingMethod.OpenCvTemplate
        ];

        foreach (PatternMatchingMethod pm in patternMethods)
        {
            string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.Pattern, pm, null);
            string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.Pattern, pm, null);
            list.Add((InspectionToolKind.Pattern, pm, null, badge, accent, $"{badge} · 회전/스케일 불변 템플릿 정합"));
        }

        {
            string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.PrimitiveShapes, null, null);
            string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.PrimitiveShapes, null, null);
            list.Add((InspectionToolKind.PrimitiveShapes, null, null, badge, accent, $"{badge} · 다중 기하 도형/마크 검출"));
        }

        {
            string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.EdgeFewShot, null, null);
            string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.EdgeFewShot, null, null);
            list.Add((InspectionToolKind.EdgeFewShot, null, null, badge, accent, $"{badge} · 소량 정상/불량 에지 특징 분류"));
        }

        InspectionSuiteAlgorithmKind[] suiteKinds = Enum.GetValues<InspectionSuiteAlgorithmKind>();
        foreach (InspectionSuiteAlgorithmKind kind in suiteKinds)
        {
            string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, kind);
            string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, kind);
            InspectionSuiteCategory cat = InspectionSuiteViewModel.ResolveCategoryForAlgorithm(kind);
            list.Add((InspectionToolKind.InspectionSuite, null, kind, badge, accent, $"[{cat}] {badge} 정밀 검사"));
        }

        return list.ToArray();
    }
}
