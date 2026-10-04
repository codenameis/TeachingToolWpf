param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Gpu
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet test PortableInspection.slnx -c $Configuration --logger 'trx;LogFileName=portable-inspection.trx'
    if ($LASTEXITCODE -ne 0) { throw '공통 CPU 검사 회귀 실패' }
    if ($Gpu) {
        dotnet test tests/VisionCompute.Tests/VisionCompute.Tests.csproj -c $Configuration --logger 'trx;LogFileName=portable-cuda.trx'
        if ($LASTEXITCODE -ne 0) { throw '실제 CUDA 검사 회귀 실패' }
    }
}
finally {
    Pop-Location
}
