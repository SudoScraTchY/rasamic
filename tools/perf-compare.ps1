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
    @{ Benchmarks = $benches } | ConvertTo-Json -Depth 12 | Set-Content $MergeOut -Encoding UTF8
    Write-Host "merged $($benches.Count) benchmarks -> $MergeOut"
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
    $ratio = $c.Statistics.Mean / $m
    if ($ratio -gt $Threshold) {
        Write-Host ("REGRESSION {0}: {1:N2}x baseline" -f $c.FullName, $ratio)
        $fail = 1
    } else {
        Write-Host ("ok   {0} {1:N2}x" -f $c.FullName, $ratio)
    }
}
exit $fail