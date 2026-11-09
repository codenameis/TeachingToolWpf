// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Domain/FewShotClassId.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

/// <summary>
/// Validated domain identifier for a few-shot classification category, guarded against <c>default(FewShotClassId)</c> bypass.
/// </summary>
public readonly record struct FewShotClassId
{
    public const int UnknownClassValue = -1;

    private readonly bool _isInitialized;

    public int Value { get; }
    public string Name { get; }
    public bool IsInitialized => _isInitialized;
    public bool IsEmpty => !_isInitialized;

    private FewShotClassId(int value, string name, bool isInitialized)
    {
        Value = value;
        Name = name;
        _isInitialized = isInitialized;
    }

    public static FewShotClassId Empty => new(0, string.Empty, isInitialized: false);

    public static FewShotClassId UnknownDefect => new(UnknownClassValue, nameof(UnknownDefect), isInitialized: true);

    public static FewShotClassId Create(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new FewShotClassId(value, name.Trim(), isInitialized: true);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("FewShotClassId must be created via Create() or UnknownDefect rather than default(FewShotClassId).");
        }
    }

    public override string ToString()
        => _isInitialized ? $"{Name}({Value})" : "<Uninitialized>";
}
