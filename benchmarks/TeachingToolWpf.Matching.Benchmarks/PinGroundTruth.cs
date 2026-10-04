using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.PatternMatching;

internal sealed record ExpectedPin(int Id, int ChipColumn, int ChipRow, int PinColumn, int PinRow,
    double Left, double Top, ImagePoint Center, ImagePoint UnrotatedReference);
internal sealed record PinAssignment(int Match, int? PinId, MatchPose Pose, double? PositionErrorPixels);
internal sealed record PinValidation(bool Passed, int UniquePins, int DuplicateMatches, int UnmappedMatches,
    int InaccurateMatches, double? MaximumPositionErrorPixels, int[] MissingPinIds, PinAssignment[] Assignments);

internal static class PinGroundTruth
{
    private const double ReferenceOffsetX = 759.518 - 760;
    private const double ReferenceOffsetY = 546.178 - 565.5;

    public static ExpectedPin[] Create(int chipColumns = 4, int chipRows = 4)
    {
        var pins = new List<ExpectedPin>(256);
        for (var chipRow = 0; chipRow < chipRows; chipRow++)
        {
            for (var chipColumn = 0; chipColumn < chipColumns; chipColumn++)
            {
                for (var pinRow = 0; pinRow < 2; pinRow++)
                {
                    for (var pinColumn = 0; pinColumn < 8; pinColumn++)
                    {
                        var left = (chipColumn * 2048) + ((pinColumn + 2) * 180);
                        var top = (chipRow * 2048) + (pinRow == 0 ? 491 : 1401);
                        var center = new ImagePoint(left + 40, top + 74.5);
                        pins.Add(new(pins.Count, chipColumn, chipRow, pinColumn, pinRow, left, top, center,
                            new(center.X + ReferenceOffsetX, center.Y + ReferenceOffsetY)));
                    }
                }
            }
        }
        return pins.ToArray();
    }

    public static PinValidation Validate(ExpectedPin[] expected, ImmutableArray<ShapeMatch> matches)
    {
        var found = new HashSet<int>();
        var assignments = new List<PinAssignment>();
        var duplicate = 0;
        var unmapped = 0;
        var inaccurate = 0;
        double? maximumError = null;
        foreach (var match in matches)
        {
            var pose = match.Pose;
            var pin = expected.FirstOrDefault(value => pose.X >= value.Left && pose.X < value.Left + 80 &&
                pose.Y >= value.Top && pose.Y < value.Top + 149);
            if (pin is null)
            {
                unmapped++;
                assignments.Add(new(assignments.Count, null, pose, null));
                continue;
            }
            var cos = Math.Cos(pose.Angle);
            var sin = Math.Sin(pose.Angle);
            var expectedX = pin.Center.X + (pose.Scale * ((cos * ReferenceOffsetX) - (sin * ReferenceOffsetY)));
            var expectedY = pin.Center.Y + (pose.Scale * ((sin * ReferenceOffsetX) + (cos * ReferenceOffsetY)));
            var error = Math.Sqrt(((pose.X - expectedX) * (pose.X - expectedX)) + ((pose.Y - expectedY) * (pose.Y - expectedY)));
            maximumError = Math.Max(maximumError ?? 0, error);
            if (error > 1)
            {
                inaccurate++;
            }
            if (!found.Add(pin.Id))
            {
                duplicate++;
            }
            assignments.Add(new(assignments.Count, pin.Id, pose, error));
        }
        var missing = expected.Where(pin => !found.Contains(pin.Id)).Select(pin => pin.Id).ToArray();
        return new(found.Count == expected.Length && duplicate == 0 && unmapped == 0 && inaccurate == 0,
            found.Count, duplicate, unmapped, inaccurate, maximumError, missing, assignments.ToArray());
    }
}
