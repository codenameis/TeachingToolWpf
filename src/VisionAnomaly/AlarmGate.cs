namespace VisionAnomaly;

/// <summary>외관은 한 분석 프레임, 움직임은 시간 지속으로 감지한다. 확인 처리와 재무장은 별개다.</summary>
public sealed class AlarmGate
{
    private bool _appearanceLatched, _motionLatched;
    private int _appearanceNormal, _motionNormal, _motionFrames;
    private double? _motionStart;
    private double? _lastTime;

    public IReadOnlyList<AnomalyKind> Update(AnomalyPrediction prediction, double seconds, float threshold = 1)
    {
        if (!double.IsFinite(seconds) || !float.IsFinite(threshold) || threshold is < .5f or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        if (_lastTime is { } last && (seconds <= last || seconds - last > 1))
        {
            Reset();
        }

        _lastTime = seconds;
        var events = new List<AnomalyKind>(2);
        if (prediction.AppearanceRatio >= threshold)
        {
            _appearanceNormal = 0;
            if (!_appearanceLatched)
            {
                events.Add(AnomalyKind.Appearance);
                _appearanceLatched = true;
            }
        }
        else if (prediction.AppearanceRatio < threshold * .8f && ++_appearanceNormal >= 3)
        {
            _appearanceLatched = false;
        }
        else if (prediction.AppearanceRatio >= threshold * .8f)
        {
            _appearanceNormal = 0;
        }

        if (prediction.MotionAvailable && prediction.MotionRatio >= threshold)
        {
            _motionNormal = 0;
            _motionStart ??= seconds;
            _motionFrames++;
            if (!_motionLatched && _motionFrames >= 2 && seconds - _motionStart >= .6)
            {
                events.Add(AnomalyKind.Motion);
                _motionLatched = true;
            }
        }
        else
        {
            _motionStart = null;
            _motionFrames = 0;
            if (!prediction.MotionAvailable || prediction.MotionRatio < threshold * .8f)
            {
                if (++_motionNormal >= 3)
                {
                    _motionLatched = false;
                }
            }

            if (prediction.MotionAvailable && prediction.MotionRatio >= threshold * .8f)
            {
                _motionNormal = 0;
            }
        }
        return events;
    }

    public void Reset()
    {
        _appearanceLatched = _motionLatched = false;
        _appearanceNormal = _motionNormal = _motionFrames = 0;
        _motionStart = _lastTime = null;
    }
}
