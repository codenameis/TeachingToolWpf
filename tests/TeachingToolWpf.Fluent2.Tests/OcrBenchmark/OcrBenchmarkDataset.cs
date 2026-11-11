using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeachingToolWpf.Fluent2.Tests.OcrBenchmark;

/// <summary>
/// Root container for externalized OCR/OCV ground-truth labels and print-group definitions (<c>labels.json</c>, schemaVersion 2).
/// </summary>
public sealed record OcrBenchmarkDataset(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("splitSeed")] int SplitSeed,
    [property: JsonPropertyName("splitPolicy")] string SplitPolicy,
    [property: JsonPropertyName("printGroups")] IReadOnlyList<OcrPrintGroupSpec> PrintGroups,
    [property: JsonPropertyName("entries")] IReadOnlyList<OcrBenchmarkEntry> Entries)
{
    public static (OcrBenchmarkDataset Dataset, string RepoRootPath, string LabelsFilePath) LoadFromDefaultLocation()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "tests", "TeachingToolWpf.Fluent2.Tests", "OcrBenchmark", "labels.json");
            if (File.Exists(candidate))
            {
                string json = File.ReadAllText(candidate);
                OcrBenchmarkDataset? parsed = JsonSerializer.Deserialize<OcrBenchmarkDataset>(json);
                if (parsed is null)
                {
                    throw new InvalidDataException($"Failed to deserialize {candidate}.");
                }

                return (parsed, current.FullName, candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/labels.json.");
    }
}
