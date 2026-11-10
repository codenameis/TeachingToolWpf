// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJThresholdSpec.cs
using System;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable specification for ImageJ "Image &gt; Adjust &gt; Threshold" including auto-threshold method and foreground polarity.
/// </summary>
public readonly record struct ImageJThresholdSpec
{
    private const byte DefaultLowerThreshold = 128;

    private readonly bool _isInitialized;
    private readonly ImageJAutoThresholdMethod _method;
    private readonly byte _minThreshold;
    private readonly byte _maxThreshold;
    private readonly bool _isDarkBackground;

    // [Domain Why]: Guard against uninitialized default struct bypassing threshold invariants.
    public bool IsInitialized => _isInitialized;

    public ImageJAutoThresholdMethod Method => _isInitialized
        ? _method
        : throw new InvalidOperationException("ImageJThresholdSpec was not initialized via Create.");

    public byte MinThreshold => _isInitialized
        ? _minThreshold
        : throw new InvalidOperationException("ImageJThresholdSpec was not initialized via Create.");

    public byte MaxThreshold => _isInitialized
        ? _maxThreshold
        : throw new InvalidOperationException("ImageJThresholdSpec was not initialized via Create.");

    public bool IsDarkBackground => _isInitialized
        ? _isDarkBackground
        : throw new InvalidOperationException("ImageJThresholdSpec was not initialized via Create.");

    private ImageJThresholdSpec(
        ImageJAutoThresholdMethod method,
        byte minThreshold,
        byte maxThreshold,
        bool isDarkBackground)
    {
        _isInitialized = true;
        _method = method;
        _minThreshold = Math.Min(minThreshold, maxThreshold);
        _maxThreshold = Math.Max(minThreshold, maxThreshold);
        _isDarkBackground = isDarkBackground;
    }

    public static ImageJThresholdSpec Create(
        ImageJAutoThresholdMethod method,
        byte minThreshold = DefaultLowerThreshold,
        byte maxThreshold = byte.MaxValue,
        bool isDarkBackground = true) => new(method, minThreshold, maxThreshold, isDarkBackground);

    public static ImageJThresholdSpec DefaultOtsu => new(
        ImageJAutoThresholdMethod.Otsu,
        DefaultLowerThreshold,
        byte.MaxValue,
        isDarkBackground: true);
}
