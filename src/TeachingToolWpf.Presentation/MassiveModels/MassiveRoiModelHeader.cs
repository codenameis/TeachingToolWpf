// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiModelHeader.cs
using System;

namespace TeachingToolWpf.MassiveModels;

/// <summary>디스크에 저장되는 1억 ROI 바이너리 모델(.vmodel)의 헤더 메타데이터.</summary>
public readonly record struct MassiveRoiModelHeader
{
    public const uint BinaryMagic = 0x4C444D56u; // [Domain Why]: ASCII "VMDL" (Vision Massive Model) little-endian magic signature.
    public const int CurrentFormatVersion = 1; // [Domain Why]: Version 1 binary layout with explicit spatial sector table + anchor table.
    public const long DefaultHundredMillionRoiCount = 100_000_000L; // [Domain Why]: User-specified 100,000,000 ROIs per model file.

    private readonly bool _isInitialized;

    private MassiveRoiModelHeader(
        int modelNumber,
        string modelName,
        MassiveRoiModelTopology topology,
        long totalRoiCount,
        int imageWidth,
        int imageHeight,
        ulong masterSeed,
        int spatialSectorCount,
        int canvasAnchorCount)
    {
        ModelNumber = modelNumber;
        ModelName = modelName;
        Topology = topology;
        TotalRoiCount = totalRoiCount;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        MasterSeed = masterSeed;
        SpatialSectorCount = spatialSectorCount;
        CanvasAnchorCount = canvasAnchorCount;
        _isInitialized = true;
    }

    public bool IsInitialized => _isInitialized;

    public int ModelNumber { get; }

    public string ModelName { get; }

    public MassiveRoiModelTopology Topology { get; }

    public long TotalRoiCount { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public ulong MasterSeed { get; }

    public int SpatialSectorCount { get; }

    public int CanvasAnchorCount { get; }

    public static MassiveRoiModelHeader Create(
        int modelNumber,
        string modelName,
        MassiveRoiModelTopology topology,
        long totalRoiCount,
        int imageWidth,
        int imageHeight,
        ulong masterSeed,
        int spatialSectorCount,
        int canvasAnchorCount)
    {
        if (modelNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modelNumber), modelNumber, "모델 번호는 1 이상이어야 합니다.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (!Enum.IsDefined(topology))
        {
            throw new ArgumentOutOfRangeException(nameof(topology), topology, "유효하지 않은 모델 토폴로지입니다.");
        }

        if (totalRoiCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRoiCount), totalRoiCount, "총 ROI 개수는 1 이상이어야 합니다.");
        }

        if (imageWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), imageWidth, "이미지 너비는 1 이상이어야 합니다.");
        }

        if (imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight), imageHeight, "이미지 높이는 1 이상이어야 합니다.");
        }

        if (spatialSectorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spatialSectorCount), spatialSectorCount, "공간 섹터 개수는 1 이상이어야 합니다.");
        }

        if (canvasAnchorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasAnchorCount), canvasAnchorCount, "캔버스 앵커 개수는 1 이상이어야 합니다.");
        }

        return new MassiveRoiModelHeader(
            modelNumber,
            modelName,
            topology,
            totalRoiCount,
            imageWidth,
            imageHeight,
            masterSeed,
            spatialSectorCount,
            canvasAnchorCount);
    }
}
