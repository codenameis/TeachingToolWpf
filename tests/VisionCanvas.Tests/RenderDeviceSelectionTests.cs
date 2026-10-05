using VisionCanvas.Rendering;
using Vortice.DXGI;

namespace VisionCanvas.Tests;

public sealed class RenderDeviceSelectionTests
{
    [Fact]
    public void AutomaticStartsWarpWhenNoHardwareAdaptersExist()
    {
        using var device = new Dx12Device(false, RenderDevicePreference.Automatic, _ => []);

        Assert.True(device.IsSoftware);
        Assert.NotEqual(nint.Zero, device.Device.NativePointer);
        Assert.False(string.IsNullOrWhiteSpace(device.AdapterName));
        device.Flush();
    }

    [Fact]
    public void ForcedSoftwareDoesNotEnumerateHardware()
    {
        using var device = new Dx12Device(false, RenderDevicePreference.Software,
            _ => throw new InvalidOperationException("Hardware must not be enumerated."));

        Assert.True(device.IsSoftware);
        Assert.Equal(0UL, device.DedicatedMemoryBytes);
        device.Flush();
    }

    [Fact]
    public void AutomaticDoesNotTreatASoftwareAdapterAsHardwareAndReleasesIt()
    {
        IDXGIAdapter1? enumerated = null;
        using var device = new Dx12Device(false, RenderDevicePreference.Automatic, factory =>
        {
            enumerated = factory.EnumWarpAdapter<IDXGIAdapter1>();
            return [enumerated];
        });

        Assert.True(device.IsSoftware);
        Assert.NotNull(enumerated);
        Assert.Equal(nint.Zero, enumerated.NativePointer);
        device.Flush();
    }

    [Fact]
    public void InvalidPreferenceFailsBeforeAdapterEnumeration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Dx12Device(false, (RenderDevicePreference)99,
            _ => throw new InvalidOperationException("Invalid preferences must be rejected first.")));
    }
}
