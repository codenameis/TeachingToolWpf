param([string]$InputDirectory = 'artifacts', [string]$OutputDirectory = 'docs/data')
$ErrorActionPreference = 'Stop'
function Percentile($Values, [double]$Fraction) {
    $sorted = @($Values | Sort-Object)
    [Math]::Round([double]$sorted[[Math]::Max(0, [Math]::Ceiling($sorted.Count * $Fraction) - 1)], 3)
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$selection = foreach ($phase in 'before', 'final') {
    $runs = @(Get-ChildItem -LiteralPath $InputDirectory -Filter "lowcore-$phase-*.json")
    if ($runs.Count -ne 3) { throw "Expected three $phase runs" }
    foreach ($file in $runs) {
        $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        foreach ($value in $report.Results | Where-Object Operation -eq 'select') {
            [pscustomobject]@{ Phase = $phase; File = $file.Name; Count = $value.Count; Pcb = $value.Hierarchy;
                P50Ms = $value.P50Ms; P95Ms = $value.P95Ms; AllocatedBytes = $value.AllocatedBytes }
        }
    }
}
$selection | Export-Csv (Join-Path $OutputDirectory 'lowcore-selection.csv') -NoTypeInformation -Encoding utf8
$editing = foreach ($mode in 'async', 'sync', 'priority') {
    $pattern = if ($mode -eq 'priority') { 'responsive-priority-*.json' } else { "responsive-lowcore-$mode-*.json" }
    $records = @(Get-ChildItem -LiteralPath $InputDirectory -Filter $pattern | ForEach-Object {
        $report = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($report.count -eq 32940) { $report.results | Where-Object { -not $_.warmup } }
    })
    foreach ($group in $records | Group-Object operation) {
        $r = $group.Group
        if ($r.Count -ne 9) { throw "Expected nine measured samples for $mode / $($group.Name)" }
        [pscustomobject]@{ Mode = $mode; Operation = $group.Name; Samples = $r.Count;
            DispatchP50Ms = Percentile $r.dispatchMs .5; DispatchP95Ms = Percentile $r.dispatchMs .95;
            CompleteP50Ms = Percentile $r.completeMs .5; GapP50Ms = Percentile $r.maxDispatcherGapMs .5;
            GapMaxMs = Percentile $r.maxDispatcherGapMs 1; NavigationMaxMs = Percentile $r.maxNavigationMs 1;
            AllocatedP50Bytes = Percentile $r.allocatedBytes .5 }
    }
}
$editing | Export-Csv (Join-Path $OutputDirectory 'lowcore-bulk-editing.csv') -NoTypeInformation -Encoding utf8
$selection | Format-Table
$editing | Format-Table
