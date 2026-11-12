using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed class MemoryBudgetException(string message) : InvalidOperationException(message);
