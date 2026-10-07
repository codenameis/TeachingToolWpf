using System.Diagnostics;
using System.Runtime.Intrinsics;
using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Cpu;

/// <summary>행/열 작업 분할, 분리 Gaussian과 선형 시간 deque 형태학. plan마다 버퍼를 재사용한다.</summary>
public sealed class CpuProgram : IDisposable
{
    private readonly Recipe _recipe;
    private readonly ImageDescriptor _descriptor;
    private readonly ExecutionOptions _options;
    private readonly PooledBuffer<float>[] _planes;
    private readonly PooledBuffer<int>[] _queues;
    private readonly float[][] _kernels;
    private readonly double[] _stageMilliseconds;
    public ReadOnlyMemory<double> StageMilliseconds => _stageMilliseconds;
    private bool _disposed;
    public long WorkingBytes
    {
        get;
    }

    public static long EstimateBytes(Recipe recipe, ImageDescriptor descriptor, ExecutionOptions options)
    {
        var count = checked((int)descriptor.PixelCount);
        var radius = recipe.Filters.Max(f => f.Radius);
        var queueLength = checked(Math.Max(descriptor.Width, descriptor.Height) + (2 * radius));
        var lanes = Math.Min(options.MaxDegreeOfParallelism, Math.Max(descriptor.Width, descriptor.Height));
        return checked((4 * PooledBuffer<float>.EstimateBytes(count)) +
            (lanes * PooledBuffer<int>.EstimateBytes(queueLength)) +
            recipe.Filters.OfType<Gaussian>().Sum(g => (long)((2 * g.Radius) + 1) * 4));
    }

    public CpuProgram(Recipe recipe, ImageDescriptor descriptor, ExecutionOptions options)
    {
        RecipeValidation.Validate(recipe, descriptor, options);
        _recipe = recipe;
        _descriptor = descriptor;
        _options = options;
        _stageMilliseconds = options.Diagnostics == DiagnosticLevel.Stages ? new double[recipe.Filters.Count] : [];
        var estimate = EstimateBytes(recipe, descriptor, options);
        if (estimate > options.MemoryBudgetBytes)
        {
            throw new MemoryBudgetException($"CPU workspace {estimate} > budget {options.MemoryBudgetBytes}; use tiles.");
        }
        _planes = new PooledBuffer<float>[4];
        _queues = new PooledBuffer<int>[Math.Min(options.MaxDegreeOfParallelism, Math.Max(descriptor.Width, descriptor.Height))];
        _kernels = new float[recipe.Filters.Count][];
        try
        {
            for (var i = 0; i < _planes.Length; i++)
            {
                _planes[i] = new(checked((int)descriptor.PixelCount));
            }
            var queueLength = checked(Math.Max(descriptor.Width, descriptor.Height) + (2 * recipe.Filters.Max(f => f.Radius)));
            for (var i = 0; i < _queues.Length; i++)
            {
                _queues[i] = new(queueLength);
            }
            for (var i = 0; i < _kernels.Length; i++)
            {
                _kernels[i] = recipe.Filters[i] is Gaussian gaussian ? gaussian.CreateKernel() : [];
            }
            WorkingBytes = _planes.Sum(p => p.Bytes) + _queues.Sum(q => q.Bytes) + _kernels.Sum(k => (long)k.Length * 4);
            if (WorkingBytes > options.MemoryBudgetBytes)
            {
                throw new MemoryBudgetException("Actual rented CPU workspace exceeds budget.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Execute(ImageView input, HostImage output, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        var current = _planes[0].Array;
        var next = _planes[1].Array;
        var temporary = _planes[2].Array;
        var auxiliary = _planes[3].Array;
        input.CopyToFloat(current);
        for (var i = 0; i < _recipe.Filters.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var started = _stageMilliseconds.Length > 0 ? Stopwatch.GetTimestamp() : 0;
            switch (_recipe.Filters[i])
            {
                case FlatField flat:
                    Correct(current, next, flat, token);
                    break;
                case Gaussian gaussian:
                    Convolve(current, temporary, _kernels[i], gaussian.Radius, false, token);
                    Convolve(temporary, next, _kernels[i], gaussian.Radius, true, token);
                    break;
                case Morphology morphology:
                    Morph(current, next, temporary, auxiliary, morphology, token);
                    break;
                case Threshold threshold:
                    Rows(token, y =>
                    {
                        var end = (y + 1) * _descriptor.Width;
                        for (var p = y * _descriptor.Width; p < end; p++)
                        {
                            next[p] = float.IsFinite(current[p]) ? (current[p] > threshold.Level ? 255 : 0) : float.NaN;
                        }
                    });
                    break;
                default:
                    throw new NotSupportedException($"Unsupported filter {_recipe.Filters[i].GetType().Name}.");
            }
            (current, next) = (next, current);
            if (_stageMilliseconds.Length > 0)
            {
                _stageMilliseconds[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
        token.ThrowIfCancellationRequested();
        output.WriteFloat(current.AsSpan(0, checked((int)_descriptor.PixelCount)));
    }

    private void Correct(float[] input, float[] output, FlatField flat, CancellationToken token)
    {
        Rows(token, y =>
        {
            var map = flat.Map.Values;
            var dark = flat.Dark is null ? default : flat.Dark.Values;
            for (var i = y * _descriptor.Width; i < (y + 1) * _descriptor.Width; i++)
            {
                var offset = dark.IsEmpty ? 0 : dark[i];
                var denominator = map[i] - offset;
                var value = flat.Kind == FlatFieldKind.RawReference
                    ? (float.IsFinite(denominator) && denominator > flat.MinimumDenominator ? (input[i] - offset) / denominator * flat.Scale : float.NaN)
                    : (map[i] > 0 ? (input[i] - offset) * map[i] * flat.Scale : float.NaN);
                output[i] = float.IsFinite(value) ? value : float.NaN;
            }
        });
    }

    private void Convolve(float[] input, float[] output, float[] kernel, int radius, bool vertical, CancellationToken token)
    {
        var width = _descriptor.Width;
        var height = _descriptor.Height;
        Rows(token, y =>
        {
            var x = 0;
            var vectorStart = vertical ? 0 : Math.Min(radius, width);
            for (; x < vectorStart; x++)
            {
                output[(y * width) + x] = ConvolvePixel(input, kernel, radius, x, y, vertical);
            }
            var vectorEnd = vertical ? width : Math.Max(vectorStart, width - radius);
            if (_options.EnableSimd && Vector256.IsHardwareAccelerated)
            {
                for (; x + Vector256<float>.Count <= vectorEnd; x += Vector256<float>.Count)
                {
                    var sum = Vector256<float>.Zero;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var offset = vertical ? (Math.Clamp(y + k, 0, height - 1) * width) + x : (y * width) + x + k;
                        var samples = Vector256.LoadUnsafe(ref input[offset]);
                        sum = Vector256.Add(sum, Vector256.Multiply(samples, Vector256.Create(kernel[k + radius])));
                    }
                    sum.StoreUnsafe(ref output[(y * width) + x]);
                }
            }
            for (; x < width; x++)
            {
                output[(y * width) + x] = ConvolvePixel(input, kernel, radius, x, y, vertical);
            }
        });
    }

    private float ConvolvePixel(float[] input, float[] kernel, int radius, int x, int y, bool vertical)
    {
        float sum = 0;
        var width = _descriptor.Width;
        for (var k = -radius; k <= radius; k++)
        {
            var index = vertical ? (Math.Clamp(y + k, 0, _descriptor.Height - 1) * width) + x
                : (y * width) + Math.Clamp(x + k, 0, width - 1);
            sum += input[index] * kernel[k + radius];
        }
        return sum;
    }

    private void Morph(float[] input, float[] output, float[] temporary, float[] auxiliary, Morphology filter, CancellationToken token)
    {
        var firstMaximum = filter.Kind is MorphologyKind.Dilation or MorphologyKind.Closing or MorphologyKind.BlackHat;
        Extrema(input, temporary, filter.Radius, false, firstMaximum, token);
        if (filter.Kind is MorphologyKind.Erosion or MorphologyKind.Dilation)
        {
            Extrema(temporary, output, filter.Radius, true, firstMaximum, token);
            return;
        }
        Extrema(temporary, auxiliary, filter.Radius, true, firstMaximum, token);
        Extrema(auxiliary, temporary, filter.Radius, false, !firstMaximum, token);
        Extrema(temporary, output, filter.Radius, true, !firstMaximum, token);
        if (filter.Kind is MorphologyKind.WhiteTopHat or MorphologyKind.BlackHat)
        {
            Rows(token, y =>
            {
                for (var i = y * _descriptor.Width; i < (y + 1) * _descriptor.Width; i++)
                {
                    output[i] = filter.Kind == MorphologyKind.BlackHat ? output[i] - input[i] : input[i] - output[i];
                }
            });
        }
    }

    private void Extrema(float[] input, float[] output, int radius, bool vertical, bool maximum, CancellationToken token)
    {
        var length = vertical ? _descriptor.Height : _descriptor.Width;
        var lines = vertical ? _descriptor.Width : _descriptor.Height;
        var lanes = Math.Min(lines, _queues.Length);
        Parallel.For(0, lanes, new ParallelOptions { MaxDegreeOfParallelism = lanes, CancellationToken = token }, lane =>
        {
            var queue = _queues[lane].Array;
            for (var line = lane; line < lines; line += lanes)
            {
                token.ThrowIfCancellationRequested();
                var start = vertical ? line : line * _descriptor.Width;
                var stride = vertical ? _descriptor.Width : 1;
                var head = 0;
                var tail = 0;
                var invalid = 0;
                for (var i = 0; i < length + (2 * radius); i++)
                {
                    var expired = i - (2 * radius) - 1;
                    if (expired >= 0)
                    {
                        var old = input[start + (Math.Clamp(expired - radius, 0, length - 1) * stride)];
                        if (!float.IsFinite(old))
                        {
                            invalid--;
                        }
                    }
                    while (head < tail && queue[head] <= expired)
                    {
                        head++;
                    }
                    var value = input[start + (Math.Clamp(i - radius, 0, length - 1) * stride)];
                    if (!float.IsFinite(value))
                    {
                        invalid++;
                    }
                    else
                    {
                        while (head < tail)
                        {
                            var previous = input[start + (Math.Clamp(queue[tail - 1] - radius, 0, length - 1) * stride)];
                            if (maximum ? previous > value : previous < value)
                            {
                                break;
                            }
                            tail--;
                        }
                        queue[tail++] = i;
                    }
                    if (i >= 2 * radius)
                    {
                        output[start + ((i - (2 * radius)) * stride)] = invalid > 0 ? float.NaN
                            : input[start + (Math.Clamp(queue[head] - radius, 0, length - 1) * stride)];
                    }
                }
            }
        });
    }

    private void Rows(CancellationToken token, Action<int> action)
    {
        if (_options.MaxDegreeOfParallelism == 1)
        {
            for (var y = 0; y < _descriptor.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                action(y);
            }
        }
        else
        {
            Parallel.For(0, _descriptor.Height,
                new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism, CancellationToken = token }, action);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var plane in _planes)
        {
            plane?.Dispose();
        }
        foreach (var queue in _queues)
        {
            queue?.Dispose();
        }
    }
}
