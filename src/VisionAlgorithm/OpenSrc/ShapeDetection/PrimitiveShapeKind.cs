using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.ShapeDetection;

[Flags]
public enum PrimitiveShapeKind
{
    None = 0,
    Triangle = 1,
    Rectangle = 2,
    Circle = 4,
    Cross = 8,
    Ring = 16,
    RectangleFrame = 32,
    Diamond = 64,
    CrossHash = 128,
    DoubleSquare = 256,
    Star = 512,
    Asterisk = 1024,
    All = Triangle | Rectangle | Circle | Cross | Ring | RectangleFrame | Diamond | CrossHash | DoubleSquare | Star | Asterisk
}
