<#
.SYNOPSIS
--cadence-gc-events 진단의 native suspension 시작/재개 시각을 활성 구간과 대응합니다.
.NOTES
비동기 callback 도착 시각을 사용하지 않습니다. UTC/QPC anchor 대응은 근사이며
수집 누락/overflow가 있으면 완료된 pause 쌍만 집계한 불완전 자료입니다.
counter 비교 허용오차는 1 ms + anchor bracket 반폭의 2배인 진단용 추정이며,
UTC 해상도·native 시계 동기화·counter 읽기 경계의 보장된 오차 상한이 아닙니다.
구버전에서 bracket 값이 없으면 허용오차와 mismatch 판정은 null입니다.
counter 일치는 완전 수집을 보장하지 않으며 overflow는 관리 목록 제한만 셉니다.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
if ($data.stage -ne 'complete' -or $data.error -or -not $data.gcEvents.Available) {
    throw 'Completed report with native GC events is required.'
}
$frequency = [double]$data.stopwatchFrequency
$anchor = $data.gcEvents
$anchorUncertaintyProperty = $anchor.PSObject.Properties['AnchorUncertaintyMilliseconds']
$anchorUncertaintyMs = if ($null -ne $anchorUncertaintyProperty -and $null -ne $anchorUncertaintyProperty.Value) {
    [double]$anchorUncertaintyProperty.Value
} else { $null }
$counterToleranceMs = if ($null -ne $anchorUncertaintyMs) {
    1.0 + (2 * $anchorUncertaintyMs)
} else { $null }
$pending = $null
$unmatched = 0
$pauses = @(foreach ($event in $anchor.events) {
    if ($event.Id -eq 9) {
        if ($null -ne $pending) { $unmatched++ }
        $pending = $event
    } elseif ($event.Id -eq 3) {
        if ($null -eq $pending) { $unmatched++; continue }
        if ($pending.Reason -in 1,6) {
            [pscustomobject]@{
                startTicks = $anchor.AnchorStopwatchTicks + ($pending.UtcTicks - $anchor.AnchorUtcTicks) * $frequency / 10000000
                endTicks = $anchor.AnchorStopwatchTicks + ($event.UtcTicks - $anchor.AnchorUtcTicks) * $frequency / 10000000
                milliseconds = ($event.UtcTicks - $pending.UtcTicks) / 10000
                reason = $pending.Reason
            }
        }
        $pending = $null
    }
})
if ($null -ne $pending) { $unmatched++ }
$phases = @(foreach ($phase in $data.phases) {
    $overlaps = @($pauses | Where-Object { $_.endTicks -gt $phase.startTicks -and $_.startTicks -lt $phase.endTicks })
    $durations = @($overlaps | ForEach-Object {
        ([Math]::Min($_.endTicks, $phase.endTicks) - [Math]::Max($_.startTicks, $phase.startTicks)) * 1000 / $frequency
    })
    $overlappedMs = [double](($durations | Measure-Object -Sum).Sum)
    $counterDifferenceMs = if ($null -ne $phase.gcPauseMs) {
        [double]$phase.gcPauseMs - $overlappedMs
    } else { $null }
    $counterMismatch = if ($null -ne $counterDifferenceMs -and $null -ne $counterToleranceMs) {
        [Math]::Abs($counterDifferenceMs) -gt $counterToleranceMs
    } else { $null }
    $assessment = if ($anchor.overflow -gt 0 -or $unmatched -gt 0) {
        'incomplete-events'
    } elseif ($null -eq $counterMismatch) {
        'unassessed'
    } elseif ($counterMismatch) {
        'counter-mismatch'
    } else {
        'consistent-estimate'
    }
    [pscustomobject]@{
        name = $phase.name
        suspensions = $overlaps.Count
        overlappedMs = $overlappedMs
        maximumWholePauseMs = ($overlaps.milliseconds | Measure-Object -Maximum).Maximum
        wholePausesOverFrameBudget = @($overlaps | Where-Object milliseconds -gt (1000 / 60)).Count
        cumulativeCounterMs = $phase.gcPauseMs
        counterMinusOverlapMs = $counterDifferenceMs
        counterComparisonToleranceMs = $counterToleranceMs
        counterMismatch = $counterMismatch
        assessment = $assessment
    }
})
[ordered]@{
    report = $Path
    anchorUncertaintyMs = $anchorUncertaintyMs
    anchorUncertaintyScope = 'UTC/QPC bracket half-width only; UTC resolution and native clock synchronization are excluded.'
    counterToleranceRule = 'Estimated 1 ms allowance plus twice the anchor half-width; not a guaranteed error bound. Unknown without a measured bracket.'
    completeness = 'Not guaranteed: consistent-estimate is counter agreement within an estimated tolerance, not proof of complete event delivery or 60 FPS.'
    overflow = $anchor.overflow
    overflowScope = 'Managed sample limit only; native EventPipe loss or delayed delivery is not counted.'
    unmatchedSuspensionEvents = $unmatched
    phases = $phases
    pauses = $pauses
} | ConvertTo-Json -Depth 5
