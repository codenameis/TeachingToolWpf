// File: src/TeachingToolWpf.Presentation/MassiveModels/MassiveRoiEntry.cs
using System;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionGeometry;

namespace TeachingToolWpf.MassiveModels;

/// <summary>1억 개 랜덤 ROI 모델에서 O(1)로 복원되는 단일 ROI의 기하·알고리즘·판정 명세.</summary>
public readonly record struct MassiveRoiEntry
{
    private readonly bool _isInitialized;

    private MassiveRoiEntry(
        long index,
        Guid id,
        string name,
        RoiGeometry geometry,
        InspectionToolKind toolKind,
        PatternMatchingMethod? patternMethod,
        InspectionSuiteAlgorithmKind? suiteAlgorithm,
        string algorithmBadge,
        string algorithmAccent,
        string parameterSummary)
    {
        Index = index;
        Id = id;
        Name = name;
        Geometry = geometry;
        ToolKind = toolKind;
        PatternMethod = patternMethod;
        SuiteAlgorithm = suiteAlgorithm;
        AlgorithmBadge = algorithmBadge;
        AlgorithmAccent = algorithmAccent;
        ParameterSummary = parameterSummary;
        _isInitialized = true;
    }

    public bool IsInitialized => _isInitialized;

    public long Index { get; }

    public Guid Id { get; }

    public string Name { get; }

    public RoiGeometry Geometry { get; }

    public InspectionToolKind ToolKind { get; }

    public PatternMatchingMethod? PatternMethod { get; }

    public InspectionSuiteAlgorithmKind? SuiteAlgorithm { get; }

    public string AlgorithmBadge { get; }

    public string AlgorithmAccent { get; }

    public string ParameterSummary { get; }

    public static MassiveRoiEntry Create(
        long index,
        Guid id,
        string name,
        RoiGeometry geometry,
        InspectionToolKind toolKind,
        PatternMatchingMethod? patternMethod,
        InspectionSuiteAlgorithmKind? suiteAlgorithm,
        string algorithmBadge,
        string algorithmAccent,
        string parameterSummary)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "ROI 인덱스는 0 이상이어야 합니다.");
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("ROI 식별자는 빈 Guid일 수 없습니다.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmBadge);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmAccent);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterSummary);

        return new MassiveRoiEntry(
            index,
            id,
            name,
            geometry,
            toolKind,
            patternMethod,
            suiteAlgorithm,
            algorithmBadge,
            algorithmAccent,
            parameterSummary);
    }

    public RoiAlgorithmAssignment ToAssignment()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("초기화되지 않은 MassiveRoiEntry(default)에서는 알고리즘 할당을 생성할 수 없습니다.");
        }

        return new RoiAlgorithmAssignment(
            RoiId: Id,
            RoiName: Name,
            Tool: ToolKind,
            PatternMethod: PatternMethod,
            SuiteAlgorithm: SuiteAlgorithm,
            AlgorithmBadge: AlgorithmBadge,
            AlgorithmAccent: AlgorithmAccent,
            ParameterSummary: ParameterSummary,
            VerdictBadge: "모델할당",
            VerdictAccent: AlgorithmAccent,
            LastResultSummary: $"{AlgorithmBadge} · 좌표({Geometry.Center.X:F1}, {Geometry.Center.Y:F1}) 랜덤 모델 로드됨");
    }

    public RoiRow ToRoiRow()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("초기화되지 않은 MassiveRoiEntry(default)에서는 RoiRow를 생성할 수 없습니다.");
        }

        string shapeGlyph = Geometry.Shape.Kind switch
        {
            RoiShapeKind.Circle => "◯",
            RoiShapeKind.Triangle => "△",
            RoiShapeKind.Polygon => "⬠",
            _ => "▭"
        };

        string kindText = $"랜덤({Geometry.Center.X:F0},{Geometry.Center.Y:F0}) · {Geometry.ShapeName}";
        return new RoiRow(Id, Name, kindText, 0)
        {
            ShapeGlyph = shapeGlyph,
            AlgorithmBadge = AlgorithmBadge,
            AlgorithmAccent = AlgorithmAccent,
            VerdictBadge = "모델할당",
            VerdictAccent = AlgorithmAccent
        };
    }
}
