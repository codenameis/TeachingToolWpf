using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CpuMatching.Core;

internal sealed class CpuSearchProfile : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDisposable _subscription;
    private IDisposable? _events;
    private readonly Dictionary<string, IncrementalHash> _hashes = new();
    private readonly Dictionary<string, long> _counts = new();
    private readonly Dictionary<string, JsonElement> _maps = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private int _batches;
    private readonly int _batchLimit;
    private readonly Action? _cancel;

    public CpuSearchProfile(int batchLimit = 0, Action? cancel = null)
    {
        _batchLimit = batchLimit;
        _cancel = cancel;
        _subscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public bool LimitReached => _batchLimit > 0 && _batches >= _batchLimit;

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "CpuMatching.Search")
        {
            _events = listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> item)
    {
        if (item.Value is IReadOnlyList<Match> matches)
        {
            if (!_hashes.TryGetValue(item.Key, out var hash))
            {
                hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _hashes.Add(item.Key, hash);
                _counts.Add(item.Key, 0);
            }
            Span<byte> bytes = stackalloc byte[5 * sizeof(double)];
            foreach (var match in matches)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, match.X);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], match.Y);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes[16..], match.AngleDegrees);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes[24..], match.Scale);
                BinaryPrimitives.WriteDoubleLittleEndian(bytes[32..], match.Score);
                hash.AppendData(bytes);
            }
            _counts[item.Key] += matches.Count;
            if (item.Key == "Verified")
            {
                _batches++;
                Console.WriteLine($"Batch {_batches}: {_timer.Elapsed.TotalSeconds:F1}s, {_counts["Candidates"]} candidates, {_counts["Verified"]} verified");
            }
        }
        else if (item.Key == "Maps")
        {
            var value = JsonSerializer.SerializeToElement(item.Value);
            _maps[$"{value.GetProperty("Width")}x{value.GetProperty("Height")}"] = value;
            if (LimitReached)
            {
                _cancel?.Invoke();
            }
        }
    }

    public object Report() => new
    {
        Stages = _hashes.ToDictionary(pair => pair.Key, pair => new
        {
            Count = _counts[pair.Key],
            Sha256 = Convert.ToHexString(pair.Value.GetCurrentHash())
        }),
        Maps = _maps.Values.ToArray()
    };

    public void OnError(Exception error) => throw error;
    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        _events?.Dispose();
        _subscription.Dispose();
        foreach (var hash in _hashes.Values)
        {
            hash.Dispose();
        }
    }
}
