using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AnomalyMonitor.Infrastructure;
using AnomalyMonitor.Services;
using VisionAnomaly;
using VisionAnomaly.Monitoring;

namespace AnomalyMonitor.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly AlarmStore _store;
    private readonly IMonitorFiles _files;
    private readonly Action<Action> _dispatch;
    private readonly IReadOnlyList<CameraSession> _sessions;
    private CameraViewModel _selectedCamera;
    private AlarmViewModel? _selectedAlarm;
    private bool _singleView, _pendingOnly = true;
    private string _message = "① 영상 연결 → ② 정상 모습 수집 → ③ 감시 시작";

    public DashboardViewModel(IReadOnlyList<CameraSession> sessions, AlarmStore store, IMonitorFiles files, Action<Action> dispatch)
    {
        _sessions = sessions;
        _store = store;
        _files = files;
        _dispatch = dispatch;
        Cameras = new(sessions.Select((session, index) => new CameraViewModel($"CAM {index + 1:00}", session, files, dispatch, camera => SelectedCamera = camera)));
        _selectedCamera = Cameras[0];
        _selectedCamera.IsSelected = true;
        EventView = CollectionViewSource.GetDefaultView(Events);
        EventView.Filter = item => !PendingOnly || ((AlarmViewModel)item).IsPending;
        AcknowledgeCommand = new(() => ReviewAsync(ReviewStatus.Acknowledged), () => SelectedAlarm?.IsPending == true, Report);
        NormalCommand = new(() => ReviewAsync(ReviewStatus.Normal), () => SelectedAlarm?.IsPending == true, Report);
        EvidenceCommand = new(() => { _files.ShowEvidence(SelectedAlarm!.EvidenceFile); return Task.CompletedTask; }, () => SelectedAlarm is not null, Report);
        foreach (var session in sessions)
        {
            session.AlarmRaised += OnAlarm;
        }

        try
        {
            foreach (var record in store.LoadRecent())
            {
                Events.Add(new(record));
            }
        }
        catch (Exception error) { Message = "기록 읽기 실패: " + error.Message; }
    }

    public ObservableCollection<CameraViewModel> Cameras
    {
        get;
    }
    public ObservableCollection<AlarmViewModel> Events { get; } = [];
    public ICollectionView EventView
    {
        get;
    }
    public CameraViewModel SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (value is null || !Set(ref _selectedCamera, value))
            {
                return;
            }

            foreach (var camera in Cameras)
            {
                camera.IsSelected = camera == value;
            }

            Changed(nameof(FirstVisible));
            Changed(nameof(SecondVisible));
            Changed(nameof(ThirdVisible));
            Changed(nameof(FourthVisible));
        }
    }
    public bool SingleView
    {
        get => _singleView; set
        {
            if (Set(ref _singleView, value))
            {
                Changed(nameof(FirstVisible));
                Changed(nameof(SecondVisible));
                Changed(nameof(ThirdVisible));
                Changed(nameof(FourthVisible));
            }
        }
    }
    public bool FirstVisible => !SingleView || SelectedCamera == Cameras[0];
    public bool SecondVisible => !SingleView || SelectedCamera == Cameras[1];
    public bool ThirdVisible => !SingleView || SelectedCamera == Cameras[2];
    public bool FourthVisible => !SingleView || SelectedCamera == Cameras[3];
    public bool PendingOnly
    {
        get => _pendingOnly; set
        {
            if (Set(ref _pendingOnly, value))
            {
                EventView.Refresh();
            }
        }
    }
    public string Message
    {
        get => _message; set => Set(ref _message, value);
    }
    public int PendingCount => Events.Count(item => item.IsPending);
    public string StoragePath => _store.DirectoryPath;
    public AlarmViewModel? SelectedAlarm
    {
        get => _selectedAlarm;
        set
        {
            if (Set(ref _selectedAlarm, value))
            {
                AcknowledgeCommand.Refresh();
                NormalCommand.Refresh();
                EvidenceCommand.Refresh();
            }
        }
    }
    public MonitorCommand AcknowledgeCommand
    {
        get;
    }
    public MonitorCommand NormalCommand
    {
        get;
    }
    public MonitorCommand EvidenceCommand
    {
        get;
    }

    private void OnAlarm(AlarmRecord record) => _dispatch(() =>
    {
        var item = new AlarmViewModel(record);
        Events.Insert(0, item);
        while (Events.Count > 200)
        {
            Events.RemoveAt(Events.Count - 1);
        }

        SelectedAlarm = item;
        Changed(nameof(PendingCount));
        Message = $"{record.CameraName} · {item.Title} · 증거 저장됨";
    });

    private async Task ReviewAsync(ReviewStatus status)
    {
        var alarm = SelectedAlarm;
        if (alarm is null || !alarm.IsPending)
        {
            return;
        }

        await Task.Run(() => _store.Save(alarm.Record with { Review = status }));
        alarm.SetReview(status);
        EventView.Refresh();
        Changed(nameof(PendingCount));
        AcknowledgeCommand.Refresh();
        NormalCommand.Refresh();
        Message = "확인 결과를 저장했습니다. 정상 기준은 자동 변경하지 않습니다.";
    }

    private void Report(Exception error) => Message = "실행 불가: " + error.Message;
    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.AlarmRaised -= OnAlarm;
        }

        foreach (var camera in Cameras)
        {
            camera.Dispose();
        }
    }
}
