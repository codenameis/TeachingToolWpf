// File: src/TeachingToolWpf.Presentation/Models/RoiAlgorithmAssignment.cs
using System;
using System.Windows.Media;
using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.Models;

/// <summary>개별 ROI에 지정·학습된 검사 알고리즘 정보와 최근 판정 요약.</summary>
public sealed record RoiAlgorithmAssignment(
    Guid RoiId,
    string RoiName,
    InspectionToolKind Tool,
    PatternMatchingMethod? PatternMethod,
    InspectionSuiteAlgorithmKind? SuiteAlgorithm,
    string AlgorithmBadge,
    string AlgorithmAccent,
    string ParameterSummary,
    string VerdictBadge,
    string VerdictAccent,
    string LastResultSummary,
    ImageSource? PatternThumbnail = null)
{
    public bool HasPatternThumbnail => PatternThumbnail is not null;
    public static string FormatAlgorithmBadge(
        InspectionToolKind tool,
        PatternMatchingMethod? patternMethod = null,
        InspectionSuiteAlgorithmKind? suiteAlgorithm = null)
    {
        return tool switch
        {
            InspectionToolKind.Pattern => patternMethod switch
            {
                PatternMatchingMethod.PatMax => "패턴 · PatMax",
                PatternMatchingMethod.CpuLine2D => "패턴 · LINE-2D",
                PatternMatchingMethod.CpuSubNcc => "패턴 · Sub-NCC",
                PatternMatchingMethod.OpenCvTemplate => "패턴 · OpenCV",
                _ => "패턴 · 기하형상"
            },
            InspectionToolKind.PrimitiveShapes => "도형·마크 검출",
            InspectionToolKind.EdgeFewShot => "Few-Shot 분류",
            InspectionToolKind.InspectionSuite => suiteAlgorithm switch
            {
                InspectionSuiteAlgorithmKind.Metrology => "스위트 · 캘리퍼",
                InspectionSuiteAlgorithmKind.GoldenVariation => "스위트 · 골든변이",
                InspectionSuiteAlgorithmKind.BlobRle => "스위트 · 블롭RLE",
                InspectionSuiteAlgorithmKind.Curvilinear => "스위트 · 비드능선",
                InspectionSuiteAlgorithmKind.FewShot => "스위트 · FewShot",
                InspectionSuiteAlgorithmKind.PatchAnomaly => "스위트 · PaDiM",
                InspectionSuiteAlgorithmKind.Calibration => "스위트 · 호모그래피",
                InspectionSuiteAlgorithmKind.CornerAlignment => "스위트 · 코너정합",
                InspectionSuiteAlgorithmKind.IndustrialOcrOcv => "스위트 · OCR/OCV",
                InspectionSuiteAlgorithmKind.LightweightOcr => "스위트 · 경량OCR",
                InspectionSuiteAlgorithmKind.AutoTeaching => "스위트 · 자동티칭",
                InspectionSuiteAlgorithmKind.CpuDeepOcr => "스위트 · 신경망OCR",
                InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon => "전처리 · 조명/재구성",
                InspectionSuiteAlgorithmKind.AdvancedMetrologyEllipse => "스위트 · 타원/복합캘리퍼",
                InspectionSuiteAlgorithmKind.AdvancedBlobWatershed => "스위트 · 고급블롭/분리",
                InspectionSuiteAlgorithmKind.DataMatrix2DReader => "스위트 · DataMatrix",
                _ => "16종 정밀 스위트"
            },
            _ => "미지정"
        };
    }

    public static string GetAlgorithmAccent(
        InspectionToolKind tool,
        PatternMatchingMethod? patternMethod = null,
        InspectionSuiteAlgorithmKind? suiteAlgorithm = null)
    {
        _ = patternMethod;
        _ = suiteAlgorithm;
        return tool switch
        {
            InspectionToolKind.Pattern => "#38BDF8",
            InspectionToolKind.PrimitiveShapes => "#2DD4BF",
            InspectionToolKind.EdgeFewShot => "#C084FC",
            InspectionToolKind.InspectionSuite => "#818CF8",
            _ => "#64748B"
        };
    }
}
