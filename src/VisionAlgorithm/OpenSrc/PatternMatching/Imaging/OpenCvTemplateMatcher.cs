using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace VisionAlgorithm.OpenSrc.PatternMatching.Imaging;

/// <summary>Mat과 네이티브 호출 수명은 이 경계에만 둔다. 호출 중 취소되어도 반환 후 자원을 해제한다.</summary>
internal static class OpenCvTemplateMatcher
{
    public static CorrelationResult Search(GrayImage image, CorrelationTemplate template, SearchOptions options, CancellationToken token)
    {
        options.Validate();
        if ((long)image.Width * image.Height > CorrelationTemplate.MaximumSearchPixels)
        {
            throw new ArgumentException("OpenCV 검색 창은 16 Mpx 이하여야 합니다.");
        }
        token.ThrowIfCancellationRequested();
        if (image.Width < template.Width || image.Height < template.Height)
        {
            return new([], false, 0);
        }
        var timer = Stopwatch.StartNew();
        var pixels = new byte[checked(image.Width * image.Height)];
        for (var row = 0; row < image.Height; row++)
        {
            token.ThrowIfCancellationRequested();
            image.Pixels.Span.Slice(row * image.Stride, image.Width).CopyTo(pixels.AsSpan(row * image.Width));
        }
        using var source = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, pixels, image.Width);
        using var pattern = Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC1, template.Pixels);
        using var mask = template.Mask.Length == 0 ? null : Mat.FromPixelData(template.Height, template.Width, MatType.CV_8UC1, template.Mask);
        using var response = new Mat();
        // OpenCV 4.13: CCOEFF_NORMED의 마스크 수식과 원본 크기 결과 맵.
        // https://docs.opencv.org/4.13.0/df/dfb/group__imgproc__object.html
        if (mask is null)
        {
            Cv2.MatchTemplate(source, pattern, response, TemplateMatchModes.CCoeffNormed);
        }
        else
        {
            Cv2.MatchTemplate(source, pattern, response, TemplateMatchModes.CCoeffNormed, mask);
        }
        token.ThrowIfCancellationRequested();
        var width = response.Width;
        var height = response.Height;
        var scores = new float[checked(width * height)];
        Marshal.Copy(response.Data, scores, 0, scores.Length);
        // 마스크 정규화의 0 분모는 NaN/Inf일 수 있다. 유효 후보로 승격하지 않는다.
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (!float.IsFinite(scores[index]) || !(options.Region?.Contains(x + template.ReferencePoint.X, y + template.ReferencePoint.Y) ?? true))
                {
                    scores[index] = float.NegativeInfinity;
                }
            }
        }
        var peaks = new PriorityQueue<(int X, int Y, float Score), (float Score, int Order)>();
        var truncated = false;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (scores[index] < options.MinimumScore || !IsMaximum(scores, width, height, x, y))
                {
                    continue;
                }
                peaks.Enqueue((x, y, scores[index]), (scores[index], -index));
                if (peaks.Count > options.MaximumCandidates)
                {
                    peaks.Dequeue();
                    truncated = true;
                }
            }
        }
        var selected = peaks.UnorderedItems.Select(value => value.Element)
            .OrderByDescending(value => value.Score).ThenBy(value => value.Y).ThenBy(value => value.X)
            .Select(peak => new CorrelationMatch(new(peak.X + template.ReferencePoint.X, peak.Y + template.ReferencePoint.Y),
                Math.Clamp(peak.Score, -1, 1))).ToImmutableArray();
        return new(selected, truncated, timer.Elapsed.TotalMilliseconds);
    }
    internal static double Overlap(ImagePoint first, ImagePoint second, CorrelationTemplate template)
    {
        var intersection = Math.Max(0, template.Width - Math.Abs(first.X - second.X)) * Math.Max(0, template.Height - Math.Abs(first.Y - second.Y));
        return intersection / ((2d * template.Width * template.Height) - intersection);
    }

    private static bool IsMaximum(float[] scores, int width, int height, int x, int y)
    {
        var index = (y * width) + x;
        for (var row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
        {
            for (var column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + 1); column++)
            {
                var other = (row * width) + column;
                if (scores[other] > scores[index] || (scores[other] == scores[index] && other < index))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
