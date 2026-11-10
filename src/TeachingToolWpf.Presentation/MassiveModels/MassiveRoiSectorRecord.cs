// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiSectorRecord.cs
using System;

namespace TeachingToolWpf.MassiveModels;

/// <summary>디스크 .vmodel 파일에 기록되는 공간 섹터 1개의 바이너리 시드 및 중심 앵커 오프셋 레코드.</summary>
public readonly record struct MassiveRoiSectorRecord
{
    public const int BinaryByteSize = 24; // [Perf Why]: Fixed 24-byte aligned binary record (float CenterX, float CenterY, float SpreadRadius, ulong SectorSeed, byte BaseAlgorithmSlot, byte ShapeMask, ushort Reserved) for single-syscall bulk read.

    private readonly bool _isInitialized;

    private MassiveRoiSectorRecord(
        float anchorX,
        float anchorY,
        float spreadRadius,
        ulong sectorSeed,
        byte baseAlgorithmSlot,
        byte shapeVariant)
    {
        AnchorX = anchorX;
        AnchorY = anchorY;
        SpreadRadius = spreadRadius;
        SectorSeed = sectorSeed;
        BaseAlgorithmSlot = baseAlgorithmSlot;
        ShapeVariant = shapeVariant;
        _isInitialized = true;
    }

    public bool IsInitialized => _isInitialized;

    public float AnchorX { get; }

    public float AnchorY { get; }

    public float SpreadRadius { get; }

    public ulong SectorSeed { get; }

    public byte BaseAlgorithmSlot { get; }

    public byte ShapeVariant { get; }

    public static MassiveRoiSectorRecord Create(
        float anchorX,
        float anchorY,
        float spreadRadius,
        ulong sectorSeed,
        byte baseAlgorithmSlot,
        byte shapeVariant)
    {
        if (!float.IsFinite(anchorX) || !float.IsFinite(anchorY))
        {
            throw new ArgumentOutOfRangeException(nameof(anchorX), "섹터 좌표는 유한한 실수여야 합니다.");
        }

        if (!float.IsFinite(spreadRadius) || (spreadRadius <= 0.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(spreadRadius), spreadRadius, "섹터 분산 반경은 0보다 커야 합니다.");
        }

        return new MassiveRoiSectorRecord(
            anchorX,
            anchorY,
            spreadRadius,
            sectorSeed,
            baseAlgorithmSlot,
            shapeVariant);
    }
}
