namespace IndustrialVision.Filters.Gpu;

// ILGPU의 동적 launcher assembly가 값 형식에 접근해야 한다.
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public struct KernelSettings
{
    public int Width;
    public int Height;
    public int Radius;
    public int Vertical;
    public int Kind;
    public int HasDark;
    public float Scale;
    public float Minimum;
    public float Level;
}
