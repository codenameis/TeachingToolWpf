using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    public bool IsPaperMethod => Method == PatternMatchingMethod.Shape;
    public bool InspectRotation
    {
        get => IsPaperMethod && ActiveShapeState.Training.InspectRotation;
        set
        {
            if (!IsPaperMethod)
            {
                return;
            }

            ChangeTrainingSettings(ActiveShapeState.Training with
            {
                InspectRotation = value
            });
            Notify(nameof(CanEditSearchAngle));
        }
    }
    public bool CanEditSearchAngle => !InspectRotation;
    public double MinimumAcceptedAngle
    {
        get => ActiveSearchSettings.Rotation.MinimumDegrees;
        set => ChangeSearchSettings(ActiveSearchSettings with
        {
            Rotation = ActiveSearchSettings.Rotation with
            {
                MinimumDegrees = value
            }
        });
    }
    public double MaximumAcceptedAngle
    {
        get => ActiveSearchSettings.Rotation.MaximumDegrees;
        set => ChangeSearchSettings(ActiveSearchSettings with
        {
            Rotation = ActiveSearchSettings.Rotation with
            {
                MaximumDegrees = value
            }
        });
    }

    private RotationAcceptance? ActiveRotationAcceptance => InspectRotation ? ActiveSearchSettings.Rotation : null;

    private static string FormatRotationDecision(ShapeMatch match, RotationAcceptance? rotation, double referenceAngle, bool incomplete)
    {
        if (rotation is null)
        {
            return "";
        }

        var verdict = incomplete ? InspectionVerdict.Warning : InspectionDecision.Evaluate(match, rotation, referenceAngle).Verdict;
        return $"\n{InspectionResultPresentation.Name(verdict)} · 학습 대비 {RotationAcceptance.DeviationDegrees(match.Pose.Angle, referenceAngle):F3}°" +
            $" · 정상 {rotation.MinimumDegrees:G}~{rotation.MaximumDegrees:G}°";
    }
}
