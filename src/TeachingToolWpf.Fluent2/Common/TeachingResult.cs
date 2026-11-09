// File: Common/TeachingResult.cs
using System;

namespace TeachingToolWpf.Fluent2.Common;

public readonly record struct TeachingResult<T>
{
    private readonly T? _value;

    public bool IsSuccess { get; }

    public string ErrorMessage { get; }

    private TeachingResult(bool isSuccess, T? value, string errorMessage)
    {
        IsSuccess = isSuccess;
        _value = value;
        ErrorMessage = errorMessage;
    }

    public T Value => IsSuccess && (_value is not null)
        ? _value
        : throw new InvalidOperationException(string.IsNullOrEmpty(ErrorMessage) ? "TeachingResult does not hold a valid value." : ErrorMessage);

    public static TeachingResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new TeachingResult<T>(true, value, string.Empty);
    }

    public static TeachingResult<T> Failure(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("Failure message must not be empty.", nameof(errorMessage));
        }

        return new TeachingResult<T>(false, default, errorMessage);
    }
}
