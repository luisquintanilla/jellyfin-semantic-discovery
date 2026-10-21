#requires -version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($path in Get-ChildItem -LiteralPath (Join-Path $repoRoot "scripts") -Filter *.ps1) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $path.FullName,
        [ref]$tokens,
        [ref]$errors)
    foreach ($error in $errors) {
        $failures.Add(
            "$($path.Name):$($error.Extent.StartLineNumber): $($error.Message)")
    }
}

if ($failures.Count -gt 0) {
    throw "PowerShell syntax validation failed:`n$($failures -join "`n")"
}

Write-Host "All repository PowerShell scripts parsed successfully."
