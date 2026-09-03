[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [double]$MinimumLineCoverage = 90,

    [double]$MinimumBranchCoverage = 80
)

$report = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'coverage.cobertura.xml' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $report) {
    throw "No Cobertura coverage report was found under '$ResultsDirectory'."
}

[xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
$lineCoverage = [double]::Parse(
    $coverage.coverage.'line-rate',
    [Globalization.CultureInfo]::InvariantCulture) * 100
$branchCoverage = [double]::Parse(
    $coverage.coverage.'branch-rate',
    [Globalization.CultureInfo]::InvariantCulture) * 100

Write-Output ('Core coverage: {0:F1}% lines, {1:F1}% branches.' -f $lineCoverage, $branchCoverage)
Write-Output ('Required minimum: {0:F1}% lines, {1:F1}% branches.' -f $MinimumLineCoverage, $MinimumBranchCoverage)

if ($lineCoverage -lt $MinimumLineCoverage -or $branchCoverage -lt $MinimumBranchCoverage) {
    throw ('Core coverage is below the required minimum. Actual: {0:F1}% lines and {1:F1}% branches.' -f
        $lineCoverage, $branchCoverage)
}
