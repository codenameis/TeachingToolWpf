namespace AnomalyMonitor.Services;

public interface IMonitorFiles
{
    string? OpenVideo();
    string? OpenModel();
    string? SaveModel();
    void ShowEvidence(string path);
}
