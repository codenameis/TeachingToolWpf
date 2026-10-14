param([switch]$CompareSkia)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo ('artifacts/loader-comparison/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $output) { throw '새 비교 폴더가 필요합니다. 기존 결과를 덮어쓰지 않습니다.' }
if (-not (Test-Path -LiteralPath (Join-Path $repo 'artifacts/streaming-codecs/win-x64/runtime/VisionImageCodecs.dll'))) {
    throw '먼저 experiments/VisionImaging.Codecs/Build-Native.ps1을 실행하세요.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$results = [Collections.Generic.List[object]]::new()
$backends = if ($CompareSkia) { @('Streaming', 'Skia') } else { @('Streaming') }
foreach ($backend in $backends) {
    $destination = Join-Path $output $backend

    $skia = if ($backend -eq 'Skia') { 'true' } else { 'false' }
    $project = Join-Path $repo 'src/TeachingToolWpf/TeachingToolWpf.csproj'
    & dotnet publish $project -c Release -r win-x64 --self-contained true "-p:UseSkiaImageLoader=$skia" -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "$backend publish 실패: $LASTEXITCODE" }
    $deps = Get-Content -LiteralPath (Join-Path $destination 'TeachingToolWpf.deps.json') -Raw | ConvertFrom-Json
    $packages = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -match '^NetVips(\.|/)' })
    $vipsFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object { $_.Name -match '^(NetVips|libvips).*\.dll$' })
    $hasCodec = Test-Path -LiteralPath (Join-Path $destination 'VisionImageCodecs.dll')

    if ($packages.Count -ne 0 -or $vipsFiles.Count -ne 0 -or -not $hasCodec) { throw '자체 배포 의존성 경계 실패' }

    $hasSkia = Test-Path -LiteralPath (Join-Path $destination 'libSkiaSharp.dll')
    $skiaPackages = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -match '^SkiaSharp(\.|/)' })
    if ($hasSkia -ne ($backend -eq 'Skia') -or (($skiaPackages.Count -gt 0) -ne ($backend -eq 'Skia'))) { throw 'Skia 배포 의존성 경계 실패' }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'coreclr.dll'))) { throw '.NET 실행 runtime 누락' }
    $launcher = @'
@echo off
cd /d "%~dp0"
start "" "%~dp0TeachingToolWpf.exe" --cadence-cache "%~dp0cache" %*
'@
    $cpuLauncher = $launcher.Replace('--cadence-cache', '--software-rendering --cadence-cache')
    [IO.File]::WriteAllText((Join-Path $destination 'Run.cmd'), $launcher.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $destination 'Run-CPU.cmd'), $cpuLauncher.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
    $guide = if ($CompareSkia) { 'docs/skia-scanline-builds.md' } else { 'docs/netvips-removal.md' }
    $guideText = Get-Content -LiteralPath (Join-Path $repo $guide) -Raw
    if ($CompareSkia) { $guideText = $guideText.Replace('(skia-jpeg-scanline.md)', '(docs/skia-jpeg-scanline.md)') }
    Set-Content -LiteralPath (Join-Path $destination 'START-HERE.md') -Value $guideText
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $destination 'TeachingToolWpf.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $package = Join-Path $repo ('.packages/' + $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
        $noticeDestination = Join-Path $destination ('licenses/' + $framework.name)
        New-Item -ItemType Directory -Path $noticeDestination -Force | Out-Null
        $copiedLicense = $false
        foreach ($name in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $notice = Join-Path $package $name
            if (Test-Path -LiteralPath $notice) {
                Copy-Item -LiteralPath $notice -Destination $noticeDestination
                if ($name.StartsWith('LICENSE')) { $copiedLicense = $true }
            }
        }
        if (!$copiedLicense) { throw "Runtime license missing: $package" }
    }
    $result = [ordered]@{
        Backend = $backend
        Runtime = 'win-x64 / .NET 10 self-contained / Release'
        Folder = $destination
        NetVipsPackages = $packages
        NetVipsDlls = @($vipsFiles | ForEach-Object Name)
        StreamingCodec = $hasCodec
        SkiaPackages = $skiaPackages
        SkiaNative = $hasSkia
        AppSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'TeachingToolWpf.dll')).Hash
        CanvasSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'VisionCanvas.dll')).Hash
        AlgorithmSha256 = (Get-FileHash -LiteralPath (Join-Path $destination 'VisionAlgorithm.dll')).Hash
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'build-info.json')
    $results.Add($result)
    $archive = Join-Path $output "$backend-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($destination, $archive, [IO.Compression.CompressionLevel]::Fastest, $true)
    Write-Host "$backend ready: $destination"
}
if ($results.Count -gt 1 -and ($results[0].CanvasSha256 -ne $results[1].CanvasSha256 -or $results[0].AlgorithmSha256 -ne $results[1].AlgorithmSha256)) {
    throw '두 배포의 공통 Canvas/알고리즘 실행물이 다릅니다.'
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-results.json')
Write-Host "Comparison builds: $output"
