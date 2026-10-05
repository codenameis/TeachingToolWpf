namespace VisionImaging;

internal static class VipsRuntime
{
    private static readonly Lazy<bool> s_configured = new(() =>
    {
        // The application caches image levels explicitly. An additional native operation cache
        // retains .v file mappings beyond Dispose and prevents directory rename on Windows.
        NetVips.Cache.Max = 0;
        NetVips.Cache.MaxMem = 128UL * 1024 * 1024;
        return true;
    });

    public static void Configure() => _ = s_configured.Value;
}
