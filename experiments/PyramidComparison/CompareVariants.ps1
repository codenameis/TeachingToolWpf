param(
    [Parameter(Mandatory)][string]$BeforeAssembly,
    [Parameter(Mandatory)][string]$AfterAssembly,
    [int]$Iterations = 5
)
$ErrorActionPreference = 'Stop'
if ($Iterations -lt 1) { throw 'Iterations must be positive.' }
$before = (Resolve-Path -LiteralPath $BeforeAssembly).Path
$after = (Resolve-Path -LiteralPath $AfterAssembly).Path
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $repo ('artifacts/pyramid-memory/paired-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$reports = [Collections.Generic.List[object]]::new()
Write-Host "Reports: $output"
foreach ($name in @('8k-color.jpg', '24k-gray16.tif')) {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $order = if ($iteration % 2 -eq 1) { @('before', 'after') } else { @('after', 'before') }
        foreach ($variant in $order) {
            $assembly = if ($variant -eq 'before') { $before } else { $after }
            $tag = "$name-$iteration-$variant"
            & dotnet $assembly (Join-Path $repo "artifacts/pyramid-comparison/inputs/$name") (Join-Path $output "$tag-cache") (Join-Path $output "$tag.json")
            if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $tag" }
            $result = Get-Content -LiteralPath (Join-Path $output "$tag.json") -Raw | ConvertFrom-Json
            $result | Add-Member NoteProperty Variant $variant
            $result | Add-Member NoteProperty Iteration $iteration
            $reports.Add($result)
            Write-Host "$tag import=$($result.ImportMilliseconds) ms"
        }
    }
}
$reports | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath (Join-Path $output 'reports.json')
