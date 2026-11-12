using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionOcr.PpOcr;

/// <summary>
/// DBNet probability map post-processor, <c>MinAreaRect</c>-based polygon unclipping,
/// reading-line clustering, and perspective quad rectification.
/// </summary>
internal static class PpOcrDbPostProcessor
{
    private const int ChannelCount = 3;
    private const int QuadCornerCount = 4;
    private const int LastQuadCornerIndex = 3;
    private const int MinWarpedCropDimensionPx = 2;
    private const byte BinaryForegroundMaskValue = byte.MaxValue;
    private const float SameLineRowMergeFactor = 0.55f;
    private const float MinLineRowHeightPx = 4.0f;
    private const float RectanglePerimeterHalfMultiplier = 2.0f;
    private const double ApproxPolyPerimeterFraction = 0.02;
    private const double ConvexQuadAreaFillFraction = 0.78;
    private const float DegenerateVectorEpsilon = 1e-3f;

    internal sealed class DetectedQuad
    {
        public Point2f[] Points { get; }

        public float Score { get; }

        public float MinX { get; }

        public float MaxX { get; }

        public float MinY { get; }

        public float MaxY { get; }

        public float CenterX => 0.5f * (MinX + MaxX);

        public float CenterY => 0.5f * (MinY + MaxY);

        public float Height => Math.Max(1.0f, MaxY - MinY);

        public DetectedQuad(Point2f[] points, float score)
        {
            Points = points;
            Score = score;
            MinX = Math.Min(Math.Min(points[0].X, points[1].X), Math.Min(points[2].X, points[LastQuadCornerIndex].X));
            MaxX = Math.Max(Math.Max(points[0].X, points[1].X), Math.Max(points[2].X, points[LastQuadCornerIndex].X));
            MinY = Math.Min(Math.Min(points[0].Y, points[1].Y), Math.Min(points[2].Y, points[LastQuadCornerIndex].Y));
            MaxY = Math.Max(Math.Max(points[0].Y, points[1].Y), Math.Max(points[2].Y, points[LastQuadCornerIndex].Y));
        }
    }

    public static List<DetectedQuad> DetectTextQuads(
        InferenceSession detSession,
        string detInputName,
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        DbDetOptions detOpts)
    {
        float[] rentedDetTensor = PpOcrPreprocessor.RentDetInputTensor(
            roiView,
            invertPolarity,
            detOpts,
            out int totalDetFloats,
            out int tensorW,
            out int tensorH,
            out float scaleX,
            out float scaleY,
            out int borderPadPx);

        try
        {
            var dense = new DenseTensor<float>(
                rentedDetTensor.AsMemory(0, totalDetFloats),
                [1, ChannelCount, tensorH, tensorW]);
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(detInputName, dense),
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = detSession.Run(inputs);
            Tensor<float> probTensor = outputs[0].AsTensor<float>();

            using var probMat = new Mat(tensorH, tensorW, MatType.CV_32FC1);
            using var binaryMat = new Mat(tensorH, tensorW, MatType.CV_8UC1);

            unsafe
            {
                float* pDst = (float*)probMat.DataPointer;
                byte* bDst = binaryMat.DataPointer;
                long probStep = probMat.Step() / sizeof(float);
                long binStep = binaryMat.Step();

                if (probTensor is DenseTensor<float> denseProb)
                {
                    ReadOnlySpan<float> srcSpan = denseProb.Buffer.Span;
                    for (int y = 0; y < tensorH; y++)
                    {
                        int srcOff = y * tensorW;
                        float* pRow = pDst + (y * probStep);
                        byte* bRow = bDst + (y * binStep);
                        for (int x = 0; x < tensorW; x++)
                        {
                            float p = srcSpan[srcOff + x];
                            pRow[x] = p;
                            bRow[x] = p > detOpts.BinThreshold ? BinaryForegroundMaskValue : (byte)0;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < tensorH; y++)
                    {
                        float* pRow = pDst + (y * probStep);
                        byte* bRow = bDst + (y * binStep);
                        for (int x = 0; x < tensorW; x++)
                        {
                            float p = probTensor[0, 0, y, x];
                            pRow[x] = p;
                            bRow[x] = p > detOpts.BinThreshold ? BinaryForegroundMaskValue : (byte)0;
                        }
                    }
                }
            }

            Cv2.FindContours(binaryMat, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0)
            {
                return [];
            }

            int limit = Math.Min(contours.Length, Math.Max(1, detOpts.MaxCandidates));
            var quads = new List<DetectedQuad>(limit);

            for (int i = 0; i < limit; i++)
            {
                Point[] contour = contours[i];
                if (contour.Length < QuadCornerCount)
                {
                    continue;
                }

                RotatedRect minRect = Cv2.MinAreaRect(contour);
                float minSide = MathF.Min(minRect.Size.Width, minRect.Size.Height);
                if (minSide < detOpts.MinBoxSidePx)
                {
                    continue;
                }

                float boxScore = ComputeContourMeanScore(probMat, contour);
                if (boxScore < detOpts.BoxThreshold)
                {
                    continue;
                }

                Point2f[] unclippedTensorPts = UnclipContourQuad(contour, minRect, detOpts.UnclipRatio);
                Point2f[] orderedOrigPts = MapAndOrderQuadPoints(
                    unclippedTensorPts,
                    scaleX,
                    scaleY,
                    borderPadPx,
                    roiView.Width,
                    roiView.Height);

                float widthTop = Distance(orderedOrigPts[0], orderedOrigPts[1]);
                float heightLeft = Distance(orderedOrigPts[0], orderedOrigPts[LastQuadCornerIndex]);
                if (MathF.Min(widthTop, heightLeft) < detOpts.MinBoxSidePx ||
                    (widthTop * heightLeft) < detOpts.MinBoxAreaPx)
                {
                    continue;
                }

                quads.Add(new DetectedQuad(orderedOrigPts, boxScore));
            }

            return quads;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedDetTensor);
        }
    }

    public static float ComputeMinRectUnclipDistance(Size2f minRectSize, float unclipRatio)
    {
        // [Math Why]: Official PaddleOCR DBPostProcess unclips the fitted MinAreaRect box (`box = get_mini_boxes(contour)`),
        // where `area = W * H` and `length = 2 * (W + H)`. Using noisy raw pixel contour perimeter inflates `length`
        // on jagged/dot strokes and shrinks `distance`, clipping character ascenders/edges.
        float boxWidth = MathF.Max(1.0f, minRectSize.Width);
        float boxHeight = MathF.Max(1.0f, minRectSize.Height);
        float boxArea = boxWidth * boxHeight;
        float boxPerimeter = MathF.Max(1.0f, RectanglePerimeterHalfMultiplier * (boxWidth + boxHeight));
        return (boxArea * unclipRatio) / boxPerimeter;
    }

    public static Mat WarpPerspectiveQuadCrop(Mat srcMat, Point2f[] orderedPts)
    {
        int cropW = Math.Max(MinWarpedCropDimensionPx, (int)MathF.Round(MathF.Max(
            Distance(orderedPts[0], orderedPts[1]),
            Distance(orderedPts[LastQuadCornerIndex], orderedPts[2]))));
        int cropH = Math.Max(MinWarpedCropDimensionPx, (int)MathF.Round(MathF.Max(
            Distance(orderedPts[0], orderedPts[LastQuadCornerIndex]),
            Distance(orderedPts[1], orderedPts[2]))));

        Point2f[] dstPts =
        [
            new Point2f(0f, 0f),
            new Point2f(cropW - 1, 0f),
            new Point2f(cropW - 1, cropH - 1),
            new Point2f(0f, cropH - 1),
        ];

        using Mat homography = Cv2.GetPerspectiveTransform(orderedPts, dstPts);
        var warped = new Mat();
        Cv2.WarpPerspective(
            srcMat,
            warped,
            homography,
            new Size(cropW, cropH),
            InterpolationFlags.Cubic,
            BorderTypes.Replicate);
        return warped;
    }

    public static List<List<DetectedQuad>> GroupQuadsIntoReadingLines(List<DetectedQuad> quads)
    {
        quads.Sort(static (a, b) => a.CenterY != b.CenterY ? a.CenterY.CompareTo(b.CenterY) : a.CenterX.CompareTo(b.CenterX));
        var rows = new List<List<DetectedQuad>>();

        foreach (DetectedQuad q in quads)
        {
            if (rows.Count == 0)
            {
                rows.Add([q]);
                continue;
            }

            List<DetectedQuad> lastRow = rows[^1];
            float rowCenterY = 0f;
            float rowHeight = 0f;
            for (int i = 0; i < lastRow.Count; i++)
            {
                rowCenterY += lastRow[i].CenterY;
                rowHeight += lastRow[i].Height;
            }

            rowCenterY /= lastRow.Count;
            rowHeight = Math.Max(MinLineRowHeightPx, rowHeight / lastRow.Count);

            if (MathF.Abs(q.CenterY - rowCenterY) <= rowHeight * SameLineRowMergeFactor)
            {
                lastRow.Add(q);
            }
            else
            {
                rows.Add([q]);
            }
        }

        for (int r = 0; r < rows.Count; r++)
        {
            rows[r].Sort(static (a, b) => a.CenterX.CompareTo(b.CenterX));
        }

        return rows;
    }

    private static float ComputeContourMeanScore(Mat probMat, Point[] contour)
    {
        int matWidth = probMat.Width;
        int matHeight = probMat.Height;
        Rect bbox = Cv2.BoundingRect(contour);
        int x0 = Math.Clamp(bbox.X, 0, matWidth - 1);
        int y0 = Math.Clamp(bbox.Y, 0, matHeight - 1);
        int x1 = Math.Clamp(bbox.Right, x0 + 1, matWidth);
        int y1 = Math.Clamp(bbox.Bottom, y0 + 1, matHeight);
        int rw = x1 - x0;
        int rh = y1 - y0;

        using var mask = new Mat(rh, rw, MatType.CV_8UC1, Scalar.All(0));
        var shifted = new Point[contour.Length];
        for (int i = 0; i < contour.Length; i++)
        {
            shifted[i] = new Point(contour[i].X - x0, contour[i].Y - y0);
        }

        Cv2.FillPoly(mask, [shifted], Scalar.All(BinaryForegroundMaskValue));
        using Mat subProb = new(probMat, new Rect(x0, y0, rw, rh));
        Scalar mean = Cv2.Mean(subProb, mask);
        return (float)mean.Val0;
    }

    private static Point2f[] UnclipContourQuad(Point[] contour, RotatedRect minRect, float unclipRatio)
    {
        float distance = ComputeMinRectUnclipDistance(minRect.Size, unclipRatio);
        double contourArea = Math.Max(1.0, Cv2.ContourArea(contour));
        double contourPerimeter = Math.Max(1.0, Cv2.ArcLength(contour, closed: true));
        Point[] approx = Cv2.ApproxPolyDP(contour, ApproxPolyPerimeterFraction * contourPerimeter, closed: true);
        double rectArea = Math.Max(1.0, minRect.Size.Width * minRect.Size.Height);

        if (approx.Length == QuadCornerCount && Cv2.IsContourConvex(approx) && contourArea >= ConvexQuadAreaFillFraction * rectArea)
        {
            if (TryOffsetConvexQuad(approx, distance, out Point2f[]? offsetQuad) && offsetQuad is not null)
            {
                return offsetQuad;
            }
        }

        var expanded = new RotatedRect(
            minRect.Center,
            new Size2f(
                minRect.Size.Width + (RectanglePerimeterHalfMultiplier * distance),
                minRect.Size.Height + (RectanglePerimeterHalfMultiplier * distance)),
            minRect.Angle);
        return expanded.Points();
    }

    private static bool TryOffsetConvexQuad(Point[] quad, float distance, out Point2f[]? result)
    {
        result = null;
        float signedArea = 0f;
        for (int i = 0; i < QuadCornerCount; i++)
        {
            Point a = quad[i];
            Point b = quad[(i + 1) % QuadCornerCount];
            signedArea += (a.X * b.Y) - (b.X * a.Y);
        }

        float normalSign = signedArea >= 0f ? 1.0f : -1.0f;
        Span<Point2f> p0 = stackalloc Point2f[QuadCornerCount];
        Span<Point2f> dir = stackalloc Point2f[QuadCornerCount];

        for (int i = 0; i < QuadCornerCount; i++)
        {
            Point a = quad[i];
            Point b = quad[(i + 1) % QuadCornerCount];
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float len = MathF.Sqrt((dx * dx) + (dy * dy));
            if (len < DegenerateVectorEpsilon)
            {
                return false;
            }

            float ux = dx / len;
            float uy = dy / len;
            float nx = normalSign * uy;
            float ny = -normalSign * ux;
            p0[i] = new Point2f(a.X + (nx * distance), a.Y + (ny * distance));
            dir[i] = new Point2f(ux, uy);
        }

        var corners = new Point2f[QuadCornerCount];
        for (int i = 0; i < QuadCornerCount; i++)
        {
            int prev = (i + LastQuadCornerIndex) % QuadCornerCount;
            float cross = (dir[prev].X * dir[i].Y) - (dir[prev].Y * dir[i].X);
            if (MathF.Abs(cross) < DegenerateVectorEpsilon)
            {
                return false;
            }

            float qpx = p0[i].X - p0[prev].X;
            float qpy = p0[i].Y - p0[prev].Y;
            float t = ((qpx * dir[i].Y) - (qpy * dir[i].X)) / cross;
            corners[i] = new Point2f(p0[prev].X + (t * dir[prev].X), p0[prev].Y + (t * dir[prev].Y));
        }

        result = corners;
        return true;
    }

    private static Point2f[] MapAndOrderQuadPoints(
        Point2f[] tensorPts,
        float scaleX,
        float scaleY,
        int borderPadPx,
        int maxWidth,
        int maxHeight)
    {
        var mapped = new Point2f[QuadCornerCount];
        for (int i = 0; i < QuadCornerCount; i++)
        {
            float ox = (tensorPts[i].X / scaleX) - borderPadPx;
            float oy = (tensorPts[i].Y / scaleY) - borderPadPx;
            mapped[i] = new Point2f(
                Math.Clamp(ox, 0f, maxWidth - 1),
                Math.Clamp(oy, 0f, maxHeight - 1));
        }

        Array.Sort(mapped, static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        Point2f tl = mapped[0].Y <= mapped[1].Y ? mapped[0] : mapped[1];
        Point2f bl = mapped[0].Y <= mapped[1].Y ? mapped[1] : mapped[0];
        Point2f tr = mapped[2].Y <= mapped[LastQuadCornerIndex].Y ? mapped[2] : mapped[LastQuadCornerIndex];
        Point2f br = mapped[2].Y <= mapped[LastQuadCornerIndex].Y ? mapped[LastQuadCornerIndex] : mapped[2];

        return [tl, tr, br, bl];
    }

    private static float Distance(Point2f a, Point2f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
