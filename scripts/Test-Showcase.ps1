#requires -version 7.0
[CmdletBinding()]
param(
    [switch] $AcceptLicenses,
    [switch] $ReuseValidatedRuntime,
    [switch] $ColdCache,
    [switch] $FreshRuntime,
    [switch] $PublishEvidence,
    [int] $TimeoutSeconds = 2400
)

$ErrorActionPreference = "Stop"
if (-not $AcceptLicenses) {
    throw "The showcase downloads 432,489,421 source bytes from archive.org/.archive.org, durian.blender.org, upload.wikimedia.org, pressbooks.pub, link.springer.com/idp.springer.com, and www.gutenberg.org. Allow up to 282,690,045 temporary atomic-download bytes, 67,108,864 snapshot bytes, and the pinned 90,636,722 model/vocabulary bytes plus Jellyfin runtime state. Re-run with -AcceptLicenses after reviewing assets/showcase/sources.lock.json."
}
if ($PublishEvidence -and -not $ColdCache) {
    throw "-PublishEvidence requires -ColdCache so publication evidence cannot be produced from a reused source cache."
}
if ($ColdCache -and $FreshRuntime) {
    throw "-ColdCache and -FreshRuntime are mutually exclusive."
}

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appHost = Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj"
$testProject = Join-Path $repoRoot "tests\Jellyfin.SemanticDiscovery.EndToEndTests\Jellyfin.SemanticDiscovery.EndToEndTests.csproj"
$dataLeaf = if ($ColdCache) {
    ".data\showcase-publication-runtime"
} elseif ($FreshRuntime) {
    ".data\showcase-measurement-runtime"
} else {
    ".data\showcase-runtime"
}
$showcaseLeaf = if ($ColdCache) {
    ".data\showcase-publication-cache"
} else {
    ".data\showcase-cache"
}
$evidenceLeaf = if ($ColdCache) {
    "artifacts\showcase-publication"
} elseif ($FreshRuntime) {
    "artifacts\showcase-measurement"
} else {
    "artifacts\showcase"
}
$dataRoot = Join-Path $repoRoot $dataLeaf
$showcaseRoot = Join-Path $repoRoot $showcaseLeaf
$credentialsPath = Join-Path $dataRoot "jellyfin-login.json"
$sourceLock = Join-Path $repoRoot "assets\showcase\sources.lock.json"
$snapshotPath = Join-Path $dataRoot "jellyfin\config\data\semantic-discovery\index.json"
$evidenceRoot = Join-Path $repoRoot $evidenceLeaf
$runtimeMarker = Join-Path $dataRoot ".showcase-runtime-owner.json"
$expectedRecommendationFloor = 0.38

function Set-ContainerUserForCurrentHost {
    if ($IsWindows) {
        return
    }

    $uid = (& id -u).Trim()
    if ($LASTEXITCODE -ne 0 -or $uid -notmatch '^\d+$') {
        throw "Could not determine the host user ID for showcase containers."
    }
    $gid = (& id -g).Trim()
    if ($LASTEXITCODE -ne 0 -or $gid -notmatch '^\d+$') {
        throw "Could not determine the host group ID for showcase containers."
    }

    $env:SEMANTIC_DISCOVERY_CONTAINER_USER = "${uid}:${gid}"
}

function Invoke-Aspire {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    $output = & dotnet tool run aspire -- @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Aspire command failed: $($Arguments -join ' ')`n$output"
    }
}

function Stop-Showcase {
    & dotnet tool run aspire -- stop `
        --apphost $appHost `
        --force `
        --non-interactive `
        --nologo 2>$null | Out-Null
    $description = & dotnet tool run aspire -- describe `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo 2>&1 | Out-String
    if ($description -notmatch "No AppHost is currently running") {
        throw "The showcase AppHost did not stop; persistent data was retained."
    }
}

function Remove-OwnedRuntime {
    if (-not (Test-Path -LiteralPath $dataRoot)) {
        return
    }
    dotnet run `
        --project src\Jellyfin.SemanticDiscovery.Setup `
        --no-build `
        --no-restore `
        -- showcase-runtime-purge `
        --output $dataRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Owned showcase runtime purge failed."
    }
}

function Initialize-OwnedRuntime {
    dotnet run `
        --project src\Jellyfin.SemanticDiscovery.Setup `
        --no-build `
        --no-restore `
        -- showcase-runtime-ensure `
        --output $dataRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Showcase runtime ownership validation failed."
    }
}

function Remove-OwnedCache {
    if (-not (Test-Path -LiteralPath $showcaseRoot)) {
        return
    }
    dotnet run `
        --project src\Jellyfin.SemanticDiscovery.Setup `
        --no-build `
        --no-restore `
        -- showcase-purge `
        --output $showcaseRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Owned showcase cache purge failed."
    }
}

function Get-ResourceHttpEndpoint {
    param([string] $ResourceName)

    $description = & dotnet tool run aspire -- describe $ResourceName `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
        throw "Could not describe Aspire resource '$ResourceName'."
    }
    $resource = @($description.resources)[0]
    $endpoint = @($resource.urls | Where-Object {
        $_.name -eq "http"
    })
    if ($endpoint.Count -ne 1) {
        throw "Aspire resource '$ResourceName' has no unique HTTP endpoint."
    }
    return [Uri]$endpoint[0].url
}

function Test-MarkItDownEgress {
    $endpoint = Get-ResourceHttpEndpoint "markitdown"
    $canary = Invoke-RestMethod `
        -Uri ([Uri]::new($endpoint, "/egress-canary")) `
        -TimeoutSec 10
    $expected = @(
        "getaddrinfo",
        "create_connection",
        "socket_connect",
        "socket_connect_ex",
        "socket_sendto")
    if ($canary.blocked -ne $true) {
        throw "MarkItDown egress canary was not blocked."
    }
    foreach ($probe in $expected) {
        if ($canary.probes.$probe -ne $true) {
            throw "MarkItDown egress primitive '$probe' was not blocked."
        }
    }
    return $canary
}

function Restart-Showcase {
    if (Test-Path -LiteralPath $credentialsPath) {
        Remove-Item -LiteralPath $credentialsPath -Force
    }
    Invoke-Aspire resource showcase-setup start `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire wait showcase-setup `
        --status down `
        --timeout $TimeoutSeconds `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire resource markitdown restart `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire wait markitdown `
        --status healthy `
        --timeout $TimeoutSeconds `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire resource jellyfin restart `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire wait jellyfin `
        --status healthy `
        --timeout $TimeoutSeconds `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire resource jellyfin-bootstrap start `
        --apphost $appHost `
        --non-interactive `
        --nologo
    Invoke-Aspire wait jellyfin-bootstrap `
        --status down `
        --timeout $TimeoutSeconds `
        --apphost $appHost `
        --non-interactive `
        --nologo
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $credentialsPath) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    if (-not (Test-Path -LiteralPath $credentialsPath)) {
        throw "Restart bootstrap did not publish credentials."
    }
}

function Start-Showcase {
    if (Test-Path -LiteralPath $credentialsPath) {
        Remove-Item -LiteralPath $credentialsPath -Force
    }
    Invoke-Aspire start `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo
    foreach ($resource in @("jellyfin", "markitdown")) {
        Invoke-Aspire wait $resource `
            --status healthy `
            --timeout $TimeoutSeconds `
            --apphost $appHost `
            --non-interactive `
            --nologo
    }
    foreach ($resource in @(
            "model-setup",
            "plugin-setup",
            "showcase-setup",
            "jellyfin-bootstrap")) {
        Invoke-Aspire wait $resource `
            --status down `
            --timeout $TimeoutSeconds `
            --apphost $appHost `
            --non-interactive `
            --nologo
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $credentialsPath) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        $bootstrap = dotnet tool run aspire -- describe jellyfin-bootstrap `
            --apphost $appHost `
            --format Json `
            --non-interactive `
            --nologo | ConvertFrom-Json
        $resource = @($bootstrap.resources)[0]
        if ($resource.state -eq "Finished" -and
            [int]$resource.exitCode -ne 0) {
            throw "Showcase bootstrap failed with exit code $($resource.exitCode)."
        }
        Start-Sleep -Seconds 5
    }
    if (-not (Test-Path -LiteralPath $credentialsPath)) {
        throw "Showcase bootstrap did not finish before the timeout."
    }
}

function Invoke-ShowcaseTests {
    param([string] $Phase)

    $env:JELLYFIN_E2E_CREDENTIALS_PATH = $credentialsPath
    $env:JELLYFIN_E2E_PROFILE = "showcase"
    $env:SHOWCASE_E2E_EXPECTED_RECOMMENDATION_FLOOR =
        $expectedRecommendationFloor.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
    $env:SHOWCASE_E2E_REQUIRE_CLIENT_MEMORY =
        if ($ColdCache -or $FreshRuntime) { "true" } else { "false" }
    $env:SHOWCASE_E2E_RUNTIME_EVIDENCE =
        Join-Path $evidenceRoot "runtime-$Phase.json"
    $env:SHOWCASE_E2E_NATIVE_SEARCH_EVIDENCE =
        Join-Path $evidenceRoot "native-search-$Phase.json"
    $env:SHOWCASE_E2E_NATIVE_SIMILAR_EVIDENCE =
        Join-Path $evidenceRoot "native-similar-$Phase.json"
    dotnet test $testProject `
        --no-build `
        --no-restore `
        --filter "FullyQualifiedName~ShowcaseEndToEndTests" `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Showcase Jellyfin API tests failed."
    }
}

function Write-AcquisitionEvidence {
    param(
        [string] $Phase,
        [string] $OutputName
    )

    dotnet run `
        --project src\Jellyfin.SemanticDiscovery.Setup `
        --no-build `
        --no-restore `
        -- showcase-acquisition-evidence `
        --source-lock $sourceLock `
        --actual (Join-Path $showcaseRoot "showcase.actual.json") `
        --phase $Phase `
        --output (Join-Path $evidenceRoot $OutputName)
    if ($LASTEXITCODE -ne 0) {
        throw "Showcase $Phase acquisition evidence failed."
    }
}

function Write-RetrievalEvidence {
    param([string] $Name)

    dotnet run `
        --project src\Jellyfin.SemanticDiscovery.Setup `
        --no-build `
        --no-restore `
        -- showcase-evidence `
        --source-lock $sourceLock `
        --snapshot $snapshotPath `
        --showcase-root $showcaseRoot `
        --model-dir (Join-Path $dataRoot "models\all-MiniLM-L6-v2") `
        --output (Join-Path $evidenceRoot $Name) `
        --recommendation-floor $expectedRecommendationFloor
    if ($LASTEXITCODE -ne 0) {
        throw "Showcase score/anchor evidence failed."
    }
    if ((Get-Item -LiteralPath $snapshotPath).Length -gt 64 * 1024 * 1024) {
        throw "Showcase semantic snapshot exceeded the 64 MiB evidence bound."
    }
}

function Copy-CanonicalEvidence {
    param(
        [string] $Source,
        [string] $Destination
    )

    $text = [IO.File]::ReadAllText($Source).Replace("`r`n", "`n")
    [IO.File]::WriteAllText(
        $Destination,
        $text,
        [Text.UTF8Encoding]::new($false))
}

Push-Location $repoRoot
$primaryFailure = $null
try {
    Set-ContainerUserForCurrentHost
    dotnet tool restore --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw "Pinned Aspire CLI restore failed." }
    dotnet restore Jellyfin.SemanticDiscovery.slnx `
        --configfile NuGet.Config `
        --locked-mode `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed." }
    dotnet build Jellyfin.SemanticDiscovery.slnx --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Showcase build failed." }
    Stop-Showcase
    if ($ColdCache) {
        Remove-OwnedCache
        Remove-OwnedRuntime
    } elseif ($FreshRuntime -or -not $ReuseValidatedRuntime) {
        Remove-OwnedRuntime
    }
    Initialize-OwnedRuntime
    if (Test-Path -LiteralPath $evidenceRoot) {
        Remove-Item -LiteralPath $evidenceRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force $evidenceRoot | Out-Null
    $env:SEMANTIC_DISCOVERY_DATA = $dataRoot
    $env:SEMANTIC_DISCOVERY_PROFILE = "showcase"
    $env:SEMANTIC_DISCOVERY_SHOWCASE_ROOT = $showcaseRoot

    Start-Showcase
    $initialMarkItDownEgress = Test-MarkItDownEgress
    $initialAcquisitionPhase = if ($ColdCache) {
        "cold-initial"
    } elseif ($ReuseValidatedRuntime) {
        "validated-initial"
    } else {
        "ordinary-initial"
    }
    Write-AcquisitionEvidence $initialAcquisitionPhase "acquisition-initial.json"
    Invoke-ShowcaseTests "initial"
    $jellyfinLogs = & dotnet tool run aspire -- logs jellyfin `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo | Out-String
    $providerLogMatches = @(
        $jellyfinLogs -split "`r?`n" |
            Where-Object {
                $_ -match
                    "TheMovieDb|TMDb|OMDb|Google Books|ComicVine|Open Subtitles|Downloading (image|subtitle|metadata)" -and
                $_ -notmatch
                    "PluginManager: Loaded plugin:"
            } |
            Select-Object -First 20)
    if ($providerLogMatches.Count -ne 0) {
        throw "Jellyfin logs indicate an unapproved metadata or image provider ran:`n$($providerLogMatches -join "`n")"
    }
    Write-RetrievalEvidence "retrieval-initial.json"
    $conversionLogs = & dotnet tool run aspire -- logs markitdown `
        --apphost $appHost `
        --search "conversion completed" `
        --format Json `
        --non-interactive `
        --nologo | Out-String
    $conversionPattern =
        "media_type=(?<media>\S+) decoded_size=(?<size>\d+) " +
        "sha256=(?<sha>[0-9a-f]{64}) process=(?<process>\S+) " +
        "rss_units=(?<units>\S+) " +
        "rss_metric=(?<metric>\S+) " +
        "process_start_peak_rss_kib=(?<start>\d+) " +
        "before_peak_rss_kib=(?<beforePeak>\d+) " +
        "before_current_rss_kib=(?<beforeCurrent>-?\d+) " +
        "after_peak_rss_kib=(?<afterPeak>\d+) " +
        "after_current_rss_kib=(?<afterCurrent>-?\d+)"
    $observations = @(
        [regex]::Matches($conversionLogs, $conversionPattern) |
            ForEach-Object {
                [ordered]@{
                    mediaType = $_.Groups["media"].Value
                    decodedSourceBytes = [long]$_.Groups["size"].Value
                    sourceSha256 = $_.Groups["sha"].Value
                    process = $_.Groups["process"].Value
                    units = $_.Groups["units"].Value
                    metricKind = $_.Groups["metric"].Value
                    processStartPeakRssKiB = [long]$_.Groups["start"].Value
                    beforePeakRssKiB = [long]$_.Groups["beforePeak"].Value
                    beforeCurrentRssKiB = [long]$_.Groups["beforeCurrent"].Value
                    afterPeakRssKiB = [long]$_.Groups["afterPeak"].Value
                    afterCurrentRssKiB = [long]$_.Groups["afterCurrent"].Value
                }
            })
    if (($ColdCache -or $FreshRuntime) -and
        ($observations.Count -lt 3 -or
        @($observations | Where-Object {
            $_.decodedSourceBytes -eq 25808724 -and
            $_.sourceSha256 -eq
                "9b68858e1b2b7b30f77eb74dd48e9b26e78419e9c8a7f08adfe01876ac7ee2b8"
        }).Count -eq 0)) {
        throw "Showcase conversion memory evidence was not emitted."
    }
    [IO.File]::WriteAllText(
        (Join-Path $evidenceRoot "server-memory-initial.json"),
        ([ordered]@{
            schemaVersion = 2
            generatorId = "showcase-server-memory-evidence-v3"
            evidenceKind = "markitdown-server-memory"
            interpretation =
                "Linux process-lifetime high-water and current RSS observations; values are not per-document allocation deltas."
            observations = $observations
        } | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Restart-Showcase
    $restartMarkItDownEgress = Test-MarkItDownEgress
    Write-AcquisitionEvidence "warm-restart" "acquisition-restart.json"
    Invoke-ShowcaseTests "restart"
    Write-RetrievalEvidence "retrieval-restart.json"
    $initial = Get-Content -Raw (Join-Path $evidenceRoot "retrieval-initial.json") |
        ConvertFrom-Json
    $restart = Get-Content -Raw (Join-Path $evidenceRoot "retrieval-restart.json") |
        ConvertFrom-Json
    if ($initial.IndexIdentity -ne $restart.IndexIdentity -or
        $initial.SnapshotContentId -ne $restart.SnapshotContentId -or
        $initial.VectorPayloadSha256 -ne $restart.VectorPayloadSha256 -or
        $initial.MetadataCount -ne $restart.MetadataCount -or
        $initial.ContentChunkCount -ne $restart.ContentChunkCount) {
        throw "Showcase restart changed stable index identity or counts."
    }
    $lock = Get-Content -Raw $sourceLock | ConvertFrom-Json
    $nativeSearch =
        Get-Content -Raw (Join-Path $evidenceRoot "native-search-initial.json") |
        ConvertFrom-Json
    $nativeSimilar =
        Get-Content -Raw (Join-Path $evidenceRoot "native-similar-initial.json") |
        ConvertFrom-Json
    foreach ($query in $initial.Queries) {
        $item = $lock.items | Where-Object id -eq $query.IntendedItemId
        $native = $nativeSearch.queries |
            Where-Object query -eq $query.Query
        if ($query.IntendedRank -ne 1 -or
            $query.SignedMarginOverHighestOther -le $initial.CosineEpsilon -or
            @($native).Count -ne 1 -or
            @($native.results).Count -eq 0 -or
            $native.results[0].name -ne $item.title -or
            $native.results[0].type -ne $item.jellyfinType) {
            throw "Native search and direct C# evidence disagree for '$($query.IntendedItemId)'."
        }
    }
    if ($nativeSearch.negativeControl.resultCount -ne 0) {
        throw "The native unrelated-query control returned a result."
    }
    foreach ($recommendation in $initial.Recommendations) {
        $sourceItem =
            $lock.items | Where-Object id -eq $recommendation.SourceItemId
        $native = $nativeSimilar.directions |
            Where-Object sourceTitle -eq $sourceItem.title
        $semanticExpectedTitle = if ($null -eq $recommendation.ExpectedItemId) {
            $null
        } else {
            ($lock.items |
                Where-Object id -eq $recommendation.ExpectedItemId).title
        }
        $actualTitle = if (@($native.results).Count -eq 0) {
            $null
        } else {
            $native.results[0].name
        }
        $semanticRuntimeResult =
            @($native.semanticRuntimeResults)
        $semanticRuntimeMatches = if ($null -eq $semanticExpectedTitle) {
            $semanticRuntimeResult.Count -eq 0
        } else {
            $semanticRuntimeResult.Count -eq 1 -and
            [Math]::Abs(
                [double]$semanticRuntimeResult[0].score -
                [double]$recommendation.TopCandidateSimilarity) -le
                    [double]$initial.CosineEpsilon
        }
        if (@($native).Count -ne 1 -or
            $native.semanticExpectedTitle -ne $semanticExpectedTitle -or
            $actualTitle -ne $native.nativeExpectedTitle -or
            -not $semanticRuntimeMatches -or
            $native.semanticRuntimeEffectiveFloor -ne
                $recommendation.ConfiguredFloor -or
            $recommendation.EligibleAtConfiguredFloor -ne
                ($recommendation.TopCandidateSimilarity -gt
                    $recommendation.ConfiguredFloor) -or
            $recommendation.EligibleAtDefaultFloor -ne
                ($recommendation.TopCandidateSimilarity -gt
                    $recommendation.DefaultFloor)) {
            throw "Native similar-items and raw matrix evidence disagree for '$($recommendation.SourceItemId)'."
        }
    }
    $initialRuntime =
        Get-Content -Raw (Join-Path $evidenceRoot "runtime-initial.json") |
        ConvertFrom-Json
    $restartRuntime =
        Get-Content -Raw (Join-Path $evidenceRoot "runtime-restart.json") |
        ConvertFrom-Json
    if ($restartRuntime.IndexGeneration -le $initialRuntime.IndexGeneration -or
        $restartRuntime.IndexIdentity -ne $initialRuntime.IndexIdentity -or
        $restartRuntime.MetadataRecordCount -ne 6 -or
        $restartRuntime.LastEmbeddedMetadataCount -ne 0 -or
        $restartRuntime.LastEmbeddedContentChunkCount -ne 0 -or
        $restartRuntime.IngestEmbeddingsTotal -ne
            $initialRuntime.IngestEmbeddingsTotal -or
        $restartRuntime.QueryEmbeddingsTotal -lt
            $initialRuntime.QueryEmbeddingsTotal) {
        throw "Showcase restart counter evidence did not prove zero ingest embeddings and stable identity."
    }
    if (($ColdCache -or $FreshRuntime) -and
        ($initialRuntime.LastEmbeddedMetadataCount -ne 6 -or
         $initialRuntime.LastEmbeddedContentChunkCount -ne 6649 -or
         $initialRuntime.IngestEmbeddingsTotal -ne 6655)) {
        throw "Cold showcase did not embed the exact 6 metadata + 6,649 content records."
    }
    foreach ($runtime in @($initialRuntime, $restartRuntime)) {
        if ($runtime.NetworkBoundary.ExternalHttpCanaryBlocked -ne $true -or
            $runtime.NetworkBoundary.MarkItDownReachable -ne $true -or
            $runtime.EffectiveRecommendationFloor -ne
                $expectedRecommendationFloor -or
            $runtime.AppHostExpectedRecommendationFloor -ne
                $expectedRecommendationFloor) {
            throw "Running plugin network/floor evidence disagrees with AppHost policy."
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $evidenceRoot "network-policy.json"),
        ([ordered]@{
            schemaVersion = 2
            generatorId = "showcase-network-evidence-v3"
            evidenceKind = "runtime-network-controls"
            jellyfin = [ordered]@{
                enforcement = $initialRuntime.NetworkBoundary.Enforcement
                residualLimitation =
                    $initialRuntime.NetworkBoundary.ResidualLimitation
                initialExternalHttpCanaryBlocked =
                    $initialRuntime.NetworkBoundary.ExternalHttpCanaryBlocked
                restartExternalHttpCanaryBlocked =
                    $restartRuntime.NetworkBoundary.ExternalHttpCanaryBlocked
                initialMarkItDownReachable =
                    $initialRuntime.NetworkBoundary.MarkItDownReachable
                restartMarkItDownReachable =
                    $restartRuntime.NetworkBoundary.MarkItDownReachable
                markItDownReachabilityProbe =
                    $initialRuntime.NetworkBoundary.MarkItDownReachabilityProbe
            }
            markItDown = [ordered]@{
                enforcement = "Python socket API guard"
                initial = $initialMarkItDownEgress
                restart = $restartMarkItDownEgress
            }
            providerConfigurationAssertedBy =
                "LibrariesDisableRemoteMetadataArtworkAndLocalImageGeneration"
        } | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    if ($PublishEvidence) {
        $publishedRoot = Join-Path $repoRoot "assets\showcase\evidence"
        New-Item -ItemType Directory -Force $publishedRoot | Out-Null
        Get-ChildItem -LiteralPath $publishedRoot -File -Filter *.json |
            Remove-Item -Force
        $publicationMap = [ordered]@{
            "acquisition-initial.json" =
                "measured-acquisition-cold.json"
            "acquisition-restart.json" =
                "measured-acquisition-restart.json"
            "retrieval-initial.json" =
                "measured-retrieval.json"
            "native-search-initial.json" =
                "measured-native-search.json"
            "native-similar-initial.json" =
                "measured-native-similar.json"
            "runtime-initial.json" =
                "measured-runtime-initial.json"
            "runtime-restart.json" =
                "measured-runtime-restart.json"
            "server-memory-initial.json" =
                "measured-server-memory.json"
            "network-policy.json" =
                "measured-network-policy.json"
        }
        foreach ($entry in $publicationMap.GetEnumerator()) {
            Copy-CanonicalEvidence `
                (Join-Path $evidenceRoot $entry.Key) `
                (Join-Path $publishedRoot $entry.Value)
        }
        dotnet run `
            --project src\Jellyfin.SemanticDiscovery.Setup `
            --no-build `
            --no-restore `
            -- showcase-evidence-manifest `
            --repository-root $repoRoot `
            --evidence-root $publishedRoot `
            --base-commit 43f393b6b419eb95afb0a485756649d4a280a372 `
            --output (Join-Path $publishedRoot "evidence-manifest.json")
        if ($LASTEXITCODE -ne 0) {
            throw "Showcase evidence manifest publication failed."
        }
    }
    if ($ColdCache) {
        Stop-Showcase
        Remove-OwnedCache
        Remove-OwnedRuntime
        if ((Test-Path -LiteralPath $showcaseRoot) -or
            (Test-Path -LiteralPath $dataRoot)) {
            throw "Cold showcase cleanup left cache or runtime residue."
        }
    } elseif ($FreshRuntime) {
        Stop-Showcase
        Remove-OwnedRuntime
        if (Test-Path -LiteralPath $dataRoot) {
            throw "Fresh measurement runtime cleanup left residue."
        }
    }

    Write-Host "Showcase profile, extraction anchors, measured scores, and restart reuse passed."
} catch {
    $primaryFailure = $_
    throw
} finally {
    Remove-Item Env:JELLYFIN_E2E_CREDENTIALS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:JELLYFIN_E2E_PROFILE -ErrorAction SilentlyContinue
    Remove-Item Env:SHOWCASE_E2E_RUNTIME_EVIDENCE -ErrorAction SilentlyContinue
    Remove-Item Env:SHOWCASE_E2E_NATIVE_SEARCH_EVIDENCE -ErrorAction SilentlyContinue
    Remove-Item Env:SHOWCASE_E2E_NATIVE_SIMILAR_EVIDENCE -ErrorAction SilentlyContinue
    Remove-Item Env:SHOWCASE_E2E_EXPECTED_RECOMMENDATION_FLOOR -ErrorAction SilentlyContinue
    Remove-Item Env:SHOWCASE_E2E_REQUIRE_CLIENT_MEMORY -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_DATA -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_PROFILE -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_SHOWCASE_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_CONTAINER_USER -ErrorAction SilentlyContinue
    try {
        Stop-Showcase
    } catch {
        if ($null -eq $primaryFailure) { throw }
        Write-Warning "Showcase AppHost cleanup failed; cache was retained."
    }
    Pop-Location
}
