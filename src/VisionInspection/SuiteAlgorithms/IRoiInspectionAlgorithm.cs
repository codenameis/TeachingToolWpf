using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 개방-폐쇄 원칙(OCP)에 따라 단일 검사 스위트 알고리즘의 메타데이터·기하 가이드·실행 로직을 캡슐화하는 계약.
/// 새 알고리즘 추가 시 이 인터페이스 구현체 1개와 <see cref="InspectionAlgorithmRegistry"/> 등록 1줄만 추가한다.
/// </summary>
public interface IRoiInspectionAlgorithm
{
    InspectionSuiteAlgorithmKind Kind { get; }

    InspectionSuiteCategory Category { get; }

    string ShortName { get; }

    string ShortPillLabel { get; }

    string AssignmentBadge { get; }

    string Description { get; }

    string UsageGuide { get; }

    string SampleSceneSlug { get; }

    string SampleSceneTitle { get; }

    bool UsesReferenceRois => false;

    bool SuppressesGuideOverlayWhenEvaluated => false;

    InspectionAlgorithmHelpSpec HelpSpec { get; }

    string GetUsageGuide(InspectionSuiteParameters parameters) => UsageGuide;

    string FormatParameterSummary(InspectionSuiteParameters parameters);

    ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters);

    ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context);
}
