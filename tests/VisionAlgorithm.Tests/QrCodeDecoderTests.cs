using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.DataCode2D;

namespace VisionAlgorithm.Tests;

public sealed class QrCodeDecoderTests
{
    private const byte DefaultDarkLevel = 24;
    private const byte DefaultLightLevel = 232;

    [Fact]
    public void Decode_UninitializedOrTinyOrFlatBuffer_HandlesGracefully()
    {
        Assert.Throws<InvalidOperationException>(() => QrCodeDecoder.Decode(default));

        byte[] tiny = new byte[10 * 10];
        QrCodeDecodeResult tinyResult = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(tiny, 10, 10, 10));
        Assert.False(tinyResult.IsDecoded);

        const int width = 96;
        const int height = 96;
        const int stride = width + 7;
        byte[] flat = new byte[height * stride];
        Array.Fill(flat, (byte)160);

        QrCodeDecodeResult flatResult = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(flat, width, height, stride));
        Assert.False(flatResult.IsDecoded);
    }

    [Fact]
    public void Decode_PaddedStrideBuffer_AccuratelyRecoversPayloadCenterAndZeroAngle()
    {
        const int width = 148;
        const int height = 140;
        const int stride = width + 11; // Intentional non-contiguous padded stride
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)205);

        // Fill stride padding columns with high-frequency alternating values to catch any stride == width bugs
        for (int y = 0; y < height; y++)
        {
            for (int x = width; x < stride; x++)
            {
                buffer[(y * stride) + x] = (byte)(((x + y) & 1) == 0 ? 0 : 255);
            }
        }

        const int originX = 32;
        const int originY = 26;
        const int modulePx = 4;
        QrCodeSymbolRenderer.RenderQrSymbol(
            "SN-2026-Q1",
            buffer,
            width,
            height,
            stride,
            originX: originX,
            originY: originY,
            moduleSizePixels: modulePx,
            errorCorrectionLevel: QrErrorCorrectionLevel.M,
            maskPattern: 2);

        QrCodeDecodeResult decoded = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(buffer, width, height, stride));
        Assert.True(decoded.IsDecoded);
        Assert.Equal("SN-2026-Q1", decoded.DecodedText);
        Assert.Equal(1, decoded.Version);
        Assert.Equal(21, decoded.Dimension);
        Assert.Equal(QrErrorCorrectionLevel.M, decoded.ErrorCorrectionLevel);
        Assert.Equal(0, decoded.CorrectedByteErrors);

        float expectedCenterX = originX + (21 * modulePx * 0.5f); // 32 + 42 = 74.0
        float expectedCenterY = originY + (21 * modulePx * 0.5f); // 26 + 42 = 68.0
        Assert.InRange(MathF.Abs(decoded.CenterWorld.X - expectedCenterX), 0.0f, 0.35f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.Y - expectedCenterY), 0.0f, 0.35f);
        Assert.InRange(MathF.Abs(decoded.AngleDegrees), 0.0f, 0.5f);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(27.5f)]
    [InlineData(-32.5f)]
    [InlineData(45.0f)]
    [InlineData(90.0f)]
    [InlineData(135.0f)]
    [InlineData(-155.0f)]
    [InlineData(180.0f)]
    public void Decode_Full360DegreeRotations_RecoversExactWorldPositionAndSignedAngle(float expectedDeg)
    {
        const int width = 200;
        const int height = 196;
        const int stride = width + 5;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)198);

        const float expectedCx = 101.25f;
        const float expectedCy = 97.75f;
        const float modulePx = 4.2f;
        float expectedRad = expectedDeg * (MathF.PI / 180.0f);

        QrCodeSymbolRenderer.RenderRotatedQrSymbol(
            "POSE-360",
            buffer,
            width,
            height,
            stride,
            centerX: expectedCx,
            centerY: expectedCy,
            moduleSizePixels: modulePx,
            angleRad: expectedRad,
            errorCorrectionLevel: QrErrorCorrectionLevel.Q,
            maskPattern: 3);

        QrCodeDecodeResult decoded = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(buffer, width, height, stride));
        Assert.True(decoded.IsDecoded, $"Failed to decode rotated QR at {expectedDeg:F1} deg.");
        Assert.Equal("POSE-360", decoded.DecodedText);

        Assert.InRange(MathF.Abs(decoded.CenterWorld.X - expectedCx), 0.0f, 0.65f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.Y - expectedCy), 0.0f, 0.65f);

        float angularErrorDeg = ShortestAngularDistanceDegrees(decoded.AngleDegrees, expectedDeg);
        Assert.InRange(angularErrorDeg, 0.0f, 1.0f);
    }

    [Fact]
    public void Decode_Version2WithAlignmentAndCorruptedModules_RecoversViaReedSolomonEcc()
    {
        const int width = 180;
        const int height = 180;
        const int stride = width + 3;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)215);

        // 13-byte UTF-8 payload forces Version 2 (25x25 with 5x5 Alignment Pattern at (18, 18)) at ECC Level H (14 data + 28 ECC bytes -> up to 14 byte errors)
        const string payload = "V2-ALIGN-2026";
        const int originX = 28;
        const int originY = 30;
        const int modulePx = 4;

        QrCodeSymbolRenderer.RenderQrSymbol(
            payload,
            buffer,
            width,
            height,
            stride,
            originX: originX,
            originY: originY,
            moduleSizePixels: modulePx,
            errorCorrectionLevel: QrErrorCorrectionLevel.H,
            maskPattern: 4,
            darkLevel: DefaultDarkLevel,
            lightLevel: DefaultLightLevel);

        // Intentionally paint a solid white scratch across data modules (row 12..13, col 10..15) while sparing finder/timing/format zones
        for (int py = originY + (12 * modulePx); py < originY + (14 * modulePx); py++)
        {
            for (int px = originX + (10 * modulePx); px < originX + (16 * modulePx); px++)
            {
                buffer[(py * stride) + px] = DefaultLightLevel;
            }
        }

        QrCodeDecodeResult decoded = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(buffer, width, height, stride));
        Assert.True(decoded.IsDecoded);
        Assert.Equal(2, decoded.Version);
        Assert.Equal(25, decoded.Dimension);
        Assert.Equal(QrErrorCorrectionLevel.H, decoded.ErrorCorrectionLevel);
        Assert.Equal(payload, decoded.DecodedText);
        Assert.True(decoded.CorrectedByteErrors >= 1, $"Expected RS byte error correction >= 1, got {decoded.CorrectedByteErrors}.");

        float expectedCx = originX + (25 * modulePx * 0.5f);
        float expectedCy = originY + (25 * modulePx * 0.5f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.X - expectedCx), 0.0f, 0.5f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.Y - expectedCy), 0.0f, 0.5f);
    }

    [Fact]
    public void Decode_InvertedLightOnDarkDpmSymbol_RecoversPayloadPositionAndAngle()
    {
        const int width = 188;
        const int height = 188;
        const int stride = width + 4;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)28);

        const float expectedCx = 94.0f;
        const float expectedCy = 94.0f;
        const float expectedDeg = -64.0f;

        QrCodeSymbolRenderer.RenderRotatedQrSymbol(
            "DPM-INV-64",
            buffer,
            width,
            height,
            stride,
            centerX: expectedCx,
            centerY: expectedCy,
            moduleSizePixels: 4.0f,
            angleRad: expectedDeg * (MathF.PI / 180.0f),
            errorCorrectionLevel: QrErrorCorrectionLevel.M,
            maskPattern: 1,
            darkLevel: 232,
            lightLevel: 28);

        QrCodeDecodeResult decoded = QrCodeDecoder.Decode(new ReadOnlyImageView2D<byte>(buffer, width, height, stride));
        Assert.True(decoded.IsDecoded);
        Assert.Equal("DPM-INV-64", decoded.DecodedText);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.X - expectedCx), 0.0f, 0.65f);
        Assert.InRange(MathF.Abs(decoded.CenterWorld.Y - expectedCy), 0.0f, 0.65f);
        Assert.InRange(ShortestAngularDistanceDegrees(decoded.AngleDegrees, expectedDeg), 0.0f, 1.0f);
    }

    [Fact]
    public void DecodeAll_QrCode_MultiSymbolMixedPolaritiesAndRotations_ReturnsAllInReadingOrderAndEnvelopes()
    {
        const int width = 960;
        const int height = 640;
        const int stride = width + 7;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)228);

        // #1 Top-Left: 0 deg Dark-on-Light
        QrCodeSymbolRenderer.RenderRotatedQrSymbol(
            "QR-TOP-L",
            buffer,
            width,
            height,
            stride,
            centerX: 230.0f,
            centerY: 200.0f,
            moduleSizePixels: 5.5f,
            angleRad: 0.0f,
            errorCorrectionLevel: QrErrorCorrectionLevel.M,
            maskPattern: 2);

        // #2 Top-Right: -32.5 deg Dark-on-Light
        QrCodeSymbolRenderer.RenderRotatedQrSymbol(
            "QR-TOP-R",
            buffer,
            width,
            height,
            stride,
            centerX: 720.0f,
            centerY: 205.0f,
            moduleSizePixels: 5.0f,
            angleRad: -32.5f * (MathF.PI / 180.0f),
            errorCorrectionLevel: QrErrorCorrectionLevel.Q,
            maskPattern: 4);

        // #3 Bottom-Center: +135.0 deg Light-on-Dark DPM plaque
        for (int py = 360; py < 610; py++)
        {
            Span<byte> row = buffer.AsSpan(py * stride, width);
            for (int px = 350; px < 610; px++)
            {
                row[px] = 28;
            }
        }

        QrCodeSymbolRenderer.RenderRotatedQrSymbol(
            "QR-BOT-INV",
            buffer,
            width,
            height,
            stride,
            centerX: 480.0f,
            centerY: 485.0f,
            moduleSizePixels: 5.0f,
            angleRad: 135.0f * (MathF.PI / 180.0f),
            errorCorrectionLevel: QrErrorCorrectionLevel.M,
            maskPattern: 1,
            darkLevel: 236,
            lightLevel: 28);

        var view = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);
        VisionMultiResult<QrCodeDecodeResult> multi = QrCodeDecoder.DecodeMulti(view);

        Assert.True(multi.HasAnyDecoded);
        Assert.Equal(3, multi.Count);
        Assert.Equal("QR-TOP-L | QR-TOP-R | QR-BOT-INV", multi.DecodedText);

        ReadOnlySpan<QrCodeDecodeResult> items = multi.Items.Span;
        Assert.Equal("QR-TOP-L", items[0].DecodedText);
        Assert.InRange(MathF.Abs(items[0].CenterWorld.X - 230.0f), 0.0f, 0.8f);
        Assert.InRange(MathF.Abs(items[0].CenterWorld.Y - 200.0f), 0.0f, 0.8f);
        Assert.InRange(ShortestAngularDistanceDegrees(items[0].AngleDegrees, 0.0f), 0.0f, 1.0f);

        Assert.Equal("QR-TOP-R", items[1].DecodedText);
        Assert.InRange(MathF.Abs(items[1].CenterWorld.X - 720.0f), 0.0f, 0.8f);
        Assert.InRange(MathF.Abs(items[1].CenterWorld.Y - 205.0f), 0.0f, 0.8f);
        Assert.InRange(ShortestAngularDistanceDegrees(items[1].AngleDegrees, -32.5f), 0.0f, 1.2f);

        Assert.Equal("QR-BOT-INV", items[2].DecodedText);
        Assert.InRange(MathF.Abs(items[2].CenterWorld.X - 480.0f), 0.0f, 0.8f);
        Assert.InRange(MathF.Abs(items[2].CenterWorld.Y - 485.0f), 0.0f, 0.8f);
        Assert.InRange(ShortestAngularDistanceDegrees(items[2].AngleDegrees, 135.0f), 0.0f, 1.2f);

        VisionInspectionEnvelope[] envelopes = multi.ToInspectionEnvelopes(nodeId: 7, roiId: Guid.NewGuid());
        Assert.Equal(3, envelopes.Length);
        Assert.Equal(3, multi.ToReplayPayload().SubRegions.Length);
    }

    [Fact]
    public void DecodeAll_DataMatrix_MultiSymbolSideBySideAndRotated_ReturnsAllInReadingOrder()
    {
        const int width = 960;
        const int height = 640;
        const int stride = width + 5;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)230);

        // #1 Top-Left: origin (160, 140), 12x12 * 10px = 120x120 -> center (220, 200)
        Ecc200SymbolRenderer.RenderEcc200Symbol(
            "DM-A1",
            buffer,
            width,
            height,
            stride,
            originX: 160,
            originY: 140,
            moduleSizePixels: 10);

        // #2 Top-Right: origin (640, 140), 12x12 * 10px = 120x120 -> center (700, 200)
        Ecc200SymbolRenderer.RenderEcc200Symbol(
            "DM-B2",
            buffer,
            width,
            height,
            stride,
            originX: 640,
            originY: 140,
            moduleSizePixels: 10);

        // #3 Bottom-Center: rotated +22.5 deg at (480, 470)
        Ecc200SymbolRenderer.RenderRotatedEcc200Symbol(
            "DM-ROT",
            buffer,
            width,
            height,
            stride,
            centerX: 480.0f,
            centerY: 470.0f,
            moduleSizePixels: 10.0f,
            angleRad: 22.5f * (MathF.PI / 180.0f));

        var view = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);
        VisionMultiResult<DataMatrixDecodeResult> multi = DataMatrixEcc200Decoder.DecodeMulti(view);

        Assert.True(multi.HasAnyDecoded);
        Assert.Equal(3, multi.Count);

        ReadOnlySpan<DataMatrixDecodeResult> items = multi.Items.Span;
        Assert.Equal("DM-A1", items[0].DecodedText);
        Assert.InRange(MathF.Abs(items[0].CenterWorld.X - 220.0f), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(items[0].CenterWorld.Y - 200.0f), 0.0f, 2.5f);

        Assert.Equal("DM-B2", items[1].DecodedText);
        Assert.InRange(MathF.Abs(items[1].CenterWorld.X - 700.0f), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(items[1].CenterWorld.Y - 200.0f), 0.0f, 2.5f);

        Assert.Equal("DM-ROT", items[2].DecodedText);
        Assert.InRange(MathF.Abs(items[2].CenterWorld.X - 480.0f), 0.0f, 2.5f);
        Assert.InRange(MathF.Abs(items[2].CenterWorld.Y - 470.0f), 0.0f, 2.5f);
        float rotDeg = items[2].AngleRad * (180.0f / MathF.PI);
        Assert.InRange(ShortestAngularDistanceDegrees(rotDeg, 22.5f), 0.0f, 1.8f);
    }

    [Fact]
    public void DecodeAllMultiScanline_Barcode1D_ColinearSideBySideAndRotated_ReturnsAllInReadingOrder()
    {
        const int width = 1080;
        const int height = 640;
        const int stride = width + 9;
        byte[] buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)236);

        // #1 & #2 Colinear side-by-side on the exact same horizontal band (y = 140..300)
        Code128SymbolRenderer.RenderCode128Symbol(
            buffer,
            width,
            height,
            stride,
            payload: "B1-LEFT",
            barLeftX: 80,
            barTopY: 140,
            barHeight: 160,
            moduleWidthPx: 3);

        Code128SymbolRenderer.RenderCode128Symbol(
            buffer,
            width,
            height,
            stride,
            payload: "B2-RGHT",
            barLeftX: 620,
            barTopY: 140,
            barHeight: 160,
            moduleWidthPx: 3);

        // #3 Bottom rotated -18.0 deg
        Code128SymbolRenderer.RenderRotatedCode128Symbol(
            buffer,
            width,
            height,
            stride,
            payload: "B3-ROT",
            centerX: 540.0f,
            centerY: 490.0f,
            barHeight: 110,
            angleRad: -18.0f * (MathF.PI / 180.0f),
            moduleWidthPx: 3.0f);

        var view = new ReadOnlyImageView2D<byte>(buffer, width, height, stride);
        VisionMultiResult<Barcode1DDecodeResult> multi = Barcode1DDecoder.DecodeMulti(view, scanlineCount: 11);

        Assert.True(multi.HasAnyDecoded);
        Assert.Equal(3, multi.Count);
        Assert.Equal("B1-LEFT | B2-RGHT | B3-ROT", multi.DecodedText);

        ReadOnlySpan<Barcode1DDecodeResult> items = multi.Items.Span;
        Assert.Equal("B1-LEFT", items[0].Payload);
        Assert.InRange(Math.Abs(items[0].AngleDeg), 0.0, 1.0);

        Assert.Equal("B2-RGHT", items[1].Payload);
        Assert.True(items[1].CenterX > items[0].CenterX + 300.0);
        Assert.InRange(Math.Abs(items[1].AngleDeg), 0.0, 1.0);

        Assert.Equal("B3-ROT", items[2].Payload);
        Assert.InRange(Math.Abs(items[2].CenterX - 540.0), 0.0, 4.5);
        Assert.InRange(Math.Abs(items[2].CenterY - 490.0), 0.0, 4.5);
        Assert.InRange(items[2].AngleDeg, -19.5, -16.5);

        Assert.Equal(3, multi.ToInspectionEnvelopes(nodeId: 9, roiId: Guid.NewGuid()).Length);
    }

    private static float ShortestAngularDistanceDegrees(float aDeg, float bDeg)
    {
        float diff = (aDeg - bDeg) % 360.0f;
        if (diff > 180.0f)
        {
            diff -= 360.0f;
        }
        else if (diff < -180.0f)
        {
            diff += 360.0f;
        }

        return MathF.Abs(diff);
    }
}
