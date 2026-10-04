using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class RobustnessTests
{
    [Fact]
    public void TiledPreprocessingPreservesGradientAndEdgesAtSeams()
    {
        var pixels = new byte[269 * 259];
        new Random(41381).NextBytes(pixels);
        var image = new GrayImage(pixels, 269, 259, 269);
        var reference = GradientImage.Create(image, 8, default);
        var tiled = GradientImage.CreateTiled(image, 8, default);
        Assert.Equal(reference.X, tiled.X);
        Assert.Equal(reference.Y, tiled.Y);
        Assert.Equal(reference.Bits, tiled.Bits);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var a = reference.EdgeAt(x, y);
                var b = tiled.EdgeAt(x, y);
                Assert.Equal(a.HasValue, b.HasValue);
                if (a is { } edge)
                {
                    Assert.InRange(Math.Abs(edge.X - b!.Value.X), 0, 1e-10);
                    Assert.InRange(Math.Abs(edge.Y - b.Value.Y), 0, 1e-10);
                }
            }
        }
    }

    [Fact]
    public void FineAngleTemplatesCanShareExactCoarseScoresWithoutLosingMemberPoses()
    {
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = ShapeTrainer.Train(image, new(ReadOnlyMemory<byte>.Empty, new(48, 48)),
            new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 5,
                AngleStepDegrees = 0.1
            });
        var prepared = PreparedSearchImage.Create(image, 8, model.Levels.Length);
        var plain = new ShapeMatcher().Search(prepared, model, new());
        var grouped = new ShapeMatcher().Search(prepared, model, new()
        {
            GroupTemplates = true
        });
        Assert.True(grouped.Diagnostics.TemplatesEvaluated < plain.Diagnostics.TemplatesEvaluated);
        Assert.Equal(plain.Matches.ToArray(), grouped.Matches.ToArray());
    }
    [Fact]
    public void NegativeAndInvertedImageRespectPolarity()
    {
        var matcher = new ShapeMatcher();
        var scene = SyntheticScene.Create(151, 143, new MatchPose(72, 70, 0.12));
        var inverted = scene.Pixels.ToArray();
        for (var i = 0; i < inverted.Length; i++)
        {
            inverted[i] = (byte)(255 - inverted[i]);
        }
        var input = new GrayImage(inverted, scene.Width, scene.Height, scene.Stride);
        Assert.Empty(matcher.Search(input, MatchingTests.Train(), new()).Matches);
        Assert.Single(matcher.Search(input, MatchingTests.Train(ContrastPolarity.IgnoreLocal), new()).Matches);
        Assert.Empty(matcher.Search(new GrayImage(new byte[151 * 143], 151, 143, 151), MatchingTests.Train(), new()).Matches);
    }

    [Fact]
    public void PartialOcclusionKeepsFixedDenominator()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var full = PreparedSearchImage.Create(scene, 8, model.Levels.Length);
        var pixels = scene.Pixels.ToArray();
        for (var y = 0; y < 65; y++)
        {
            Array.Fill(pixels, (byte)25, y * 160, 160);
        }
        var partial = PreparedSearchImage.Create(new(pixels, 160, 150, 160), 8, model.Levels.Length);
        var a = ContinuousVerifier.Score(full.Gradients[0], model, new(80, 75, 0));
        var b = ContinuousVerifier.Score(partial.Gradients[0], model, new(80, 75, 0));
        Assert.True(b.Score < a.Score - 0.1);
        Assert.True(b.Support < a.Support - 0.1);
        var matches = new ShapeMatcher().Search(partial, model, new()
        {
            MinimumScore = 0.55,
            MinimumSupport = 0.5,
            CandidateThreshold = 0.5
        }).Matches;
        Assert.Single(matches);
        Assert.True(matches[0].Pose.X >= 79.5 && matches[0].Pose.X <= 80.5, matches[0].ToString());
    }

    [Fact]
    public void LowContrastNoiseBlurAndPaddingAreHandled()
    {
        var source = SyntheticScene.Create(155, 149, new MatchPose(73.4, 77.2, -0.1));
        var input = source.Pixels.Span;
        const int Stride = 167;
        var padded = Enumerable.Repeat((byte)255, Stride * 149).ToArray();
        var random = new Random(78113);
        for (var y = 0; y < 149; y++)
        {
            for (var x = 0; x < 155; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        sum += input[(Math.Clamp(y + dy, 0, 148) * 155) + Math.Clamp(x + dx, 0, 154)];
                    }
                }
                padded[(y * Stride) + x] = (byte)Math.Clamp(40 + (sum / 18) + random.Next(-3, 4), 0, 255);
            }
        }
        var result = new ShapeMatcher().Search(new GrayImage(padded, 155, 149, Stride), MatchingTests.Train(), new());
        var match = Assert.Single(result.Matches);
        Assert.InRange(match.Pose.X, 72.9, 73.9);
        Assert.InRange(match.Pose.Y, 76.7, 77.7);
    }

    [Fact]
    public void SimdMatchesScalarForUnalignedOddRowsAndMaximumAccumulator()
    {
        const int Width = 317;
        const int Height = 37;
        var bits = new byte[Width * Height];
        new Random(4901).NextBytes(bits);
        var map = OrientationMaps.Create(bits, Width, Height, 2, 4);
        var features = Enumerable.Range(0, 512).Select(i => new QuantizedFeature((i % 13) - 6, (i % 7) - 3, i % 8)).ToImmutableArray();
        var columns = (Width + 3) / 4;
        var rows = (Height + 3) / 4;
        var scalar = new ushort[columns * rows];
        var vector = new ushort[scalar.Length];
        CandidateSearch.Accumulate(map, features, scalar, columns, rows, false, default);
        CandidateSearch.Accumulate(map, features, vector, columns, rows, true, default);
        Assert.Equal(scalar, vector);
        Assert.Contains((ushort)51200, scalar);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var expected = features.Sum(f => (int)map.At(f.Bin, (x * 4) + f.X, (y * 4) + f.Y));
                Assert.Equal(expected, scalar[(y * columns) + x]);
            }
        }
    }

    [Fact]
    public async Task SharedPreprocessingIsImmutableAndCancellationIsObserved()
    {
        var model = MatchingTests.Train();
        var image = PreparedSearchImage.Create(SyntheticScene.Create(163, 151, new MatchPose(81, 75, 0.15)), 8, model.Levels.Length);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => new ShapeMatcher().Search(image, model,
            new()
            {
                UseSimd = i % 2 == 0,
                GroupTemplates = i % 2 == 1,
                MaximumParallelism = 2
            }))));
        foreach (var result in results)
        {
            Assert.Equal(results[0].Matches.ToArray(), result.Matches.ToArray());
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ShapeMatcher().Search(image, model, new(), cancelled.Token));
        var incompatible = PreparedSearchImage.Create(SyntheticScene.Create(64, 64), 9, 1);
        Assert.Throws<ArgumentException>(() => new ShapeMatcher().Search(incompatible, model, new()));
        var capped = new ShapeMatcher().Search(image, model, new()
        {
            MaximumCandidates = 1
        });
        Assert.True(capped.Diagnostics.CandidateBudgetExceeded);
    }

    [Fact]
    public void CircleRotationIsReportedUnobservableAndPreservesInitialPose()
    {
        const int Size = 128;
        var pixels = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt(((x + 0.5 - 64) * (x + 0.5 - 64)) + ((y + 0.5 - 64) * (y + 0.5 - 64)));
                pixels[(y * Size) + x] = (byte)(25 + (200 * Math.Clamp(28.5 - distance, 0, 1)));
            }
        }
        var image = new GrayImage(pixels, Size, Size, Size);
        var model = ShapeTrainer.Train(image, new(ReadOnlyMemory<byte>.Empty, new(64, 64)), new());
        var result = new ShapeMatcher().Search(image, model, new());
        var match = Assert.Single(result.Matches);
        Assert.Equal(RefinementStatus.IllConditioned, match.Refinement.Status);
        Assert.False(match.Refinement.AngleObservable);
        Assert.Equal(match.Refinement.InitialPose, match.Pose);
    }
}
