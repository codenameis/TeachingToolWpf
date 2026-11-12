namespace IndustrialVision.Filters.Core;

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
