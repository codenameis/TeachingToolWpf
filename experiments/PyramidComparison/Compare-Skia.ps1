param([int]$Repeats = 5, [string]$OutputRoot = '', [switch]$JpegOnly)
$ErrorActionPreference = 'Stop'
if ($Repeats -lt 1) { throw 'Repeats must be positive.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repo ('artifacts/skia-comparison/paired-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Use a new output directory. Existing caches are preserved.' }
$inputs = @(
    'artifacts/pyramid-comparison/inputs/8k-gray8.png',
    'artifacts/pyramid-comparison/inputs/8k-color.jpg',
    'artifacts/streaming-codecs/large-codec-inputs/8k-adam7.png',
    'artifacts/streaming-codecs/large-codec-inputs/8k-progressive.jpg'
)
if ($JpegOnly) { $inputs = @($inputs | Where-Object { $_.EndsWith('.jpg') }) }
$programs = @{
    Streaming = Join-Path $PSScriptRoot 'Streaming/bin/Release/net10.0/Streaming.dll'
    Skia = Join-Path $PSScriptRoot 'Skia/bin/Release/net10.0/Skia.dll'
}
foreach ($path in @($programs.Values) + @($inputs | ForEach-Object { Join-Path $repo $_ })) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing input/build: $path" }
}
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($OutputRoot))
if ($drive.AvailableFreeSpace -lt (2 + $(if ($JpegOnly) { 1.35 } else { 2.3 }) * $Repeats) * 1GB) { throw 'Insufficient disk space for preserved caches.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$rows = @()
foreach ($input in $inputs) {
    $name = [IO.Path]::GetFileNameWithoutExtension($input)
    for ($run = 1; $run -le $Repeats; $run++) {
        $order = if ($run % 2) { @('Streaming', 'Skia') } else { @('Skia', 'Streaming') }
        foreach ($backend in $order) {
            $id = "$name-$backend-$run"
            $report = Join-Path $OutputRoot "$id.json"
            & dotnet $programs[$backend] (Join-Path $repo $input) (Join-Path $OutputRoot "$id-cache") $report
            if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $id (see $report)" }
            $value = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            $rows += [pscustomobject]@{
                Input = $name; Backend = $backend; Run = $run
                ImportMs = $value.ImportMilliseconds; ReopenMs = $value.ReopenMilliseconds
                PeakPrivateMiB = $value.PeakPrivateBytes / 1MB
                PeakWorkingMiB = $value.PeakWorkingSetBytes / 1MB
                ManagedAllocatedMiB = $value.ManagedAllocatedBytes / 1MB
                TileP95Ms = $value.TilePasses[2].P95Milliseconds
                CpuMs = $value.CpuMilliseconds; Report = $report
            }
            Write-Host "$id PASS import=$([math]::Round($value.ImportMilliseconds, 1)) ms"
        }
    }
}
function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count % 2) { return $sorted[[int][math]::Floor($sorted.Count / 2)] }
    return ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
}
$summary = foreach ($group in ($rows | Group-Object Input, Backend)) {
    $result = [ordered]@{ Input = $group.Group[0].Input; Backend = $group.Group[0].Backend; Runs = $group.Count }
    foreach ($metric in @('ImportMs', 'ReopenMs', 'PeakPrivateMiB', 'PeakWorkingMiB', 'ManagedAllocatedMiB', 'TileP95Ms', 'CpuMs')) {
        $result[$metric] = Median $group.Group.$metric
    }
    $result['ImportMinMs'] = ($group.Group.ImportMs | Measure-Object -Minimum).Minimum
    $result['ImportMaxMs'] = ($group.Group.ImportMs | Measure-Object -Maximum).Maximum
    [pscustomobject]$result
}
[pscustomobject]@{ Timestamp = (Get-Date -Format o); OutputRoot = $OutputRoot; Summary = @($summary); Runs = $rows } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json')
$summary | Format-Table Input, Backend, ImportMs, PeakPrivateMiB, PeakWorkingMiB, TileP95Ms
Write-Host "Reports: $OutputRoot"
