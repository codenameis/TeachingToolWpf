using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace IndustrialVision.Filters.Gpu;

public sealed record SmokeResult(string Status, string Device, string Backend, long MemoryBytes, int ComparedValues);
