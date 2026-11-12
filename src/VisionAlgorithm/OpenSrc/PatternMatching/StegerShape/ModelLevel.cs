using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record ModelLevel
{
    public int Factor { get; init; }
    public ImmutableArray<ModelFeature> Features { get; init; }
    internal double[] FeatureX { get; }
    internal double[] FeatureY { get; }
    internal double[] NormalX { get; }
    internal double[] NormalY { get; }
    internal byte[] QuadrantMask { get; }
    internal double MaxRadius { get; }
    internal double MaxNormalLength { get; }

    public ModelLevel(int Factor, ImmutableArray<ModelFeature> Features)
    {
        this.Factor = Factor;
        this.Features = Features;
        var count = Features.IsDefault ? 0 : Features.Length;
        FeatureX = new double[count];
        FeatureY = new double[count];
        NormalX = new double[count];
        NormalY = new double[count];
        QuadrantMask = new byte[count];
        var maxRadius = 0d;
        var maxNormal = 0d;
        for (var i = 0; i < count; i++)
        {
            var f = Features[i];
            FeatureX[i] = f.X;
            FeatureY[i] = f.Y;
            NormalX[i] = f.NormalX;
            NormalY[i] = f.NormalY;
            QuadrantMask[i] = (byte)(1 << ((f.X >= 0 ? 1 : 0) + (f.Y >= 0 ? 2 : 0)));
            var r = Math.Sqrt((f.X * f.X) + (f.Y * f.Y));
            if (r > maxRadius)
            {
                maxRadius = r;
            }
            var nl = Math.Sqrt((f.NormalX * f.NormalX) + (f.NormalY * f.NormalY));
            if (nl > maxNormal)
            {
                maxNormal = nl;
            }
        }
        MaxRadius = maxRadius;
        MaxNormalLength = maxNormal;
    }

    public void Deconstruct(out int factor, out ImmutableArray<ModelFeature> features)
    {
        factor = Factor;
        features = Features;
    }
}
