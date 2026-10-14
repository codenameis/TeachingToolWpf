param(
    [int]$Iterations = 3,
    [string]$LargeInputDirectory
)
$ErrorActionPreference = 'Stop'
if ($Iterations -lt 1) { throw 'Iterations must be positive.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('artifacts/pyramid-comparison/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$inputs = Join-Path $repo 'artifacts/pyramid-comparison/inputs'
$dotnet = (Get-Command dotnet).Source
$baseline = Join-Path $PSScriptRoot 'Baseline/bin/Release/net10.0/Baseline.dll'
$own = Join-Path $PSScriptRoot 'Own/bin/Release/net10.0/Own.dll'
New-Item -ItemType Directory -Path $output | Out-Null
Write-Host "Reports: $output"

# 빌드/fixture 생성은 측정 전에 수행한다. 이 스크립트는 캐시를 지우지 않는다.
& $dotnet build (Join-Path $repo 'experiments/PyramidComparison.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet $baseline fixtures $inputs
if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed.' }

$reports = [Collections.Generic.List[object]]::new()
function Measure-One([string]$backend, [string]$image, [string]$tag) {
    $cache = Join-Path $output "$tag-cache"
    $report = Join-Path $output "$tag.json"
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $assembly = if ($backend -eq 'NativeReference') { $baseline } else { $own }
    foreach ($argument in @($assembly, $image, $cache, $report)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            $process.WaitForExit()
            @{ Backend = $backend; Image = $image; Status = 'TIMEOUT'; Error = '180 second limit' } |
                ConvertTo-Json | Set-Content -LiteralPath $report
        }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "$tag.stdout.txt")
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output "$tag.stderr.txt")
        if (-not (Test-Path -LiteralPath $report)) {
            @{ Backend = $backend; Image = $image; Status = 'FAIL'; Error = "Process exit $($process.ExitCode), no report" } |
                ConvertTo-Json | Set-Content -LiteralPath $report
        }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $reports.Add($result)
        Write-Host "$tag $($result.Status) import=$($result.ImportMilliseconds) ms"
    }
    finally { $process.Dispose() }
}

foreach ($name in @('8k-gray8.png', '8k-color.jpg', '24k-gray8.tif', '24k-gray16.tif')) {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        # OS 파일 캐시는 강제로 비우지 않는다. 구현 실행 순서만 번갈아 바꾼다.
        $order = if ($iteration % 2 -eq 1) { @('NativeReference', 'Own') } else { @('Own', 'NativeReference') }
        foreach ($backend in $order) { Measure-One $backend (Join-Path $inputs $name) "$name-$iteration-$backend" }
    }
}
if ($LargeInputDirectory) {
    foreach ($name in @('24k-8bit.tif', '24k-16bit.tif')) {
        foreach ($backend in @('NativeReference', 'Own')) {
            Measure-One $backend ([IO.Path]::GetFullPath((Join-Path $LargeInputDirectory $name))) "$name-large-$backend"
        }
    }
}
$reports | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'reports.json')
Write-Host "Complete: $output"
