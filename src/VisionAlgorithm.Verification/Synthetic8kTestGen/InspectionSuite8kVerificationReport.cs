using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Calibration;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Curvilinear;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.GoldenVariation;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatchAnomaly;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Quantitative verification report produced by <see cref="InspectionSuite8kTestRunner"/> across all 7 algorithms on the 8K scene.
/// </summary>
public sealed record InspectionSuite8kVerificationReport(
    double SceneGenerationMilliseconds,
    float CaliperWidthErrorPixels,
    float LineAngleErrorDegrees,
    float CircleRadiusErrorPixels,
    float CircleCenterErrorPixels,
    int GoldenNormalFalseDefectPixels,
    int GoldenDefectiveDetectedPixels,
    long RleCleanedBlobCount,
    float RleBlobCentroidErrorPixels,
    float StegerCenterlineRmsErrorPixels,
    bool ContinuousBeadPassed,
    int GappedBeadDetectedGapCount,
    bool FewShotClassifiedKnownClassesCorrectly,
    bool FewShotRejectedOodAnomaly,
    bool PadimAcceptedNormalDie,
    bool PadimDetectedDefectiveDie,
    float PadimLocalizationErrorPixels,
    double HomographyRmsErrorMm,
    float LensUndistortRoundTripErrorPixels,
    double TotalAlgorithmExecutionMilliseconds);
