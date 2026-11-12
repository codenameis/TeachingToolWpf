namespace CpuMatching.Core;

public readonly record struct CpuSearchRegion(int X, int Y, int Width, int Height)
{
    public void Validate(CpuGrayImage image)
    {
        if (Width < 1 || Height < 1 || X < 0 || Y < 0 || Width < 4 || Height < 4 || (long)X + Width > image.Width || (long)Y + Height > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "Search region must be inside the image.");
        }
    }
}
