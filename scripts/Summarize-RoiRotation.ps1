<#
.SYNOPSIS
회전 진단의 입력 비용과 같은 시간 구간에 렌더된 preview 프레임을 요약합니다.
.EXAMPLE
./scripts/Summarize-RoiRotation.ps1 -Path 'artifacts/rotation-after-*.json' -Format Json
.EXAMPLE
./scripts/Summarize-RoiRotation.ps1 -SkipRender -IncludeMetrics
.NOTES
백분위는 nearest-rank 방식입니다. schema 1은 입력마다 JSON을 저장하던 진단이며,
schema 2는 입력 loop 이후 저장하므로 프레임 간격을 동일 조건으로 비교하지 마세요.
GPU metrics에는 공통 timestamp가 없어 IncludeMetrics 결과는 전체 실행 범위로만 표시합니다.
DisplayedMeasured는 모델 preview가 관측된 입력만 포함하며 물리 화면 표시 여부가 아닙니다.
FirstPreview의 RenderPreparationEndMs는 CPU draw 기록 종료이며 Present/모니터 지연이 아닙니다.
#>
[CmdletBinding()]
param(
    [string[]]$Path = @('artifacts/rotation-before-*.json', 'artifacts/rotation-after-*.json',
        'artifacts/rotation-final-*.json', 'artifacts/rotation-ready-*.json', 'artifacts/rotation-release-*.json'),
    [ValidateSet('Table', 'Json')][string]$Format = 'Table',
    [switch]$SkipRender,
    [switch]$IncludeMetrics
)
$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Read-Number($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    return [double]::Parse([string]$Value, [Globalization.NumberStyles]::Float, $invariant)
}

function Get-Stats($Values) {
    $numbers = @($Values | ForEach-Object { Read-Number $_ } | Where-Object { $null -ne $_ } | Sort-Object)
    if ($numbers.Count -eq 0) { return $null }
    $sum = 0.0
    foreach ($number in $numbers) { $sum += $number }
    [pscustomobject]@{
        Count = $numbers.Count
        P50 = $numbers[[Math]::Max(0, [Math]::Ceiling($numbers.Count * .5) - 1)]
        P95 = $numbers[[Math]::Max(0, [Math]::Ceiling($numbers.Count * .95) - 1)]
        Max = $numbers[-1]
        Sum = $sum
    }
}

function Get-InputStats($Samples) {
    $result = [ordered]@{ Samples = @($Samples).Count }
    foreach ($field in @('CallbackMs', 'LayoutMs', 'CallbackAllocatedBytes', 'LayoutAllocatedBytes', 'ThreadCpuMs', 'GcPauseMs', 'MaxGapMs')) {
        $result[$field] = Get-Stats @($Samples | ForEach-Object { $_.$field })
    }
    [pscustomobject]$result
}

function Find-LinkedFile([string]$RecordedPath, [IO.FileInfo]$ReportFile, [string]$FallbackName) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RecordedPath)) {
        $candidates += $RecordedPath
        if (-not [IO.Path]::IsPathRooted($RecordedPath)) { $candidates += Join-Path $ReportFile.DirectoryName $RecordedPath }
        $candidates += Join-Path $ReportFile.DirectoryName ([IO.Path]::GetFileName($RecordedPath))
    }
    $candidates += Join-Path $ReportFile.DirectoryName $FallbackName
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Get-Item -LiteralPath $candidate).FullName }
    }
    return $null
}

function Get-RenderSummary($Report, $Measured, [int]$Schema, [string]$CsvPath, $Rows) {
    if ($SkipRender) { return [pscustomobject]@{ Status = 'skipped'; Source = $CsvPath } }
    if ($Schema -lt 2) { return [pscustomobject]@{ Status = 'unavailable-schema1-phase-mixed'; Source = $CsvPath } }
    if (-not $CsvPath) { return [pscustomobject]@{ Status = 'missing-render-csv'; Source = $null } }
    $frequency = Read-Number $Report.stopwatchFrequency
    if (@($Measured).Count -eq 0 -or -not $frequency -or $frequency -le 0) {
        return [pscustomobject]@{ Status = 'missing-measured-window'; Source = $CsvPath }
    }
    $ordered = @($Measured | Sort-Object StartTicks)
    if ($null -eq $ordered[0].StartTicks -or $null -eq $ordered[-1].LayoutEndTicks) {
        return [pscustomobject]@{ Status = 'missing-sample-timestamps'; Source = $CsvPath }
    }
    $windowStart = [long]$ordered[0].StartTicks
    $windowEnd = [long]$ordered[-1].LayoutEndTicks + [long][Math]::Ceiling($frequency * .016)
    $required = @('transform_preview', 'stopwatch_start_ticks', 'stopwatch_end_ticks', 'stopwatch_frequency', 'frame_start_interval_ms')
    if ($rows.Count -eq 0 -or @($required | Where-Object { $_ -notin $rows[0].PSObject.Properties.Name }).Count -gt 0) {
        return [pscustomobject]@{ Status = 'missing-render-timestamps'; Source = $CsvPath }
    }
    $frames = @($rows | Where-Object {
        $_.transform_preview -eq '1' -and [long]$_.stopwatch_start_ticks -ge $windowStart -and
        [long]$_.stopwatch_end_ticks -le $windowEnd -and [long]$_.stopwatch_end_ticks -ge [long]$_.stopwatch_start_ticks
    })
    if (@($frames | Where-Object { (Read-Number $_.stopwatch_frequency) -ne $frequency }).Count -gt 0) {
        return [pscustomobject]@{ Status = 'clock-frequency-mismatch'; Source = $CsvPath }
    }
    # The first frame can span the warmup/measurement boundary; exclude that interval only.
    $intervals = @($frames | ForEach-Object {
        $interval = Read-Number $_.frame_start_interval_ms
        if ($null -ne $interval -and [double]$_.stopwatch_start_ticks - $interval * $frequency / 1000 -ge $windowStart) { $interval }
    })
    [pscustomobject]@{
        Status = $(if ($frames.Count) { 'measured-preview-only' } else { 'no-matching-preview-frames' })
        Source = $CsvPath
        WindowStartTicks = $windowStart
        WindowEndTicks = $windowEnd
        WindowTailMs = 16
        Frames = $frames.Count
        PrepareMs = Get-Stats @($frames.prepare_ms)
        PaintMs = Get-Stats @($frames.paint_ms)
        SubmitMs = Get-Stats @($frames.submit_ms)
        StageSumCpuMs = Get-Stats @($frames | ForEach-Object { (Read-Number $_.prepare_ms) + (Read-Number $_.paint_ms) + (Read-Number $_.submit_ms) })
        FrameStartIntervalMs = Get-Stats $intervals
        RoiUploadBytes = Get-Stats @($frames.roi_upload_bytes)
        RoiInstances = Get-Stats @($frames.roi_instances)
    }
}

function Get-FirstPreview($Report, [int]$Schema, [bool]$HasPreviewSamples, [string]$CsvPath, $Rows) {
    $result = [ordered]@{
        Status = 'unavailable'; Source = $CsvPath; Origin = 'first-input-sample-start'
        RenderStartMs = $null; RenderPreparationEndMs = $null
        FirstNonNullSampleIndex = $null; SampleCallbackEndUpperBoundMs = $null
        RenderAngleDegrees = $null
    }
    if ($Schema -lt 2 -or -not $HasPreviewSamples) { $result.Status = 'unavailable-preview-sample-field'; return [pscustomobject]$result }
    $frequency = Read-Number $Report.stopwatchFrequency
    $firstInput = @($Report.samples | Sort-Object index)[0]
    if (-not $frequency -or $frequency -le 0 -or $null -eq $firstInput.StartTicks) {
        $result.Status = 'missing-input-clock'; return [pscustomobject]$result
    }
    $start = [long]$firstInput.StartTicks
    $firstObserved = $Report.samples | Where-Object { $null -ne $_.displayedAngleDegrees } | Sort-Object index | Select-Object -First 1
    if ($null -ne $firstObserved) {
        $result.FirstNonNullSampleIndex = $firstObserved.index
        if ($null -ne $firstObserved.CallbackEndTicks) {
            $result.SampleCallbackEndUpperBoundMs = ([long]$firstObserved.CallbackEndTicks - $start) * 1000 / $frequency
        }
    }
    if ($SkipRender) { $result.Status = 'render-skipped'; return [pscustomobject]$result }
    if (-not $CsvPath) { $result.Status = 'missing-render-csv'; return [pscustomobject]$result }
    $required = @('transform_preview', 'stopwatch_start_ticks', 'stopwatch_end_ticks', 'stopwatch_frequency')
    if (@($Rows).Count -eq 0 -or @($required | Where-Object { $_ -notin $Rows[0].PSObject.Properties.Name }).Count -gt 0) {
        $result.Status = 'missing-render-timestamps'; return [pscustomobject]$result
    }
    # Search the whole run, including warmup: the first rendered preview can precede the measured window.
    $firstFrame = $Rows | Where-Object {
        $_.transform_preview -eq '1' -and [long]$_.stopwatch_start_ticks -ge $start -and
        [long]$_.stopwatch_end_ticks -ge [long]$_.stopwatch_start_ticks
    } | Sort-Object { [long]$_.stopwatch_start_ticks } | Select-Object -First 1
    if ($null -eq $firstFrame) { $result.Status = 'no-preview-frame'; return [pscustomobject]$result }
    if ((Read-Number $firstFrame.stopwatch_frequency) -ne $frequency) {
        $result.Status = 'clock-frequency-mismatch'; return [pscustomobject]$result
    }
    $result.Status = 'render-preparation-observed-not-physical-display'
    $result.RenderStartMs = ([long]$firstFrame.stopwatch_start_ticks - $start) * 1000 / $frequency
    $result.RenderPreparationEndMs = ([long]$firstFrame.stopwatch_end_ticks - $start) * 1000 / $frequency
    $result.RenderAngleDegrees = Read-Number $firstFrame.preview_angle_degrees
    [pscustomobject]$result
}

function Get-MetricsSummary([string]$CsvPath) {
    if (-not $IncludeMetrics) { return [pscustomobject]@{ Status = 'source-link-only'; Source = $CsvPath } }
    if (-not $CsvPath) { return [pscustomobject]@{ Status = 'missing-metrics-csv'; Source = $null } }
    $rows = @(Import-Csv -LiteralPath $CsvPath)
    [pscustomobject]@{
        Status = 'whole-run-unmatched-to-rotation'; Source = $CsvPath; Frames = $rows.Count
        CpuMs = Get-Stats @($rows.cpu_ms); GpuMs = Get-Stats @($rows.gpu_ms)
        PresentMs = Get-Stats @($rows.present_ms); FrameIntervalMs = Get-Stats @($rows.interval_ms)
        # dropped is a counter, not one independent increment per CSV row: never sum it.
        DroppedCounterMax = $(if ($rows.Count) { (Get-Stats @($rows.dropped)).Max } else { $null })
        DroppedCounterLast = $(if ($rows.Count) { Read-Number $rows[-1].dropped } else { $null })
    }
}

$files = @($Path | ForEach-Object { Get-ChildItem -Path $_ -File -ErrorAction SilentlyContinue } |
    Where-Object { $_.Name -notmatch '(?i)(^|[-_.])summary([-_.]|$)' } | Sort-Object FullName -Unique)
if ($files.Count -eq 0) { throw '입력 JSON 파일이 없습니다. -Path glob을 확인하세요.' }
$summaries = @(foreach ($file in $files) {
    try {
        $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        # A redirected summary may have an arbitrary filename. Only raw diagnostic objects have samples.
        if ($report -is [array] -or $null -eq $report.PSObject.Properties['samples']) { continue }
        $schema = if ($report.schemaVersion) { [int]$report.schemaVersion } else { 1 }
        $measured = @($report.samples | Where-Object { $_.warmup -eq $false })
        $warmup = @($report.samples | Where-Object { $_.warmup -eq $true })
        $hasPreviewSamples = $schema -ge 2 -and @($report.samples).Count -gt 0 -and
            $null -ne $report.samples[0].PSObject.Properties['displayedAngleDegrees']
        $displayed = @($measured | Where-Object { $null -ne $_.displayedAngleDegrees })
        $renderPath = Find-LinkedFile $report.renderProfilePath $file ($file.BaseName + '-render.csv')
        $metricsPath = Find-LinkedFile $report.metricsPath $file ($file.BaseName + '.csv')
        $renderRows = @(if (-not $SkipRender -and $schema -ge 2 -and $renderPath) { Import-Csv -LiteralPath $renderPath })
        $operations = @(foreach ($group in @($report.operations | Where-Object { $_.name -in @('commit', 'undo', 'redo') } | Group-Object name)) {
            [pscustomobject]@{
                Name = $group.Name
                DispatchMs = Get-Stats @($group.Group.dispatchMs)
                CompletionMs = Get-Stats @($group.Group.completionMs)
                LayoutMs = Get-Stats @($group.Group.layoutMs)
                DispatchAllocatedBytes = Get-Stats @($group.Group.dispatchAllocatedBytes)
                MaxGapMs = Get-Stats @($group.Group.MaxGapMs)
            }
        })
        [pscustomobject]@{
            File = $file.FullName; Schema = $schema; Stage = $report.stage
            RoiCount = $report.count; Layout = $report.layout; Mode = $report.mode
            Selection = $(if ($report.selectionMode) { $report.selectionMode } else { 'all' })
            SelectedCount = $report.selectedCount; AffectedCount = $report.affectedCount
            Error = $report.error; UndoExact = $report.undoExact; RedoExact = $report.redoExact
            TimingMethod = $(if ($schema -ge 2) { 'buffered-after-loop' } else { 'json-write-between-samples' })
            Measured = Get-InputStats $measured
            DisplayedMeasured = $(if ($hasPreviewSamples) { Get-InputStats $displayed } else { $null })
            WaitingMeasuredSamples = $(if ($hasPreviewSamples) { $measured.Count - $displayed.Count } else { $null })
            FirstPreview = Get-FirstPreview $report $schema $hasPreviewSamples $renderPath $renderRows
            Warmup = Get-InputStats $warmup
            WarmupSamples = $warmup
            Operations = $operations
            Render = Get-RenderSummary $report $measured $schema $renderPath $renderRows
            Metrics = Get-MetricsSummary $metricsPath
        }
    }
    catch { [pscustomobject]@{ File = $file.FullName; SummaryError = $_.Exception.Message } }
})

if ($Format -eq 'Json') { ConvertTo-Json -InputObject $summaries -Depth 12; return }
foreach ($summary in $summaries) {
    Write-Output "`n$($summary.File)"
    if ($summary.SummaryError) { Write-Output "요약 실패: $($summary.SummaryError)"; continue }
    $summary | Select-Object Schema, Stage, RoiCount, Layout, Mode, Selection, TimingMethod, WaitingMeasuredSamples, UndoExact, RedoExact, Error | Format-List
    $summary.FirstPreview | Format-List
    $table = @(foreach ($scope in @('Measured', 'DisplayedMeasured', 'Warmup')) {
        foreach ($property in $summary.$scope.PSObject.Properties | Where-Object Name -ne 'Samples') {
            if ($null -ne $property.Value) {
                [pscustomobject]@{ Scope = $scope; Metric = $property.Name; N = $property.Value.Count; P50 = $property.Value.P50; P95 = $property.Value.P95; Max = $property.Value.Max; Sum = $property.Value.Sum }
            }
        }
    }
    foreach ($operation in $summary.Operations) {
        foreach ($property in $operation.PSObject.Properties | Where-Object Name -ne 'Name') {
            if ($null -ne $property.Value) {
                [pscustomobject]@{ Scope = $operation.Name; Metric = $property.Name; N = $property.Value.Count; P50 = $property.Value.P50; P95 = $property.Value.P95; Max = $property.Value.Max; Sum = $property.Value.Sum }
            }
        }
    }
    foreach ($scope in @('Render', 'Metrics')) {
        foreach ($property in $summary.$scope.PSObject.Properties | Where-Object { $null -ne $_.Value -and $null -ne $_.Value.PSObject.Properties['P50'] }) {
            [pscustomobject]@{ Scope = $scope; Metric = $property.Name; N = $property.Value.Count; P50 = $property.Value.P50; P95 = $property.Value.P95; Max = $property.Value.Max; Sum = $property.Value.Sum }
        }
    })
    $table | Format-Table -AutoSize
    $summary.WarmupSamples | Select-Object index, CallbackMs, LayoutMs, CallbackAllocatedBytes, LayoutAllocatedBytes, MaxGapMs | Format-Table -AutoSize
    Write-Output "Render: $($summary.Render.Status) [$($summary.Render.Source)]"
    Write-Output "Metrics: $($summary.Metrics.Status) [$($summary.Metrics.Source)]"
    if ($IncludeMetrics) { Write-Output "전체 실행 dropped counter: max=$($summary.Metrics.DroppedCounterMax), last=$($summary.Metrics.DroppedCounterLast) (회전 단계에 귀속하지 않음)" }
}
