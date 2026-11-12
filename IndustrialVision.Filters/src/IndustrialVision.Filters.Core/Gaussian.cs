namespace IndustrialVision.Filters.Core;

public sealed record Gaussian : FilterSpec
{
    public override int Radius
    {
        get;
    }
    public double Sigma
    {
        get;
    }

    public Gaussian(int radius, double sigma)
    {
        if (radius < 1 || radius > 4096 || !double.IsFinite(sigma) || sigma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        Radius = radius;
        Sigma = sigma;
    }

    public float[] CreateKernel()
    {
        var kernel = new float[(2 * Radius) + 1];
        double sum = 0;
        for (var k = -Radius; k <= Radius; k++)
        {
            sum += Math.Exp(-0.5 * (k / Sigma) * (k / Sigma));
        }
        for (var k = -Radius; k <= Radius; k++)
        {
            kernel[k + Radius] = (float)(Math.Exp(-0.5 * (k / Sigma) * (k / Sigma)) / sum);
        }
        return kernel;
    }
}
