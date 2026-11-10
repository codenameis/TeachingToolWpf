// File: src/VisionAlgorithm/OpenSrc/ToolBlock/RelativeOffsetSpec.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Master teaching reference specification for <see cref="RelativeOffsetCombinerNode"/> defining the nominal
/// local-frame offset $(u_{\text{ref}}, v_{\text{ref}})$, nominal angle $\theta_{\text{ref}}$, and pixel-to-millimeter scale.
/// </summary>
public readonly record struct RelativeOffsetSpec
{
    private readonly bool _isInitialized;

    /// <summary>Gets the master taught local X offset $u_{\text{ref}}$ in pixels inside the reference fixture frame.</summary>
    public double MasterLocalX { get; }

    /// <summary>Gets the master taught local Y offset $v_{\text{ref}}$ in pixels inside the reference fixture frame.</summary>
    public double MasterLocalY { get; }

    /// <summary>Gets the master taught reference angle $\theta_{\text{ref}}$ in radians.</summary>
    public double MasterAngleRad { get; }

    /// <summary>Gets the isotropic calibration scale factor ($\text{mm/px}$).</summary>
    public double MmPerPixel { get; }

    public bool IsValid => _isInitialized && double.IsFinite(MasterLocalX) && double.IsFinite(MasterLocalY) && double.IsFinite(MasterAngleRad) && MmPerPixel > 0.0;

    public RelativeOffsetSpec(
        double masterLocalX,
        double masterLocalY,
        double masterAngleRad,
        double mmPerPixel = 1.0)
    {
        if (!double.IsFinite(masterLocalX) || !double.IsFinite(masterLocalY) || !double.IsFinite(masterAngleRad))
        {
            throw new ArgumentOutOfRangeException(nameof(masterLocalX), "Master reference coordinates and angle must be finite.");
        }

        if (!double.IsFinite(mmPerPixel) || mmPerPixel <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(mmPerPixel), "mmPerPixel must be positive and finite.");
        }

        MasterLocalX = masterLocalX;
        MasterLocalY = masterLocalY;
        MasterAngleRad = masterAngleRad;
        MmPerPixel = mmPerPixel;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(RelativeOffsetSpec)} was not initialized via its constructor.");
        }
    }
}
