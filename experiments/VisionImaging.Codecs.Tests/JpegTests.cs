namespace VisionImaging.Codecs.Tests;

public sealed class JpegTests
{
    // NetVips fixture 도구로 만든16×16 Gray117 progressive JPEG. 디코더 테스트는 코덱 패키지에 의존하지 않는다.
    private const string ConstantJpeg = "/9j/4QC8RXhpZgAASUkqAAgAAAAGABIBAwABAAAAAQAAABoBBQABAAAAVgAAABsBBQABAAAAXgAAACgBAwABAAAAAgAAABMCAwABAAAAAQAAAGmHBAABAAAAZgAAAAAAAAA4YwAA6AMAADhjAADoAwAABgAAkAcABAAAADAyMTABkQcABAAAAAECAwAAoAcABAAAADAxMDABoAMAAQAAAP//AAACoAQAAQAAABAAAAADoAQAAQAAABAAAAAAAAAA/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB/8IACwgAEAAQAQERAP/EABUAAQEAAAAAAAAAAAAAAAAAAAAG/9oACAEBAAAAAZMf/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQABBQIf/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQAGPwIf/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQABPyEf/9oACAEBAAAAEA//xAAUEAEAAAAAAAAAAAAAAAAAAAAg/9oACAEBAAE/EB//2Q==";

    [Fact]
    public void ProgressiveGrayscaleReadsOriginalValues()
    {
        using var reader = new NativeRasterDecoder().Open(Input(false), CancellationToken.None);
        var pixels = new byte[256];
        reader.ReadRows(0, 16, pixels, CancellationToken.None);
        Assert.Equal((16, 16), (reader.Width, reader.Height));
        Assert.All(pixels, value => Assert.Equal(117, value));
    }

    [Fact]
    public void TruncatedProgressiveJpegDoesNotSilentlyInsertEndMarker()
    {
        var path = Input(true);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new NativeRasterDecoder().Open(path, CancellationToken.None);
            reader.ReadRows(0, 16, new byte[256], CancellationToken.None);
        });
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void BudgetIsCheckedBeforeCoefficientDecode()
    {
        Assert.Throws<InvalidOperationException>(() => new NativeRasterDecoder(1024).Open(Input(false), CancellationToken.None));
    }

    private static string Input(bool truncate)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "원본.jpg");
        var bytes = Convert.FromBase64String(ConstantJpeg);
        File.WriteAllBytes(path, truncate ? bytes[..^2] : bytes);
        return path;
    }
}
