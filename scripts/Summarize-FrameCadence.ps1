<#
.SYNOPSIS
60Hz 입력 진단의 활성 구간만 집계합니다. 물리 화면 표시 FPS가 아닙니다.
.EXAMPLE
./scripts/Summarize-FrameCadence.ps1 -Path artifacts/frame-cadence/before-1.json
.NOTES
같은 이름의 -render.csv와 -metrics.csv가 필요합니다. 백분위는 nearest-rank입니다.
최신 입력의 지연뿐 아니라 대기 슬롯 지연과 생략된 주기도 함께 읽어야 합니다.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
function Number($value) { [double]::Parse([string]$value, $invariant) }
function Distribution($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    [ordered]@{
        count = $sorted.Count
        p50 = $sorted[[Math]::Ceiling($sorted.Count * .50) - 1]
        p95 = $sorted[[Math]::Ceiling($sorted.Count * .95) - 1]
        p99 = $sorted[[Math]::Ceiling($sorted.Count * .99) - 1]
        max = $sorted[-1]
        overFrameBudget = @($sorted | Where-Object { $_ -gt (1000 / 60) }).Count
        overTwoFrames = @($sorted | Where-Object { $_ -gt (2000 / 60) }).Count
    }
}
$results = foreach ($file in $Path) {
    $data = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($data.stage -ne 'complete' -or $data.error) { throw "Incomplete diagnostic: $file" }
    $stem = [IO.Path]::ChangeExtension($file, $null).TrimEnd('.')
    $render = @(Import-Csv -LiteralPath ($stem + '-render.csv'))
    $metrics = @(Import-Csv -LiteralPath ($stem + '-metrics.csv'))
    $frequency = [double]$data.stopwatchFrequency
    foreach ($phase in $data.phases) {
        $frames = @($render | Where-Object {
            [long]$_.stopwatch_start_ticks -ge $phase.startTicks -and [long]$_.stopwatch_end_ticks -le $phase.endTicks
        })
        # Both ends of an interval must belong to the active phase; do not count setup or idle gaps.
        $intervals = @($frames | Where-Object {
            ([long]$_.stopwatch_start_ticks - (Number $_.frame_start_interval_ms) * $frequency / 1000) -ge $phase.startTicks
        } | ForEach-Object { Number $_.frame_start_interval_ms })
        $present = @($metrics | Where-Object {
            [long]$_.stopwatch_record_ticks -le $phase.endTicks -and
            ([long]$_.stopwatch_record_ticks - (Number $_.interval_ms) * $frequency / 1000) -ge $phase.startTicks
        } | ForEach-Object { Number $_.interval_ms })
        $inputIntervals = @(for ($index = 1; $index -lt $phase.navigation.Count; $index++) {
            ($phase.navigation[$index].ExecuteTicks - $phase.navigation[$index - 1].ExecuteTicks) * 1000 / $frequency
        })
        [ordered]@{
            file = $file
            phase = $phase.name
            durationMs = ($phase.endTicks - $phase.startTicks) * 1000 / $frequency
            renderStartIntervalMs = Distribution $intervals
            presentReturnIntervalMs = Distribution $present
            renderSubmitMs = Distribution @($frames | ForEach-Object { Number $_.submit_ms })
            inputLatestDueToExecuteMs = Distribution @($phase.navigation | ForEach-Object { ($_.ExecuteTicks - $_.DueTicks) * 1000 / $frequency })
            inputPendingSlotMs = Distribution @($phase.navigation | ForEach-Object { ($_.ExecuteTicks - $_.SlotQueuedTicks) * 1000 / $frequency })
            inputAppliedIntervalMs = Distribution $inputIntervals
            producerSkippedPeriods = ($phase.produced.ProducerSkippedPeriods | Measure-Object -Sum).Sum
            # Includes producer skips; the two skip counts must not be added together.
            inputSkippedPeriods = ($phase.navigation.SkippedSinceLastApplied | Measure-Object -Sum).Sum
            unappliedAtStop = $(if ($phase.produced.Count -gt 0) {
                $phase.produced[-1].Sequence - $(if ($phase.navigation.Count -gt 0) { $phase.navigation[-1].Sequence } else { 0 })
            } else { 0 })
            produced = $phase.produced.Count
            applied = $phase.navigation.Count
            gcPauseMs = $phase.gcPauseMs
            undoExact = $data.undoExact
            redoExact = $data.redoExact
            adapter = $data.GpuStatus
        }
    }
}
$results | ConvertTo-Json -Depth 6
