param(
    [string]$ResultsDir = "perf-results",
    [string]$CpuOverride
)

# Per-runner regression gate: hardware variance must not confound the >10% gate.
# Committed baselines live in docs/perf-baselines/<cpu-slug>.json; a runner without
# one uploads a candidate baseline (advisory) until a human promotes it.
$cpu = if ($CpuOverride) { $CpuOverride } else { (Get-CimInstance Win32_Processor).Name }
$slug = (($cpu -replace '[^a-zA-Z0-9]+', '-').Trim('-')).ToLower()
Write-Host "runner cpu: $cpu ($slug)"

$compare = Join-Path $PSScriptRoot "perf-compare.ps1"
$base = "docs/perf-baselines/$slug.json"
$ref = (Get-Content "docs/perf-reference-slug.txt" -Raw).Trim()

if (Test-Path $base) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $compare -Baseline $base -Current $ResultsDir
    exit $LASTEXITCODE
}
if ($slug -eq $ref) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $compare -Baseline "docs/perf-baseline.json" -Current $ResultsDir
    exit $LASTEXITCODE
}
& powershell -NoProfile -ExecutionPolicy Bypass -File $compare -MergeDir $ResultsDir -MergeOut "candidate-baseline.json"
Write-Host "no committed baseline for $slug; candidate-baseline.json uploaded (advisory until promoted to docs/perf-baselines/)"
exit 0