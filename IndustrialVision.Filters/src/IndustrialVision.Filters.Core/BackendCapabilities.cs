using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed record BackendCapabilities(string Backend, string Device, long MemoryBytes, IReadOnlyList<string> Filters);
