using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed record StagePlan(string Filter, string Device, string Reason);
