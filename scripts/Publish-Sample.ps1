param(
    [switch]$InspectionLatency,
    [string]$OutputDirectory = 'artifacts/publish'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $publishArguments = @('publish', 'src/TeachingToolWpf/TeachingToolWpf.csproj', '-c', 'Release', '--no-restore', '-o', $OutputDirectory)
    if ($InspectionLatency) {
        # 기준 장비용 사전 컴파일. 라이브러리 일반 빌드와 시스템 환경변수는 바꾸지 않는다.
        dotnet restore src/TeachingToolWpf/TeachingToolWpf.csproj -r win-x64 -p:PublishReadyToRun=true
        if ($LASTEXITCODE -ne 0) { throw 'ReadyToRun package restore failed.' }
        $publishArguments += @('-r', 'win-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true',
            '-p:PublishReadyToRunCrossgen2ExtraArgs=--instruction-set=avx2', '-p:TieredCompilation=false')
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Run dotnet restore before publishing.' }
    # Keep existing measurement evidence browsable from the bundled documentation.
    $evidenceRoot = Join-Path $OutputDirectory 'artifacts'
    New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
    foreach ($folder in @('benchmarks-final', 'benchmarks')) {
        $sourceFolder = Join-Path 'artifacts' $folder
        if (Test-Path -LiteralPath $sourceFolder) {
            $destination = Join-Path $evidenceRoot $folder
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Get-ChildItem -LiteralPath $sourceFolder -File | Where-Object Extension -in '.json','.csv' |
                Copy-Item -Destination $destination
        }
    }
    foreach ($name in @('core-baseline.json','core-optimized.json','large-image-results.json','memory-before.json','memory-after.json','memory-long.json','smoke.log','app-smoke.png','viewport-smoke.png','live-smoke.png','filter-smoke.png','minimap-8k.png','minimap-24k.png','minimap-live.png','shapes-app.png','shapes-outline.png','shapes-filter.png')) {
        $sourceFile = Join-Path 'artifacts' $name
        if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination $evidenceRoot }
    }
    Write-Output (Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory, $projectRoot)) 'TeachingToolWpf.exe')
}
finally { Pop-Location }
