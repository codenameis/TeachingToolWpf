using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Layers;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Controls;

public sealed record VideoAnnotation(Rect Bounds, string Label);
