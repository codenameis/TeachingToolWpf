param([Parameter(Mandatory)][string]$Prefix)

$ErrorActionPreference = 'Stop'
$report = Get-Content -LiteralPath "$Prefix.json" -Raw | ConvertFrom-Json
$frames = @(Import-Csv -LiteralPath "$Prefix.csv")
$budget = 1000.0 / 60

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    return $sorted[[Math]::Ceiling($sorted.Count * $Percentile) - 1]
}

$sections = foreach ($section in $report.Sections) {
    $rows = @($frames | Where-Object {
        [long]$_.stopwatch_record_ticks -ge $section.StartTicks -and
        [long]$_.stopwatch_record_ticks -le $section.EndTicks
    })
    if ($rows.Count -lt 2) {
        throw "$($section.Name): 렌더 프레임이 충분하지 않습니다."
    }
    $intervals = [double[]]@($rows | ForEach-Object { [double]$_.interval_ms })
    $gpuTimes = [double[]]@($rows | Where-Object { $_.gpu_ms -ne '' } | ForEach-Object { [double]$_.gpu_ms })
    $fps = ($rows.Count - 1) * $report.StopwatchFrequency /
        ([long]$rows[-1].stopwatch_record_ticks - [long]$rows[0].stopwatch_record_ticks)
    $max = ($intervals | Measure-Object -Maximum).Maximum
    $hasPacing = $null -ne $rows[0].PSObject.Properties['pacing_wait_ms']
    $hasDiagnostics = $null -ne $rows[0].PSObject.Properties['scene_age_ms']
    $display = [System.Collections.Generic.List[object]]::new()
    $displayOrdered = $true
    foreach ($row in $rows) {
        if ([long]$row.display_present_count -le 0) { continue }
        if ($display.Count -gt 0) {
            $previous = $display[$display.Count - 1]
            if ([long]$row.display_present_count -lt [long]$previous.display_present_count -or
                [long]$row.display_refresh_count -lt [long]$previous.display_refresh_count) { $displayOrdered = $false }
            if ($row.display_present_count -eq $previous.display_present_count) { continue }
        }
        $display.Add($row)
    }
    $displayCadence = $null
    $excessRefreshes = $null
    if ($displayOrdered -and $display.Count -ge 2) {
        $presentDelta = [long]$display[-1].display_present_count - [long]$display[0].display_present_count
        $refreshDelta = [long]$display[-1].display_refresh_count - [long]$display[0].display_refresh_count
        $syncDelta = [long]$display[-1].display_sync_refresh_count - [long]$display[0].display_sync_refresh_count
        $qpcDelta = [long]$display[-1].display_sync_qpc - [long]$display[0].display_sync_qpc
        if ($refreshDelta -gt 0 -and $syncDelta -gt 0 -and $qpcDelta -gt 0 -and $presentDelta -gt 0) {
            $displayCadence = $presentDelta / $refreshDelta * $syncDelta * $report.StopwatchFrequency / $qpcDelta
            $excessRefreshes = 0
            for ($i = 1; $i -lt $display.Count; $i++) {
                $excessRefreshes += [Math]::Max(0,
                    ([long]$display[$i].display_refresh_count - [long]$display[$i - 1].display_refresh_count) -
                    ([long]$display[$i].display_present_count - [long]$display[$i - 1].display_present_count))
            }
        }
    }
    [pscustomobject]@{
        Name = $section.Name
        SelectedRoiCount = $section.SelectedRoiCount
        SelectedBlocks = $section.SelectedBlocks
        Frames = $rows.Count
        Fps = [Math]::Round($fps, 3)
        IntervalP95Ms = Get-Percentile $intervals .95
        IntervalMaxMs = $max
        OverBudgetFrames = @($intervals | Where-Object { $_ -gt $budget }).Count
        Over20MsFrames = @($intervals | Where-Object { $_ -gt 20 }).Count
        CpuP95Ms = Get-Percentile ([double[]]@($rows | ForEach-Object { [double]$_.cpu_ms })) .95
        GpuP95Ms = if ($gpuTimes.Count) { Get-Percentile $gpuTimes .95 } else { $null }
        GpuMaxMs = if ($gpuTimes.Count) { ($gpuTimes | Measure-Object -Maximum).Maximum } else { $null }
        PresentP95Ms = Get-Percentile ([double[]]@($rows | ForEach-Object { [double]$_.present_ms })) .95
        FenceWaitAverageMs = if ($hasDiagnostics) { ($rows.fence_wait_ms | Measure-Object -Average).Average } else { $null }
        PacingWaitAverageMs = if ($hasPacing) { ($rows.pacing_wait_ms | Measure-Object -Average).Average } else { $null }
        SceneAgeAverageMs = if ($hasDiagnostics) { ($rows.scene_age_ms | Measure-Object -Average).Average } else { $null }
        SceneAgeP95Ms = if ($hasDiagnostics) { Get-Percentile ([double[]]$rows.scene_age_ms) .95 } else { $null }
        DisplayStatisticsSamples = $display.Count
        DisplayCadenceFps = $displayCadence
        DisplayExcessRefreshes = $excessRefreshes
        InputP95Ms = $section.InputP95Milliseconds
        InputMaxMs = $section.InputMaxMilliseconds
        ProcessCpuMs = $section.ProcessCpuMilliseconds
        AverageCpuCores = $section.AverageCpuCores
        ProcessCpuMsPerFrame = if ($null -ne $section.ProcessCpuMilliseconds) { $section.ProcessCpuMilliseconds / $rows.Count } else { $null }
        AllocatedBytes = $section.AllocatedBytes
        GcPauseMs = $section.GcPauseMilliseconds
        PrivateBytes = $section.PrivateBytes
        Strict60Fps = if ($fps -ge 60 -and $max -le $budget) { 'PASS' } else { 'FAIL' }
    }
}
$result = [pscustomobject]@{
    LogicalRoiCount = $report.LogicalRoiCount
    Adapter = $report.Adapter
    Viewport = $report.Viewport
    LogicalProcessorCount = $report.LogicalProcessorCount
    RendererAssemblySha256 = $report.RendererAssemblySha256
    RepeatedRoiShaderSha256 = $report.RepeatedRoiShaderSha256
    Correctness = $report.Correctness
    BudgetMs = $budget
    Note = 'Strict60Fps는 렌더 제출 간격으로 이전 기준 유지. DisplayCadenceFps는 DXGI 표시/refresh/QPC 카운터 기반 추정이며 물리 화면 촬영이 아니다. SceneAge는 장면 제출부터 그리기 시작까지이며 물리 입력 지연이 아니다. ProcessCpuMs는 앱의 사용자+커널 CPU 시간 합이며 대기 경과 시간인 CpuP95Ms와 다르다. AverageCpuCores는 CPU 시간/측정 경과 시간, ProcessCpuMsPerFrame은 구간 CPU 시간/제출 수의 추정이다. DWM/다른 프로세스는 제외한다. 정적·워밍업 제외. 없는 추가 계측은 null.'
    Sections = @($sections)
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$Prefix.summary.json" -Encoding utf8
$sections | Format-Table Name, Frames, Fps, IntervalP95Ms, IntervalMaxMs, Over20MsFrames, Strict60Fps
