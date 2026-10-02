param(
    [string[]]$Profiles = @('artifacts/render-before-*.profile.csv', 'artifacts/render-final-*.profile.csv'),
    [string]$Output = 'docs/data/retained-roi-render.csv'
)
$ErrorActionPreference = 'Stop'
function Quantile([double[]]$Values, [double]$Fraction) {
    if (!$Values.Count) { return $null }
    $sorted = @($Values | Sort-Object)
    [math]::Round($sorted[[math]::Ceiling($sorted.Count * $Fraction) - 1], 4)
}
$results = foreach ($file in Get-ChildItem -Path $Profiles -File | Sort-Object Name) {
    $rows = @(Import-Csv -LiteralPath $file.FullName | Where-Object { [double]$_.seconds -gt 2 -and [int]$_.rois -gt 0 })
    $framePath = $file.FullName.Replace('.profile.csv', '.frames.csv')
    $frames = if (Test-Path -LiteralPath $framePath) { @(Import-Csv -LiteralPath $framePath) } else { @() }
    foreach ($group in $rows | Group-Object rois,selected) {
        $samples = @($group.Group)
        $times = @($samples | ForEach-Object { [double]$_.seconds })
        $first = ($times | Measure-Object -Minimum).Minimum
        $last = ($times | Measure-Object -Maximum).Maximum
        $timed = @($frames | Where-Object { [double]$_.seconds -ge $first -and [double]$_.seconds -le $last })
        [pscustomobject]@{
            File = $file.Name
            Count = [int]$samples[0].rois
            Selected = [int]$samples[0].selected
            Samples = $samples.Count
            PaintP50 = Quantile @($samples | ForEach-Object { [double]$_.paint_ms }) .50
            PaintP95 = Quantile @($samples | ForEach-Object { [double]$_.paint_ms }) .95
            PaintP99 = Quantile @($samples | ForEach-Object { [double]$_.paint_ms }) .99
            SubmitP95 = Quantile @($samples | ForEach-Object { [double]$_.submit_ms }) .95
            AllocatedKiB = [math]::Round(($samples | Measure-Object allocated_bytes -Average).Average / 1KB, 2)
            UploadMiB = if ($samples[0].PSObject.Properties.Name -contains 'roi_upload_bytes') { [math]::Round(($samples | Measure-Object roi_upload_bytes -Sum).Sum / 1MB, 4) } else { $null }
            InstanceMax = ($samples | Measure-Object roi_instances -Maximum).Maximum
            CacheMiB = if ($samples[0].PSObject.Properties.Name -contains 'roi_cache_bytes') { [math]::Round(($samples | Measure-Object roi_cache_bytes -Maximum).Maximum / 1MB, 2) } else { $null }
            FrameCpuP95 = Quantile @($timed | ForEach-Object { [double]$_.cpu_ms }) .95
            FrameGpuP95 = Quantile @($timed | Where-Object { $_.gpu_ms -ne '' } | ForEach-Object { [double]$_.gpu_ms }) .95
            IntervalP95 = Quantile @($timed | ForEach-Object { [double]$_.interval_ms }) .95
            PrivatePeakMiB = [math]::Round(($timed | Measure-Object private_bytes -Maximum).Maximum / 1MB, 2)
        }
    }
}
$directory = Split-Path ([IO.Path]::GetFullPath($Output)) -Parent
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$results | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding utf8
$results | Format-Table File,Selected,PaintP50,PaintP95,SubmitP95,AllocatedKiB,FrameGpuP95
