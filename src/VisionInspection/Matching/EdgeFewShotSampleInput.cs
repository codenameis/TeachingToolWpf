using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Math;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record EdgeFewShotSampleInput(Guid RoiId, string Name, RoiFixturePose Pose, FewShotClassId ClassId);
