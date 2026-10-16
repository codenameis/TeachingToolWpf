namespace VisionAnomaly.Tests;

public sealed class AlarmGateTests
{
    [Fact]
    public void OneBadFrameRaisesAppearanceAndRequiresNormalRecoveryToRearm()
    {
        var gate = new AlarmGate();
        Assert.Contains(AnomalyKind.Appearance, gate.Update(Prediction(2, 0), 0));
        Assert.Empty(gate.Update(Prediction(2, 0), .2));
        for (var index = 1; index <= 3; index++)
        {
            Assert.Empty(gate.Update(Prediction(.4f, 0), .2 + (index * .2)));
        }

        Assert.Contains(AnomalyKind.Appearance, gate.Update(Prediction(2, 0), 1));
    }

    [Fact]
    public void MotionNeedsContinuousTimeAndDoesNotBridgeMissingFrames()
    {
        var gate = new AlarmGate();
        Assert.Empty(gate.Update(Prediction(0, 2), 0));
        Assert.Empty(gate.Update(Prediction(0, 2), .3));
        Assert.Contains(AnomalyKind.Motion, gate.Update(Prediction(0, 2), .7));
        gate.Reset();
        Assert.Empty(gate.Update(Prediction(0, 2), 0));
        Assert.Empty(gate.Update(Prediction(0, 2), 4));
        Assert.Empty(gate.Update(Prediction(0, 0), 4.2));
    }

    [Fact]
    public void RecoveryMustBeConsecutiveAndThresholdMultiplierAppliesToBothKinds()
    {
        var gate = new AlarmGate();
        Assert.Contains(AnomalyKind.Appearance, gate.Update(Prediction(2, 0), 0));
        gate.Update(Prediction(.4f, 0), .1);
        gate.Update(Prediction(.4f, 0), .2);
        gate.Update(Prediction(.9f, 0), .3);
        gate.Update(Prediction(.4f, 0), .4);
        Assert.Empty(gate.Update(Prediction(2, 0), .5));
        gate.Reset();
        Assert.Empty(gate.Update(Prediction(1.5f, 1.5f), 0, 2));
        Assert.Empty(gate.Update(Prediction(1.5f, 1.5f), .7, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Update(Prediction(1, 1), 1, float.NaN));
    }

    private static AnomalyPrediction Prediction(float appearance, float motion) => new(appearance, motion, true, [], 1);
}
