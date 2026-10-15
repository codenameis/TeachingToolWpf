param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ApplicationArguments
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$bundledNative = Join-Path (Split-Path $executablePath -Parent) 'runtimes/win-x64/native'
if (Test-Path -LiteralPath (Join-Path $bundledNative 'cudart64_12.dll')) {
    # TorchSharp 배포에 포함된 CUDA DLL을 ONNX와 함께 사용한다. Python 설치가 필요 없다.
    $directories = @($bundledNative)
}
else {
    $locations = @('artifacts/tdcm-cuda-runtime/nvidia', 'artifacts/tdcm-cuda-extra/nvidia')
    $directories = foreach ($location in $locations) {
        $path = Join-Path $repository $location
        if (-not (Test-Path -LiteralPath $path)) {
            throw "CUDA 런타임 경로 없음: $path. docs/posematch-tdcm-cuda.md를 확인하세요."
        }
        Get-ChildItem -LiteralPath $path -Filter *.dll -Recurse | ForEach-Object DirectoryName
    }
}
$previousPath = $env:PATH
try {
    # 이 자식 프로세스에만 적용한다. 시스템 PATH/드라이버를 변경하지 않는다.
    $env:PATH = (($directories | Sort-Object -Unique) -join ';') + ';' + $previousPath
    & $Executable @ApplicationArguments
    if ($LASTEXITCODE -ne 0) { throw "프로그램 종료 코드: $LASTEXITCODE" }
}
finally {
    $env:PATH = $previousPath
}
