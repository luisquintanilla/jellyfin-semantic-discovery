#requires -version 7.0
[CmdletBinding()]
param(
    [string] $ImageTag = "jellyfin-semantic-fixture-generator:locked",
    [switch] $SkipGitCleanCheck
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dataRoot = Join-Path $repoRoot ".data"
$temporaryOutput = Join-Path $dataRoot "fixture-repro"
$staleOutput = Join-Path $dataRoot "fixture-stale"
$invalidUpdateRoot = Join-Path $dataRoot "fixture-invalid-update"
$unsafeOutput = Join-Path $dataRoot "fixture-unsafe"
$canonicalOutput = Join-Path $repoRoot "assets\fixtures\generated"

function Reset-TemporaryOutput {
    param([string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $safePrefix = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $safePrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a path outside the repository data directory."
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force $fullPath | Out-Null
}

function Invoke-ExpectFailure {
    param(
        [string] $Description,
        [scriptblock] $Action
    )

    & $Action
    if ($LASTEXITCODE -eq 0) {
        throw "$Description unexpectedly succeeded."
    }
}

function Get-FileHashes {
    param([string] $Root)

    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath $Root -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $path = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            $fileHash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            $hashes[$path] = $fileHash.Hash.ToLowerInvariant()
        }
    return $hashes
}

function Get-DockerHostUserArguments {
    if ($IsWindows) {
        return @()
    }

    $uid = (& id -u).Trim()
    if ($LASTEXITCODE -ne 0 -or $uid -notmatch '^\d+$') {
        throw "Could not determine the host user ID for fixture generation."
    }

    $gid = (& id -g).Trim()
    if ($LASTEXITCODE -ne 0 -or $gid -notmatch '^\d+$') {
        throw "Could not determine the host group ID for fixture generation."
    }

    return @("--user", "${uid}:${gid}", "--env", "HOME=/tmp")
}

Push-Location $repoRoot
try {
    $dockerHostUserArguments = @(Get-DockerHostUserArguments)
    $fixtureStatusBefore =
        @(git status --porcelain=v1 --untracked-files=all -- assets/fixtures)
    docker build `
        --tag $ImageTag `
        --file (Join-Path $repoRoot "containers\fixture-generator\Dockerfile") `
        $repoRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The locked fixture generator image failed to build."
    }

    Reset-TemporaryOutput $temporaryOutput
    docker run --rm `
        @dockerHostUserArguments `
        --mount "type=bind,source=$temporaryOutput,target=/output" `
        $ImageTag `
        -ManifestPath /opt/fixture-generator/manifest.json `
        -OutputRoot /output `
        -Mode Generate
    if ($LASTEXITCODE -ne 0) {
        throw "Clean fixture generation failed."
    }

    $canonicalHashes = Get-FileHashes $canonicalOutput
    $temporaryHashes = Get-FileHashes $temporaryOutput
    if ($canonicalHashes.Count -ne $temporaryHashes.Count) {
        throw "Canonical and clean-generated fixture path sets differ."
    }
    foreach ($path in $canonicalHashes.Keys) {
        if (-not $temporaryHashes.Contains($path) -or
            $canonicalHashes[$path] -ne $temporaryHashes[$path]) {
            throw "Clean-generated fixture differs from canonical path '$path'."
        }
    }

    Reset-TemporaryOutput $staleOutput
    Copy-Item (Join-Path $canonicalOutput "*") $staleOutput -Recurse -Force
    $stalePaths = @(
        "library/Movies/The Signal Garden (2024)/movie.nfo",
        "library/Movies/The Signal Garden (2024)/The Signal Garden (2024).en.srt",
        "library/Books/Field Notes on Static (2024)/metadata.opf"
    )
    foreach ($path in $stalePaths) {
        $target = Join-Path $staleOutput $path
        [System.IO.File]::AppendAllText(
            $target,
            "STALE_BYTES_MUST_NOT_BE_BLESSED",
            [System.Text.UTF8Encoding]::new($false))
    }
    $staleBefore = Get-FileHashes $staleOutput
    Invoke-ExpectFailure "Read-only stale fixture verification" {
        pwsh -NoProfile -File (Join-Path $PSScriptRoot "Generate-Fixtures.ps1") `
            -Mode Verify `
            -ManifestPath (Join-Path $repoRoot "assets\fixtures\manifest.json") `
            -OutputRoot $staleOutput
    }
    $staleAfter = Get-FileHashes $staleOutput
    if ($staleBefore.Count -ne $staleAfter.Count) {
        throw "Read-only verification changed the stale fixture path count."
    }
    foreach ($path in $staleBefore.Keys) {
        if (-not $staleAfter.Contains($path) -or
            $staleBefore[$path] -ne $staleAfter[$path]) {
            throw "Read-only verification rewrote stale path '$path'."
        }
    }
    foreach ($path in $staleAfter.Keys) {
        if (-not $staleBefore.Contains($path)) {
            throw "Read-only verification created stale path '$path'."
        }
    }

    Reset-TemporaryOutput $unsafeOutput
    $unsafeSentinel = Join-Path $unsafeOutput "must-survive.txt"
    [System.IO.File]::WriteAllText(
        $unsafeSentinel,
        "UNOWNED",
        [System.Text.UTF8Encoding]::new($false))
    Invoke-ExpectFailure "Generate into an unowned populated directory" {
        docker run --rm `
            @dockerHostUserArguments `
            --mount "type=bind,source=$unsafeOutput,target=/output" `
            $ImageTag `
            -ManifestPath /opt/fixture-generator/manifest.json `
            -OutputRoot /output `
            -Mode Generate
    }
    if ((Get-Content -Raw -LiteralPath $unsafeSentinel) -ne "UNOWNED") {
        throw "Unsafe generation changed an unowned destination."
    }

    Reset-TemporaryOutput $invalidUpdateRoot
    Copy-Item (Join-Path $repoRoot "assets\fixtures\manifest.json") `
        (Join-Path $invalidUpdateRoot "manifest.json")
    Copy-Item (Join-Path $repoRoot "assets\fixtures\SHA256SUMS") `
        (Join-Path $invalidUpdateRoot "SHA256SUMS")
    $invalidManifestPath = Join-Path $invalidUpdateRoot "manifest.json"
    $invalidManifest = Get-Content -Raw -LiteralPath $invalidManifestPath |
        ConvertFrom-Json
    ($invalidManifest.items | Where-Object kind -eq "pdf").expected.pageCount = 999
    $invalidJson = ($invalidManifest | ConvertTo-Json -Depth 30).Replace("`r`n", "`n") + "`n"
    [System.IO.File]::WriteAllText(
        $invalidManifestPath,
        $invalidJson,
        [System.Text.UTF8Encoding]::new($false))
    $invalidChecksumPath = Join-Path $invalidUpdateRoot "SHA256SUMS"
    $checksumBefore = (Get-FileHash $invalidChecksumPath -Algorithm SHA256).Hash
    Invoke-ExpectFailure "Invalid checksum update" {
        docker run --rm `
            @dockerHostUserArguments `
            --mount "type=bind,source=$invalidUpdateRoot,target=/source" `
            $ImageTag `
            -ManifestPath /source/manifest.json `
            -OutputRoot /source/generated `
            -Mode UpdateChecksums
    }
    $checksumAfter = (Get-FileHash $invalidChecksumPath -Algorithm SHA256).Hash
    if ($checksumBefore -ne $checksumAfter) {
        throw "A failed canonical update changed the checksum file."
    }

    if (-not $SkipGitCleanCheck) {
        $fixtureStatusAfter =
            @(git status --porcelain=v1 --untracked-files=all -- assets/fixtures)
        if (($fixtureStatusBefore -join "`n") -ne ($fixtureStatusAfter -join "`n")) {
            throw "Fixture verification changed the tracked or untracked fixture tree."
        }
    }

    $imageId = docker image inspect $ImageTag --format "{{.Id}}"
    Write-Host "Canonical fixtures match clean locked generation ($imageId)."
} finally {
    foreach ($path in @(
            $temporaryOutput,
            $staleOutput,
            $invalidUpdateRoot,
            $unsafeOutput)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    Pop-Location
}
