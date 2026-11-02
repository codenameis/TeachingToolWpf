using global::ILGPU;

namespace VisionCompute.ILGPU;

internal static class CudaPrimitiveKernels
{
    public static void Median(Index1D index, ArrayView<byte> input, ArrayView<byte> output, int width, int height)
    {
        var x = index % width;
        var y = index / width;
        // OpenCV medianBlur와 같은 복제 경계. 정수 비교만 사용해 반올림 차이를 만들지 않는다.
        var left = Math.Max(0, x - 1);
        var right = Math.Min(width - 1, x + 1);
        var top = Math.Max(0, y - 1) * width;
        var middle = y * width;
        var bottom = Math.Min(height - 1, y + 1) * width;
        var a = input[top + left];
        var b = input[top + x];
        var c = input[top + right];
        var d = input[middle + left];
        var e = input[middle + x];
        var f = input[middle + right];
        var g = input[bottom + left];
        var h = input[bottom + x];
        var i = input[bottom + right];
        // Nine-value odd/even sorting network; the middle value is the exact median.
        for (var pass = 0; pass < 5; pass++)
        {
            Sort(ref a, ref b);
            Sort(ref c, ref d);
            Sort(ref e, ref f);
            Sort(ref g, ref h);
            Sort(ref b, ref c);
            Sort(ref d, ref e);
            Sort(ref f, ref g);
            Sort(ref h, ref i);
        }
        output[index] = e;
    }

    private static void Sort(ref byte first, ref byte second)
    {
        var low = Math.Min(first, second);
        second = Math.Max(first, second);
        first = low;
    }
}
