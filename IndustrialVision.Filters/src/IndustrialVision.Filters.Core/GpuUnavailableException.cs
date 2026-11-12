using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed class GpuUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
