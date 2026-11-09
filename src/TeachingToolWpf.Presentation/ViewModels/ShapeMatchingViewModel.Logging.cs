using System.Diagnostics;
using TeachingToolWpf.Models;
using VisionCanvas.Rendering;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private const int MaximumInspectionLogs = 20;
    private readonly List<InspectionRunLog> _inspectionLogs = [];
    public IReadOnlyList<InspectionRunLog> InspectionLogs => _inspectionLogs.AsReadOnly();
    public string InspectionLogText => IsTemplateMethod ? "OpenCV의 검사 시간은 ‘결과’ 요약에서 확인하세요. 이 방식은 단계별 JSON 로그를 제공하지 않습니다." :
        IsPatMaxMethod ? _patMaxState.LastRunLogText :
        IsCpuMethod ? CpuLogText : _inspectionLogs.Count == 0
        ? "하이브리드 형상 검사 후 단계별 시간이 표시됩니다. 최근 20회 보관 · JSON 저장 가능"
        : $"최근 검사 · {_inspectionLogs.Count}회 기록 보관 (JSON에 전체 저장)\n" + _inspectionLogs[^1].Describe();

    public async Task SaveInspectionLogsAsync(string path)
    {
        var snapshot = _inspectionLogs.ToArray();
        try
        {
            if (IsCpuMethod)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_cpuLogs.ToArray(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(path, json);
            }
            else
            {
                await InspectionRunLog.SaveAsync(snapshot, path);
            }
            Status = "검사 로그를 저장했습니다.";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            Status = "로그 저장 실패: " + error.Message;
        }
    }

    private void AddInspectionLog(InspectionRunLog log)
    {
        if (_inspectionLogs.Count == MaximumInspectionLogs)
        {
            _inspectionLogs.RemoveAt(0);
        }
        _inspectionLogs.Add(log);
        Notify(nameof(InspectionLogText));
    }

    private void UpdateInspectionLog(Guid? id, Func<InspectionRunLog, InspectionRunLog> update, bool notify = false)
    {
        var index = _inspectionLogs.FindIndex(log => log.RunId == id);
        if (index < 0)
        {
            return;
        }
        _inspectionLogs[index] = update(_inspectionLogs[index]);
        if (notify)
        {
            Notify(nameof(InspectionLogText));
        }
    }

    public void RecordOverlayPresentation(OverlayPresentation presentation)
    {
        var cpu = _cpuLogs.FirstOrDefault(value => value.PresentationId == presentation.PresentationId);
        if (cpu is not null && cpu.PresentedMilliseconds is null && presentation.CompletedTimestamp >= cpu.StartedTimestamp)
        {
            UpdateCpuLog(cpu.RunId, value => value with
            {
                PresentedMilliseconds = Stopwatch.GetElapsedTime(value.StartedTimestamp, presentation.CompletedTimestamp).TotalMilliseconds
            });
        }
        var log = _inspectionLogs.FirstOrDefault(value => value.PresentationId == presentation.PresentationId);
        if (log is null || log.RendererPresentationMilliseconds is not null ||
            presentation.CompletedTimestamp < log.StartedTimestamp)
        {
            return;
        }
        UpdateInspectionLog(log.RunId, value => value with
        {
            RendererPresentationMilliseconds = Stopwatch.GetElapsedTime(value.StartedTimestamp, presentation.CompletedTimestamp).TotalMilliseconds
        }, true);
    }
}
