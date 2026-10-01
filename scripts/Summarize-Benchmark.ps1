param([string]$Directory = 'artifacts/benchmarks-final', [double]$WarmupSeconds = 10)
$ErrorActionPreference = 'Stop'
function Percentile($Values, [double]$Percent) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    return [Math]::Round($ordered[[Math]::Max(0, [Math]::Ceiling($ordered.Count * $Percent) - 1)], 3)
}
$results = foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.csv') {
    $metadata = Get-Content -LiteralPath ($file.FullName + '.json') -Raw | ConvertFrom-Json
    $rows = @(Import-Csv -LiteralPath $file.FullName | Where-Object { [double]$_.seconds -ge $WarmupSeconds -and $_.source_sequence -ne '' })
    if ($rows.Count -lt 2) { throw "Insufficient frame samples: $($file.Name)" }
    $duration = [double]$rows[-1].seconds - [double]$rows[0].seconds
    $cpu = @($rows | ForEach-Object { [double]$_.cpu_ms })
    $gpu = @($rows | Where-Object gpu_ms -ne '' | ForEach-Object { [double]$_.gpu_ms })
    $interval = @($rows | ForEach-Object { [double]$_.interval_ms })
    $latency = @($rows | ForEach-Object { [double]$_.source_to_submit_ms })
    [pscustomobject]@{
        File = $file.Name; Format = $metadata.Format; RoiCount = $metadata.RoiCount
        Seconds = [Math]::Round($duration, 3); Samples = $rows.Count
        DisplayFps = [Math]::Round(($rows.Count - 1) / $duration, 3)
        SourceFps = [Math]::Round(([long]$rows[-1].source_sequence - [long]$rows[0].source_sequence) / $duration, 3)
        CpuMeanMs = [Math]::Round(($cpu | Measure-Object -Average).Average, 3)
        CpuP50Ms = Percentile $cpu .50; CpuP95Ms = Percentile $cpu .95; CpuP99Ms = Percentile $cpu .99
        GpuP50Ms = Percentile $gpu .50; GpuP95Ms = Percentile $gpu .95; GpuP99Ms = Percentile $gpu .99
        IntervalP50Ms = Percentile $interval .50; IntervalP95Ms = Percentile $interval .95; IntervalP99Ms = Percentile $interval .99
        ReadyToPresentP50Ms = Percentile $latency .50; ReadyToPresentP95Ms = Percentile $latency .95; ReadyToPresentP99Ms = Percentile $latency .99
        Dropped = [long]$rows[-1].dropped - [long]$rows[0].dropped
        PeakPrivateMiB = [Math]::Round((($rows | ForEach-Object { [long]$_.private_bytes } | Measure-Object -Maximum).Maximum / 1MB), 1)
        PeakTextureMiB = [Math]::Round((($rows | ForEach-Object { [long]$_.texture_bytes } | Measure-Object -Maximum).Maximum / 1MB), 1)
    }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'summary.json') -Encoding utf8
$results | Format-Table File, DisplayFps, SourceFps, CpuP95Ms, GpuP95Ms, ReadyToPresentP95Ms, Dropped, PeakPrivateMiB -AutoSize
