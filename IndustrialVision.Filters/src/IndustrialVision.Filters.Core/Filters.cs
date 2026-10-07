namespace IndustrialVision.Filters.Core;

public abstract record FilterSpec
{
    public abstract int Radius
    {
        get;
    }
}

public enum FlatFieldKind
{
    RawReference, ReciprocalGain
}

/// <summary>좌표/노출/버전을 고정한 보정 자료. 생성 시 복사하여 외부 변경을 차단한다.</summary>
public sealed class CalibrationMap
{
    private readonly float[] _values;
    public ImageDescriptor Descriptor
    {
        get;
    }
    public long Version
    {
        get;
    }
    public ReadOnlySpan<float> Values => _values;
    public long Bytes => (long)_values.Length * sizeof(float);

    public CalibrationMap(ImageDescriptor descriptor, ReadOnlySpan<float> values, long version)
    {
        if (version <= 0 || descriptor.PixelCount != values.Length)
        {
            throw new ArgumentException("Calibration size/version mismatch.");
        }
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentException("Calibration values must be finite.");
            }
        }
        Descriptor = descriptor;
        Version = version;
        _values = values.ToArray();
    }

    public void ValidateFor(ImageDescriptor input)
    {
        if (Descriptor.Width != input.Width || Descriptor.Height != input.Height ||
            Descriptor.OriginX != input.OriginX || Descriptor.OriginY != input.OriginY ||
            Descriptor.ExposureKey != input.ExposureKey || Descriptor.PixelSizeMicrometers != input.PixelSizeMicrometers)
        {
            throw new ArgumentException("Calibration coordinates, size, exposure or pixel size mismatch.");
        }
    }
}

public sealed record FlatField : FilterSpec
{
    public CalibrationMap Map
    {
        get;
    }
    public CalibrationMap? Dark
    {
        get;
    }
    public FlatFieldKind Kind
    {
        get;
    }
    public float Scale
    {
        get;
    }
    public float MinimumDenominator
    {
        get;
    }
    public override int Radius => 0;

    public FlatField(CalibrationMap map, FlatFieldKind kind = FlatFieldKind.RawReference,
        CalibrationMap? dark = null, float scale = 1, float minimumDenominator = 0.000001f)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!Enum.IsDefined(kind) || !float.IsFinite(scale) || scale <= 0 ||
            !float.IsFinite(minimumDenominator) || minimumDenominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
        Map = map;
        Dark = dark;
        Kind = kind;
        Scale = scale;
        MinimumDenominator = minimumDenominator;
    }
}

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

public enum MorphologyKind
{
    Erosion, Dilation, Opening, Closing, WhiteTopHat, BlackHat
}

public sealed record Morphology : FilterSpec
{
    public override int Radius
    {
        get;
    }
    public MorphologyKind Kind
    {
        get;
    }
    public int Halo => Kind is MorphologyKind.Erosion or MorphologyKind.Dilation ? Radius : checked(2 * Radius);

    public Morphology(MorphologyKind kind, int radius)
    {
        if (!Enum.IsDefined(kind) || radius < 1 || radius > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        Kind = kind;
        Radius = radius;
    }
}

/// <summary>유효 픽셀에 value > Level이면 255, 아니면 0. 출력은 Gray8.</summary>
public sealed record Threshold : FilterSpec
{
    public float Level
    {
        get;
    }
    public override int Radius => 0;

    public Threshold(float level)
    {
        if (!float.IsFinite(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        Level = level;
    }
}

public sealed class Recipe
{
    public IReadOnlyList<FilterSpec> Filters
    {
        get;
    }
    public long Version
    {
        get;
    }

    public Recipe(long version, params FilterSpec[] filters)
    {
        if (version <= 0 || filters.Length == 0 || filters.Any(f => f is null))
        {
            throw new ArgumentException("A positive version and nonempty recipe are required.");
        }
        Filters = Array.AsReadOnly((FilterSpec[])filters.Clone());
        Version = version;
    }
}
