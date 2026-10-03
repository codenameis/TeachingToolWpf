param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$styleRules = @('IDE0003', 'IDE0011', 'IDE0047', 'IDE0048', 'IDE0161', 'IDE0065')

function Invoke-CodeFormat {
    param([string[]]$FormatArguments)

    & dotnet format @FormatArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'C# 컨벤션 검사에 실패했습니다. 표시된 위치를 수정하세요. IDE1006은 심볼 Rename으로 참조까지 변경해야 합니다.'
    }
}

Push-Location $projectRoot
try {
    if (-not $Verify) {
        # 중첩 수식은 첫 수정 이후 안쪽 규칙이 드러날 수 있어 한 번 더 적용한다.
        for ($pass = 0; $pass -lt 2; $pass++) {
            Invoke-CodeFormat -FormatArguments (@('style', 'TeachingToolWpf.slnx', '--no-restore', '--diagnostics') + $styleRules + @('--severity', 'warn', '--verbosity', 'minimal'))
        }
        Invoke-CodeFormat -FormatArguments @('whitespace', 'TeachingToolWpf.slnx', '--no-restore', '--verbosity', 'minimal')
    }

    Invoke-CodeFormat -FormatArguments @('whitespace', 'TeachingToolWpf.slnx', '--no-restore', '--verify-no-changes', '--verbosity', 'minimal')
    Invoke-CodeFormat -FormatArguments (@('style', 'TeachingToolWpf.slnx', '--no-restore', '--verify-no-changes', '--diagnostics', 'IDE1006') + $styleRules + @('--severity', 'warn', '--verbosity', 'minimal'))
    Write-Output 'C# 컨벤션 자동 검사 통과'
}
finally {
    Pop-Location
}
