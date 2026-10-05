using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCompute.ILGPU;
using Xunit.Abstractions;

namespace VisionCompute.Tests;

public sealed class CudaCandidateScoringTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 19, 2, 4)]
    [InlineData(27, 1, 2, 4)]
    [InlineData(33, 29, 0, 1)]
    [InlineData(37, 23, 2, 4)]
    [InlineData(71, 59, 4, 7)]
    public void BatchedScoresEqualIndependentSquareSpreadAndCosine(int width, int height, int radius, int step)
    {
        var request = CreateRequest(width, height, radius, step);
        var original = request.OrientationBits.ToArray();
        using var scorer = new CudaImagePreprocessor();
        var actual = scorer.Score(request);
        Assert.Equal(request.GridWidth, actual.GridWidth);
        Assert.Equal(request.GridHeight, actual.GridHeight);
        Assert.Equal(request.Templates.Length, actual.TemplateCount);
        Assert.Equal(ReferenceScores(request), actual.Scores);
        Assert.Equal(original, request.OrientationBits.ToArray());
        Assert.Equal(1, scorer.CandidateDiagnostics.Calls);
        Assert.Equal(width * height, scorer.CandidateDiagnostics.ProcessedPixels);
        Assert.Equal(request.Templates.Length, scorer.CandidateDiagnostics.TemplateCount);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(scorer.CandidateDiagnostics));
    }

    [Fact]
    public void EveryOrientationMaskMatchesTheLookupContract()
    {
        var bits = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var templates = Enumerable.Range(0, 8).Select(bin => ImmutableArray.Create(new CandidateScoreFeature(0, 0, bin)))
            .ToImmutableArray();
        var request = new ShapeCandidateScoreRequest(bits, 256, 1, 0, 1, templates);
        using var scorer = new CudaImagePreprocessor();
        Assert.Equal(ReferenceScores(request), scorer.Score(request).Scores);
    }

    [Fact]
    public void GrowthShrinkAndPreprocessingShareResourcesWithoutChangingReturnedScores()
    {
        using var scorer = new CudaImagePreprocessor();
        var firstRequest = CreateRequest(17, 13, 2, 4);
        var first = scorer.Score(firstRequest);
        foreach (var (width, height) in new[] { (129, 113), (8, 9), (17, 13) })
        {
            var request = CreateRequest(width, height, 2, 4);
            Assert.Equal(ReferenceScores(request), scorer.Score(request).Scores);
            var image = CudaPreprocessingTests.RandomImage(width, height, 3);
            CudaPreprocessingTests.AssertPlanes(CudaPreprocessingTests.ReferenceGradient(image, 3), scorer.Prepare(image, 3));
        }
        Assert.Equal(ReferenceScores(firstRequest), first.Scores);
        Assert.Equal(4, scorer.CandidateDiagnostics.Calls);
        Assert.Equal(3, scorer.Diagnostics.Calls);
    }

    [Fact]
    public void MaximumFeatureCountRetainsTheExactUnsignedShortSum()
    {
        var request = new ShapeCandidateScoreRequest(new byte[] { 1 }, 1, 1, 0, 1,
            [Enumerable.Repeat(new CandidateScoreFeature(0, 0, 0), 512).ToImmutableArray()]);
        using var scorer = new CudaImagePreprocessor();
        Assert.Equal(new ushort[] { 51200 }, scorer.Score(request).Scores);
    }

    [Fact]
    public void CancellationAndDisposalDoNotReuseAnIncompleteSubmission()
    {
        var scorer = new CudaImagePreprocessor();
        var request = CreateRequest(31, 27, 2, 4);
        Assert.Throws<OperationCanceledException>(() => scorer.Score(request, new(true)));
        Assert.Equal(0, scorer.CandidateDiagnostics.Calls);
        Assert.Equal(ReferenceScores(request), scorer.Score(request).Scores);
        scorer.Dispose();
        scorer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scorer.Score(request));
    }

    [Fact]
    public void InvalidRequestsFailBeforeDeviceSubmission()
    {
        using var scorer = new CudaImagePreprocessor();
        var valid = CreateRequest(31, 27, 2, 4);
        foreach (var request in new[]
        {
            valid with { Width = 0 },
            valid with { Radius = -1 },
            valid with { Step = 0 },
            valid with { OrientationBits = new byte[3] },
            valid with { Templates = [] },
            valid with { Templates = [ImmutableArray<CandidateScoreFeature>.Empty] },
            valid with { Templates = [ImmutableArray.Create(new CandidateScoreFeature(0, 0, 8))] },
            valid with { Templates = [Enumerable.Repeat(new CandidateScoreFeature(0, 0, 0), 513).ToImmutableArray()] }
        })
        {
            Assert.Throws<ArgumentException>(() => scorer.Score(request));
        }
        Assert.Equal(0, scorer.CandidateDiagnostics.Calls);
    }

    [Fact]
    public void OversizedOutputBatchIsRejectedBeforeDeviceSubmission()
    {
        var request = new ShapeCandidateScoreRequest(new byte[ShapeCandidateScoreRequest.MaximumPixels],
            2048, 1024, 2, 1, Enumerable.Repeat(ImmutableArray.Create(new CandidateScoreFeature(0, 0, 0)), 9).ToImmutableArray());
        using var scorer = new CudaImagePreprocessor();
        Assert.Throws<ArgumentException>(() => scorer.Score(request));
        Assert.Equal(0, scorer.CandidateDiagnostics.Calls);
    }

    [Fact]
    public void InFlightCancellationDrainsTheStreamBeforeTheNextRequest()
    {
        using var scorer = new CudaImagePreprocessor();
        var features = Enumerable.Repeat(new CandidateScoreFeature(0, 0, 0), 512).ToImmutableArray();
        var bits = new byte[2048 * 1024];
        Array.Fill(bits, (byte)1);
        var request = new ShapeCandidateScoreRequest(bits, 2048, 1024, 2, 1,
            Enumerable.Repeat(features, 8).ToImmutableArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(5));
        Assert.Throws<OperationCanceledException>(() => scorer.Score(request, cancellation.Token));
        var next = CreateRequest(33, 29, 2, 4);
        Assert.Equal(ReferenceScores(next), scorer.Score(next).Scores);
    }

    private static ShapeCandidateScoreRequest CreateRequest(int width, int height, int radius, int step)
    {
        var random = new Random(1049);
        var bits = new byte[width * height];
        random.NextBytes(bits);
        var templates = ImmutableArray.CreateBuilder<ImmutableArray<CandidateScoreFeature>>();
        for (var template = 0; template < 7; template++)
        {
            var features = ImmutableArray.CreateBuilder<CandidateScoreFeature>();
            for (var feature = 0; feature < 12 + template; feature++)
            {
                features.Add(new(random.Next(-9, 10), random.Next(-9, 10), random.Next(8)));
            }
            features.Add(new(-width - 20, height + 20, template));
            templates.Add(features.ToImmutable());
        }
        return new(bits, width, height, radius, step, templates.ToImmutable());
    }

    private static ushort[] ReferenceScores(ShapeCandidateScoreRequest request)
    {
        var responses = new byte[request.Width * request.Height * 8];
        var bits = request.OrientationBits.Span;
        for (var y = 0; y < request.Height; y++)
        {
            for (var x = 0; x < request.Width; x++)
            {
                var combined = 0;
                for (var row = Math.Max(0, y - request.Radius); row <= Math.Min(request.Height - 1, y + request.Radius); row++)
                {
                    for (var column = Math.Max(0, x - request.Radius); column <= Math.Min(request.Width - 1, x + request.Radius); column++)
                    {
                        combined |= bits[(row * request.Width) + column];
                    }
                }
                for (var bin = 0; bin < 8; bin++)
                {
                    var value = 0d;
                    for (var sourceBin = 0; sourceBin < 8; sourceBin++)
                    {
                        if ((combined & (1 << sourceBin)) != 0)
                        {
                            value = Math.Max(value, Math.Abs(Math.Cos((bin - sourceBin) * Math.PI / 8)));
                        }
                    }
                    responses[(((y * request.Width) + x) * 8) + bin] = (byte)Math.Round(value * 100);
                }
            }
        }
        var scores = new ushort[request.Templates.Length * request.GridWidth * request.GridHeight];
        for (var template = 0; template < request.Templates.Length; template++)
        {
            for (var y = 0; y < request.GridHeight; y++)
            {
                for (var x = 0; x < request.GridWidth; x++)
                {
                    var sum = 0;
                    foreach (var feature in request.Templates[template])
                    {
                        var sampleX = (x * request.Step) + feature.X;
                        var sampleY = (y * request.Step) + feature.Y;
                        if (sampleX >= 0 && sampleX < request.Width && sampleY >= 0 && sampleY < request.Height)
                        {
                            sum += responses[(((sampleY * request.Width) + sampleX) * 8) + feature.Bin];
                        }
                    }
                    scores[(((template * request.GridHeight) + y) * request.GridWidth) + x] = (ushort)sum;
                }
            }
        }
        return scores;
    }
}
