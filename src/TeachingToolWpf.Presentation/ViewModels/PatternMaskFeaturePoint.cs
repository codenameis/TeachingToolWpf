using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public sealed record PatternMaskFeaturePoint(
    double LocalX,
    double LocalY,
    double NormalX,
    double NormalY,
    bool IsExcluded);
