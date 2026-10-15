using PoseMatch.Tdcm;

namespace VisionAlgorithm.PoseMatch;

public static class TdcmImageAdapter
{
    public static PixelImage FromGray(GrayImage image)
    {
        if (!image.ExcludedPixels.IsEmpty)
        {
            throw new NotSupportedException("PoseMatch-TDCM은 제외 마스크를 지원하지 않습니다.");
        }

        return new(image.Pixels, image.Width, image.Height, image.Stride);
    }
}
