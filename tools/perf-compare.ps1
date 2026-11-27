param(
    [string]$Baseline,
    [string]$Current,
    [string]$MergeDir,
    [string]$MergeOut,
    [double]$Threshold = 1.10
)

function Read-Benches([string]$path) {
    $files = if (Test-Path $path -PathType Container) {
        Get-ChildItem -Path $path -Recurse -Filter '*-report-full-compressed.json'
    } else {
        Get-Item $path
    }
    $all = @()
    foreach ($f in $files) {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
        $all += $j.Benchmarks
    }
    return $all
}

if ($MergeDir) {
    $benches = Read-Benches $MergeDir
    $compact = @($benches | ForEach-Object {
        [pscustomobject]@{
            FullName = $_.FullName
            Statistics = [pscustomobject]@{ Mean = $_.Statistics.Mean }
        }
    })
    @{ Benchmarks = $compact } | ConvertTo-Json -Depth 4 | Set-Content $MergeOut -Encoding UTF8
    Write-Host "merged $($compact.Count) benchmarks -> $MergeOut"
    exit 0
}

$base = Read-Benches $Baseline
$cur = Read-Benches $Current
$map = @{}
foreach ($b in $base) { $map[$b.FullName] = $b.Statistics.Mean }
$fail = 0
foreach ($c in $cur) {
    $m = $map[$c.FullName]
    if ($null -eq $m) {
        Write-Host "new  $($c.FullName)"
        continue
    }
    $mean = $c.Statistics.Mean
    $stdErr = if ($null -eq $c.Statistics.StandardError) { 0 } else { $c.Statistics.StandardError }
    $pointRatio = $mean / $m
    # Nanosecond benchmarks need uncertainty-aware gating; a noisy point mean is not a regression.
    $lowerRatio = [Math]::Max(0, $mean - 2 * $stdErr) / $m
    if ($lowerRatio -gt $Threshold) {
        Write-Host ("REGRESSION {0}: {1:N2}x baseline (lower confidence; point {2:N2}x)" -f $c.FullName, $lowerRatio, $pointRatio)
        $fail = 1
    } else {
        Write-Host ("ok   {0} point {1:N2}x, lower confidence {2:N2}x" -f $c.FullName, $pointRatio, $lowerRatio)
    }
}
exit $fail