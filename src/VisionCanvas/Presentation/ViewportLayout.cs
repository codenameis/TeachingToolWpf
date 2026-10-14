namespace VisionCanvas.Presentation;

/// <summary>Layout values shared with presentation logic without retaining a visual element.</summary>
public sealed class ViewportLayout
{
    public ViewportLayout(double width = 0, double height = 0, double dpi = 1) => Update(width, height, dpi);

    public double Width
    {
        get; private set;
    }
    public double Height
    {
        get; private set;
    }
    public double Dpi { get; private set; } = 1;
    public event Action? Changed;

    public void Update(double width, double height, double dpi)
    {
        if (!double.IsFinite(width) || width < 0 || !double.IsFinite(height) || height < 0 ||
            !double.IsFinite(dpi) || dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport dimensions must be finite and DPI must be positive.");
        }
        if (Width == width && Height == height && Dpi == dpi)
        {
            return;
        }
        Width = width;
        Height = height;
        Dpi = dpi;
        Changed?.Invoke();
    }
}
