using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Offers 60 Hz navigation independently of UI progress during actual bulk-edit commands.</summary>
internal static class FrameCadenceRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? fallback : args[index + 1];
        }
        var report = Option("--cadence-report", "artifacts/frame-cadence.json");
        var imagePath = Option("--cadence-image", "");
        var grouping = args.Contains("--cadence-group");
        var operations = new List<object>();
        var phases = new List<object>();
        var count = 0;
        var navigationMilliseconds = 2000;
        int? cancelMilliseconds = null;
        var stage = "initialize";
        var undoExact = false;
        var redoExact = false;
        bool? groupCancelExact = null;
        GcEventProbe? gcEvents = null;
        string? renderError = null;
        void RendererStatus(string message)
        {
            if (message.StartsWith("렌더 오류:"))
            {
                renderError = message;
            }
        }
        viewport.RendererStatusChanged += RendererStatus;
        void Save(string? error = null) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            stage,
            fileBackend = ImageLoaderHost.Backend,
            imagePath,
            count,
            grouping,
            navigationMilliseconds,
            cancelMilliseconds,
            stopwatchFrequency = Stopwatch.Frequency,
            requestedNavigationHz = 60,
            navigation = "Synthetic viewport pan through EditorSession.SetViewport; off-screen window",
            measurement = "Active phase intervals only; Present return and GPU completion are separate external metrics",
            operations,
            phases,
            undoExact,
            redoExact,
            groupCancelExact,
            gcEvents = gcEvents?.Snapshot(),
            error,
            model.GpuStatus
        }, new JsonSerializerOptions { WriteIndented = true }));

        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            try
            {
                if (!int.TryParse(Option("--rois", "40000"), out count) || count <= 0)
                {
                    throw new ArgumentException("--rois must be a positive integer.");
                }
                if (!int.TryParse(Option("--cadence-navigation-ms", "2000"), out navigationMilliseconds) || navigationMilliseconds <= 0)
                {
                    throw new ArgumentException("--cadence-navigation-ms must be a positive integer.");
                }
                if (args.Contains("--cadence-cancel-ms"))
                {
                    if (!grouping || !int.TryParse(Option("--cadence-cancel-ms", ""), out var delay) || delay <= 0)
                    {
                        throw new ArgumentException("--cadence-cancel-ms requires --cadence-group and a positive integer.");
                    }
                    cancelMilliseconds = delay;
                }
                if (args.Contains("--cadence-gc-events"))
                {
                    gcEvents = new();
                }
                await model.InitializeAsync();
                if (imagePath.Length != 0)
                {
                    var path = Path.GetFullPath(imagePath);
                    await Navigate("file-import", () => model.Images.ReplaceAsync(new(path, "파일 준비 중 탐색", 0, 0, ImageFileVersion.Read(path))),
                        timeout: TimeSpan.FromMinutes(2));
                    if (model.Session.State.Image?.Key != path)
                    {
                        throw new InvalidOperationException("Cadence file import did not complete.");
                    }
                }
                var fixture = RoiInteractionRun.Fixture(count, !grouping);
                if (grouping)
                {
                    fixture = fixture.Select(geometry => new RoiGeometry((geometry.Center * .5) + new PointD(2048, 2048),
                        geometry.Width * .5, geometry.Height * .5, geometry.Angle));
                }
                model.Session.AddRange(RoiInteractionRun.DescribeFixture(fixture, !grouping));
                await model.SelectAll.ExecuteAsync();
                await model.EditCompletion;
                window.UpdateLayout();
                await Task.Delay(300);
                var original = model.Session.State;
                if (original.Rois.Count != count || original.Selection.Count != count)
                {
                    throw new InvalidOperationException("PCB fixture or initial selection count mismatch.");
                }

                await Navigate("steady", () => Task.Delay(2000), tailMilliseconds: 0);
                DocumentState changed;
                if (grouping)
                {
                    if (cancelMilliseconds is { } delay)
                    {
                        groupCancelExact = false;
                        var beforeCancel = model.Session.State;
                        var historyBytes = model.Session.HistoryBytes;
                        var canUndo = model.Session.CanUndo;
                        var canRedo = model.Session.CanRedo;
                        var undoName = model.Session.UndoName;
                        var redoName = model.Session.RedoName;
                        await Navigate("group-cancel", () => CancelGroup(delay),
                            timeout: TimeSpan.FromMilliseconds((long)delay + 30000));
                        // Validation is outside the active cadence phase so its full-document walk is not measured as editing.
                        VerifyDocument(beforeCancel, model.Session.State, "Group cancellation");
                        if (model.Session.HistoryBytes != historyBytes || model.Session.CanUndo != canUndo || model.Session.CanRedo != canRedo ||
                            model.Session.UndoName != undoName || model.Session.RedoName != redoName)
                        {
                            throw new InvalidOperationException("Group cancellation changed edit history.");
                        }
                        groupCancelExact = true;
                    }
                    await Navigate("group", () => Command("group", () =>
                    {
                        model.Group.Execute(null);
                        return Task.CompletedTask;
                    }));
                    changed = model.Session.State;
                    VerifyGroup(original, changed);
                    var navigationTimeout = TimeSpan.FromMilliseconds(Math.Max(30000L, (long)navigationMilliseconds + 5000));
                    await Navigate("group-pan", () => Task.Delay(navigationMilliseconds), tailMilliseconds: 0, timeout: navigationTimeout);
                    await Navigate("group-zoom", () => Task.Delay(navigationMilliseconds), tailMilliseconds: 0, zoom: true, timeout: navigationTimeout);
                }
                else
                {
                    var pasteStep = new PointD(20 / original.Viewport.Scale, 20 / original.Viewport.Scale);
                    await Navigate("copy-paste", async () =>
                    {
                        await Command("copy", () =>
                        {
                            model.Copy.Execute(null);
                            return Task.CompletedTask;
                        });
                        await Command("paste", () => model.Paste.ExecuteAsync());
                    });
                    changed = model.Session.State;
                    VerifyPaste(original, changed, pasteStep);
                }

                await Navigate("undo", () => Command("undo", () => model.Undo.ExecuteAsync()));
                VerifyDocument(original, model.Session.State, "Undo");
                undoExact = true;
                await Navigate("redo", () => Command("redo", () => model.Redo.ExecuteAsync()));
                VerifyDocument(changed, model.Session.State, "Redo");
                redoExact = true;
                if (renderError is not null)
                {
                    throw new InvalidOperationException(renderError);
                }
                stage = "complete";
                if (gcEvents is not null)
                {
                    // Native events arrive asynchronously. This wait is outside every active phase.
                    await Task.Delay(500);
                }
                Save();

                async Task Command(string name, Func<Task> execute)
                {
                    var pause = GC.GetTotalPauseDuration();
                    var start = Stopwatch.GetTimestamp();
                    var pending = execute();
                    var dispatched = Stopwatch.GetTimestamp();
                    await pending;
                    await model.EditCompletion;
                    await model.PasteCompletion;
                    operations.Add(new
                    {
                        name,
                        startTicks = start,
                        dispatchEndTicks = dispatched,
                        completionTicks = Stopwatch.GetTimestamp(),
                        gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                        rois = model.RoiCount,
                        selected = model.Session.State.Selection.Count
                    });
                }

                async Task CancelGroup(int delayMilliseconds)
                {
                    if (!model.Group.CanExecute(null))
                    {
                        throw new InvalidOperationException("Group cancellation command is unavailable.");
                    }
                    var pause = GC.GetTotalPauseDuration();
                    var start = Stopwatch.GetTimestamp();
                    long dispatchEnd = 0;
                    long? cancelStart = null;
                    long? cancelEnd = null;
                    long? completion = null;
                    long? canEditRecovered = null;
                    var inProgressAtCancel = false;
                    var escapeHandled = false;
                    PropertyChangedEventHandler recovery = (_, change) =>
                    {
                        if (change.PropertyName == nameof(model.CanEdit) && model.CanEdit)
                        {
                            canEditRecovered ??= Stopwatch.GetTimestamp();
                        }
                    };
                    model.PropertyChanged += recovery;
                    try
                    {
                        var pending = model.Group.ExecuteAsync();
                        var edit = model.EditCompletion;
                        dispatchEnd = Stopwatch.GetTimestamp();
                        // Let the dispatched preparation run before Escape. Elapsed delay does not identify its worker stage.
                        await Task.Delay(delayMilliseconds);
                        inProgressAtCancel = !model.CanEdit && (!pending.IsCompleted || !edit.IsCompleted);
                        cancelStart = Stopwatch.GetTimestamp();
                        escapeHandled = model.HandleKey(Key.Escape, false, false);
                        cancelEnd = Stopwatch.GetTimestamp();
                        await pending;
                        await edit;
                        completion = Stopwatch.GetTimestamp();
                        if (!inProgressAtCancel)
                        {
                            throw new InvalidOperationException("Grouping completed before Escape; cancellation was not exercised.");
                        }
                        if (!escapeHandled || !model.CanEdit)
                        {
                            throw new InvalidOperationException("Group cancellation did not restore editing availability.");
                        }
                        canEditRecovered ??= completion;
                    }
                    finally
                    {
                        model.PropertyChanged -= recovery;
                        operations.Add(new
                        {
                            name = "group-cancel",
                            startTicks = start,
                            dispatchEndTicks = dispatchEnd,
                            cancelDispatchTicks = cancelStart,
                            cancelDispatchEndTicks = cancelEnd,
                            completionTicks = completion,
                            canEditRecoveredTicks = canEditRecovered,
                            requestedCancelDelayMs = delayMilliseconds,
                            elapsedDispatchToCancelMs = cancelStart is { } requested ? Stopwatch.GetElapsedTime(dispatchEnd, requested).TotalMilliseconds : (double?)null,
                            cancelToCompletionMs = cancelStart is { } requestedAt && completion is { } completed
                                ? Stopwatch.GetElapsedTime(requestedAt, completed).TotalMilliseconds : (double?)null,
                            cancelToCanEditMs = inProgressAtCancel && cancelStart is { } cancelledAt && canEditRecovered is { } recovered
                                ? Stopwatch.GetElapsedTime(cancelledAt, recovered).TotalMilliseconds : (double?)null,
                            cancellationTiming = "Delay begins after command dispatch; elapsed time does not guarantee worker start or the Warm/Spatial stage.",
                            inProgressAtCancel,
                            escapeHandled,
                            canEditAtCompletion = model.CanEdit,
                            gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                            rois = model.RoiCount,
                            selected = model.Session.State.Selection.Count
                        });
                    }
                }

                async Task Navigate(string name, Func<Task> action, int tailMilliseconds = 1000, bool zoom = false, TimeSpan? timeout = null)
                {
                    stage = name;
                    Save();
                    var pause = GC.GetTotalPauseDuration();
                    var driver = new NavigationDriver(window.Dispatcher, model, zoom);
                    var start = driver.StartTicks;
                    long commandEnd = 0;
                    try
                    {
                        await action().WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
                        commandEnd = Stopwatch.GetTimestamp();
                        if (tailMilliseconds > 0)
                        {
                            await Task.Delay(tailMilliseconds);
                        }
                    }
                    finally
                    {
                        await driver.StopAsync();
                        phases.Add(new
                        {
                            name,
                            startTicks = start,
                            commandEndTicks = commandEnd,
                            endTicks = Stopwatch.GetTimestamp(),
                            gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                            produced = driver.Produced,
                            navigation = driver.Applied
                        });
                    }
                    if (renderError is not null)
                    {
                        throw new InvalidOperationException(renderError);
                    }
                    Save();
                }
            }
            catch (Exception ex)
            {
                Save(ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                viewport.RendererStatusChanged -= RendererStatus;
                gcEvents?.Dispose();
                window.Close();
            }
        };
    }

    private static void VerifyDocument(DocumentState expected, DocumentState actual, string operation)
    {
        // Navigation deliberately changes the view; ROI-only history must still restore every document value.
        if (expected.Image != actual.Image || expected.Rois.Count != actual.Rois.Count || !expected.Selection.SetEquals(actual.Selection) ||
            expected.Rois.Any(pair => !actual.Rois.TryGetValue(pair.Key, out var roi) || pair.Value != roi))
        {
            throw new InvalidOperationException(operation + " did not restore exact ROI values and selection.");
        }
    }

    private static void VerifyGroup(DocumentState before, DocumentState after)
    {
        var added = after.Rois.Values.Where(roi => !before.Rois.ContainsKey(roi.Id)).ToArray();
        if (added.Length != 1 || !added[0].IsGroup || after.Rois.Count != before.Rois.Count + 1 ||
            !after.Selection.SetEquals([added[0].Id]))
        {
            throw new InvalidOperationException("Grouping did not create and select exactly one parent.");
        }
        var group = added[0];
        foreach (var pair in before.Rois)
        {
            var expected = pair.Value.ParentId is null ? pair.Value with
            {
                ParentId = group.Id
            } : pair.Value;
            if (after.Rois[pair.Key] != expected)
            {
                throw new InvalidOperationException("Grouping changed a shape or did not preserve the explicit subtree.");
            }
        }
    }

    private static void VerifyPaste(DocumentState before, DocumentState after, PointD step)
    {
        var original = before.Rois.Values.OrderBy(roi => roi.Order).ToArray();
        var copied = after.Rois.Values.Where(roi => !before.Rois.ContainsKey(roi.Id)).OrderBy(roi => roi.Order).ToArray();
        if (copied.Length != original.Length || !after.Selection.SetEquals(copied.Where(roi => roi.ParentId is null).Select(roi => roi.Id)) ||
            before.Rois.Any(pair => !after.Rois.TryGetValue(pair.Key, out var roi) || pair.Value != roi))
        {
            throw new InvalidOperationException("Paste changed existing ROIs or did not select exactly the new roots.");
        }
        var remap = original.Select((roi, index) => (roi.Id, NewId: copied[index].Id)).ToDictionary(pair => pair.Id, pair => pair.NewId);
        var bounds = before.Snapshot.Spatial.Bounds;
        var image = before.Image!;
        var scale = Math.Min(1, Math.Min(image.Width / bounds.Width, image.Height / bounds.Height));
        var halfWidth = bounds.Width * scale / 2;
        var halfHeight = bounds.Height * scale / 2;
        var desired = bounds.Center + step;
        var center = new PointD(Math.Clamp(desired.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(desired.Y, halfHeight, image.Height - halfHeight));
        var orderBase = original.Max(roi => roi.Order);
        for (var index = 0; index < original.Length; index++)
        {
            var parent = original[index].ParentId is { } id ? remap[id] : (Guid?)null;
            var expectedGeometry = original[index].Geometry.WithFrame(center + ((original[index].Geometry.Center - bounds.Center) * scale),
                original[index].Geometry.Width * scale, original[index].Geometry.Height * scale, original[index].Geometry.Angle);
            if (copied[index].ParentId != parent || copied[index].Filter != original[index].Filter ||
                copied[index].Geometry != expectedGeometry || copied[index].IsGroup != original[index].IsGroup ||
                copied[index].Order != orderBase + index + 1 || copied[index].Name != original[index].Name[..Math.Min(original[index].Name.Length, 253)] + " 복사")
            {
                throw new InvalidOperationException("Paste did not preserve explicit parents and shape values.");
            }
        }
    }

    private sealed record NavigationRequest(long Sequence, long DueTicks, long CreatedTicks);
    private sealed record ProducedInput(long Sequence, long DueTicks, long CreatedTicks, long ProducerSkippedPeriods, bool ReplacedPending);
    private sealed record AppliedInput(long Sequence, long DueTicks, long CreatedTicks, long SlotQueuedTicks, long ExecuteTicks,
        long EndTicks, long SkippedSinceLastApplied, double Scale, double OffsetX, double OffsetY);

    private sealed class NavigationDriver
    {
        private readonly Dispatcher _dispatcher;
        private readonly EditorViewModel _model;
        private readonly ViewportTransform _initialView;
        private readonly bool _zoom;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _pump;
        private NavigationRequest? _latest;
        private DispatcherOperation? _operation;
        private Exception? _failure;
        private int _posted;
        private int _stopped;
        private long _slotQueuedTicks;
        private long _lastApplied;

        public long StartTicks { get; } = Stopwatch.GetTimestamp();
        public List<ProducedInput> Produced { get; } = [];
        public List<AppliedInput> Applied { get; } = [];

        public NavigationDriver(Dispatcher dispatcher, EditorViewModel model, bool zoom)
        {
            _dispatcher = dispatcher;
            _model = model;
            _initialView = model.Session.State.Viewport;
            _zoom = zoom;
            _pump = Task.Run(ProduceAsync);
        }

        private async Task ProduceAsync()
        {
            var token = _cancellation.Token;
            long next = 1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var due = StartTicks + (long)(next * (double)Stopwatch.Frequency / 60);
                    var now = Stopwatch.GetTimestamp();
                    if (now < due)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (due - now) * 1000d / Stopwatch.Frequency)), token);
                        continue;
                    }
                    var sequence = Math.Max(next, (long)((now - StartTicks) * 60d / Stopwatch.Frequency));
                    due = StartTicks + (long)(sequence * (double)Stopwatch.Frequency / 60);
                    var request = new NavigationRequest(sequence, due, now);
                    var replaced = Interlocked.Exchange(ref _latest, request) is not null;
                    Produced.Add(new(sequence, due, now, sequence - next, replaced));
                    next = sequence + 1;
                    Post();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Normal shutdown of the independent input producer.
            }
        }

        private void Post()
        {
            if (Volatile.Read(ref _stopped) != 0 || Interlocked.CompareExchange(ref _posted, 1, 0) != 0)
            {
                return;
            }
            Volatile.Write(ref _slotQueuedTicks, Stopwatch.GetTimestamp());
            _operation = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ApplyLatest));
        }

        private void ApplyLatest()
        {
            try
            {
                var request = Interlocked.Exchange(ref _latest, null);
                if (request is null || Volatile.Read(ref _stopped) != 0)
                {
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                var seconds = (request.DueTicks - StartTicks) / (double)Stopwatch.Frequency;
                var view = _initialView.Pan(new(Math.Sin(seconds * 2) * 6, Math.Sin(seconds * 1.3) * 3));
                if (_zoom)
                {
                    view = view.ZoomAt(new(650, 400), Math.Pow(2, Math.Sin(seconds * 2) * 2));
                }
                _model.Session.SetViewport(view);
                Applied.Add(new(request.Sequence, request.DueTicks, request.CreatedTicks, Volatile.Read(ref _slotQueuedTicks), start,
                    Stopwatch.GetTimestamp(), request.Sequence - _lastApplied - 1, view.Scale, view.Offset.X, view.Offset.Y));
                _lastApplied = request.Sequence;
            }
            catch (Exception ex)
            {
                _failure = ex;
                Interlocked.Exchange(ref _stopped, 1);
                _cancellation.Cancel();
            }
            finally
            {
                Interlocked.Exchange(ref _posted, 0);
                if (Volatile.Read(ref _latest) is not null)
                {
                    Post();
                }
            }
        }

        public async Task StopAsync()
        {
            Interlocked.Exchange(ref _stopped, 1);
            _cancellation.Cancel();
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(5));
                _operation?.Abort();
                Interlocked.Exchange(ref _latest, null);
                if (_failure is not null)
                {
                    throw new InvalidOperationException("Navigation callback failed.", _failure);
                }
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
