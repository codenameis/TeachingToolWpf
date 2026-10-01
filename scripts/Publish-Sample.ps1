$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet publish TeachingToolWpf.csproj -c Release --no-restore -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Run dotnet restore before publishing.' }
    # Keep existing measurement evidence browsable from the bundled documentation.
    $evidenceRoot = 'artifacts/publish/artifacts'
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
    foreach ($name in @('core-baseline.json','core-optimized.json','large-image-results.json','smoke.log','app-smoke.png','viewport-smoke.png','live-smoke.png','filter-smoke.png')) {
        $sourceFile = Join-Path 'artifacts' $name
        if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination $evidenceRoot }
    }
    Write-Output (Join-Path $projectRoot 'artifacts/publish/TeachingToolWpf.exe')
}
finally { Pop-Location }
