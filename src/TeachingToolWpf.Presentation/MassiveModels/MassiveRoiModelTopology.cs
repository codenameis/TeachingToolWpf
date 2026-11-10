// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiModelTopology.cs
namespace TeachingToolWpf.MassiveModels;

/// <summary>1억 개 랜덤 ROI 모델(5종)의 공간 분포 토폴로지 구분.</summary>
public enum MassiveRoiModelTopology : byte
{
    UniformFullScatter = 1,
    MultiClusterWafer = 2,
    ConcentricRingArray = 3,
    DiagonalSpiralWave = 4,
    StratifiedCheckerJitter = 5
}
