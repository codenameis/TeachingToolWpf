using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>축소 검색 창의 읽기·검색·좌표 게시를 공통 제한 crop 실행기에 연결한다.</summary>
internal static class ShapeCoarseSearchScheduler
{
    internal sealed record Request(int Left, int Top, int Width, int Height, SearchRegion Region);
    internal sealed record Completion(int Index, int Left, int Top, MatchResult Result);

    public static double Run(IReadOnlyList<Request> requests, MatchingImageReader reader, int factor,
        ShapeModel model, SearchOptions options, ShapeMatcher matcher, CancellationToken token, Action<Completion> publish)
        => BoundedInspectionCropScheduler.Run(requests,
            request => (long)(request.Width / factor) * (request.Height / factor),
            (request, readToken) => reader.ReadReduced(request.Left, request.Top, request.Width, request.Height, factor, readToken),
            (request, image, workToken) => new ShapeCoarseSearchWindow(image, request.Left, request.Top, request.Region)
                .Search(model, options, workToken, matcher, requests.Count == 1 ? options.MaximumParallelism : 1),
            (index, result) => publish(new(index, requests[index].Left, requests[index].Top, result)),
            new(options.MaximumParallelism, PreparedSearchImage.MaximumPixels, PreparedSearchImage.MaximumPixels), token);
}
