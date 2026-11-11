using System;
using System.IO;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Fluent2.Tests;

public sealed class SharedPpOcrFixture : IDisposable
{
    public SharedPpOcrFixture()
    {
        string repoRoot = LocateRepoRoot();
        string modelDir = Path.Combine(repoRoot, "models", "ppocr");
        Service = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(modelDir, PpOcrExecutionDevice.Cpu));
    }

    public PpOcrInferenceService Service { get; }

    public void Dispose() => Service.Dispose();

    private static string LocateRepoRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "models", "ppocr")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate models/ppocr directory from test base directory.");
    }
}
