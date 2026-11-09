using System;
using System.IO;
using System.Linq;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using Xunit;
using Xunit.Abstractions;

namespace VisionAlgorithm.Tests;

public sealed class UnifiedNewSubsystemsTests
{
    private readonly ITestOutputHelper _output;

    public UnifiedNewSubsystemsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void UnifiedNewSubsystems_All19BmpCasesAnd8kSheets_PassEndToEnd()
    {
        string exportDir = Path.Combine(
            AppContext.BaseDirectory,
            "SyntheticVisionTestSuites");

        UnifiedNewSubsystems8kRunner.UnifiedVerificationReport report =
            UnifiedNewSubsystems8kRunner.RunAllVerifications(exportDir);

        foreach (var c in report.Cases)
        {
            _output.WriteLine($"[{c.SuiteName}] {c.CaseName} -> {(c.Passed ? "PASS" : "FAIL")} ({c.ElapsedMilliseconds:F2} ms) | {c.Details}");
        }

        _output.WriteLine($"Total Cases: {report.Cases.Count}, AllPassed: {report.AllPassed}, TotalElapsed: {report.TotalElapsedMilliseconds:F2} ms");
        _output.WriteLine($"Exported 19 .bmp files + GroundTruth_Manifest.txt to: {exportDir}");

        Assert.True(File.Exists(Path.Combine(exportDir, "GroundTruth_Manifest.txt")));
        Assert.Equal(19, report.Cases.Count);

        foreach (var c in report.Cases)
        {
            Assert.True(c.Passed, $"Failed case: [{c.SuiteName}] {c.CaseName} -> {c.Details}");
        }

        Assert.True(report.AllPassed);
    }
}
