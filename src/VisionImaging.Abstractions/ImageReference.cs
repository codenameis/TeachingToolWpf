namespace VisionImaging.Abstractions;

public sealed record ImageReference(string Key, string Name, int Width, int Height, string Version = "");
