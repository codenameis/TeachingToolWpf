using System.Windows;
using AnomalyMonitor.ViewModels;
using VisionAnomaly;
using VisionAnomaly.Controls;
using VisionAnomaly.Monitoring;
using VisionImaging.Abstractions;

namespace AnomalyMonitor.Services;

/// <summary>App 조립 지점의 영상/표시 어댑터. ViewModel에는 구체 viewport를 전달하지 않는다.</summary>
public sealed class MonitorLink : IDisposable
{
    private readonly CameraSession _session;
    private readonly CameraMonitorControl _control;
    private readonly CameraViewModel _model;
    private readonly Action<Action> _dispatch;
    public MonitorLink(CameraSession session, CameraMonitorControl control, CameraViewModel model, Action<Action> dispatch)
    {
        _session = session;
        _control = control;
        _model = model;
        _dispatch = dispatch;
        _session.FrameReady += OnFrame;
        _session.Updated += OnUpdated;
        _control.RendererStatusChanged += OnRendererStatus;
    }
    private void OnFrame(VideoFrame frame, AnomalyPrediction? prediction)
    {
        var marks = prediction?.Areas.Concat(prediction.MotionAreas).Select(area => new VideoAnnotation(new(area.X, area.Y, area.Width, area.Height), "이상 후보")).ToArray() ?? [];
        var region = _session.Snapshot.Region ?? AnalysisRegion.Full;
        var rectangle = new Rect(region.X * frame.Pixels.Width, region.Y * frame.Pixels.Height,
            region.Width * frame.Pixels.Width, region.Height * frame.Pixels.Height);
        _control.Present(frame.Retain(), marks, rectangle);
    }
    private void OnUpdated(SessionSnapshot snapshot)
    {
        if (snapshot.Mode is MonitorMode.Disconnected or MonitorMode.Connecting)
        {
            _dispatch(_control.Clear);
        }
    }
    private void OnRendererStatus(string status) => _dispatch(() => _model.Renderer = status);
    public void Dispose()
    {
        _session.FrameReady -= OnFrame;
        _session.Updated -= OnUpdated;
        _control.RendererStatusChanged -= OnRendererStatus;
    }
}
