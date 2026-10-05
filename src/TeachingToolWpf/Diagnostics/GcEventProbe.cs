using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Opt-in native GC event timestamps, separate from delayed listener delivery times.</summary>
internal sealed class GcEventProbe : EventListener
{
    private readonly object _gate = new();
    private readonly List<Sample> _samples = [];
    private int _overflow;

    public bool Available
    {
        get;
    }
    public long AnchorUtcTicks
    {
        get;
    }
    public long AnchorStopwatchTicks
    {
        get;
    }
    public double AnchorUncertaintyMilliseconds
    {
        get;
    }

    public GcEventProbe()
    {
        var before = Stopwatch.GetTimestamp();
        AnchorUtcTicks = DateTime.UtcNow.Ticks;
        var after = Stopwatch.GetTimestamp();
        AnchorStopwatchTicks = before + ((after - before) / 2);
        // Bracket half-width only: UTC resolution and native clock synchronization are not included.
        AnchorUncertaintyMilliseconds = (after - before) * 500d / Stopwatch.Frequency;
        foreach (var source in EventSource.GetSources())
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
                Available = true;
                break;
            }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs value)
    {
        if (value.EventSource.Name != "Microsoft-Windows-DotNETRuntime" || value.EventId is not (1 or 2 or 3 or 7 or 8 or 9))
        {
            return;
        }
        var sample = new Sample(value.EventId, value.EventName, value.TimeStamp.ToUniversalTime().Ticks,
            Stopwatch.GetTimestamp(), Field("Count"), Field("Depth"), Field("Reason"));
        lock (_gate)
        {
            if (_samples.Count < 4096)
            {
                _samples.Add(sample);
            }
            else
            {
                _overflow++;
            }
        }

        int? Field(string name)
        {
            if (value.PayloadNames is null || value.Payload is null)
            {
                return null;
            }
            for (var index = 0; index < value.PayloadNames.Count; index++)
            {
                if (value.PayloadNames[index] == name)
                {
                    return Convert.ToInt32(value.Payload[index]);
                }
            }
            return null;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                Available,
                AnchorUtcTicks,
                AnchorStopwatchTicks,
                AnchorUncertaintyMilliseconds,
                overflow = _overflow,
                events = _samples.ToArray()
            };
        }
    }

    private sealed record Sample(int Id, string? Name, long UtcTicks, long DeliveredTicks, int? Count, int? Generation, int? Reason);
}
