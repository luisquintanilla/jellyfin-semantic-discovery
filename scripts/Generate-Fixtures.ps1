#requires -version 7.0
<#
.SYNOPSIS
    Deterministically generates the tiny synthetic Jellyfin library fixtures used by the
    semantic discovery demo (Movie, Series, PDF, EPUB) from assets/fixtures/manifest.json,
    and validates the generated output.

.DESCRIPTION
    Reads assets/fixtures/manifest.json as the single source of truth for fixture content
    (titles, synthetic subtitle/metadata/document text, distinctive retrieval phrases, and
    expected structural properties). Produces a Jellyfin 12-shaped library tree under
    assets/fixtures/generated/library/ containing:

      Movies/<Title> (<Year>)/movie.nfo, <video>.mp4, <video>.<lang>.srt, <video>.<lang>.vtt
      Series/<Title> (<Year>)/tvshow.nfo
        Season 01/season.nfo, <episode>.mp4, <episode>.nfo, <episode>.<lang>.srt/.vtt
      Books/<Title> (<Year>)/<file>.pdf|.epub, metadata.opf

    MP4s are produced with ffmpeg lavfi test sources (video) and a sine tone (audio) under
    the locked linux/amd64 generator image, with x264 assembly disabled to avoid CPU feature
    dispatch changing bytes. PDF and EPUB fixtures are hand-built from raw
    bytes / a ZipArchive using only .NET/PowerShell standard libraries (no third-party PDF
    or EPUB packages). NFO/OPF sidecars are written with System.Xml.XmlWriter for safe
    escaping.

    The script validates everything it produces (ffprobe duration/codec/pixel-format for
    each MP4, subtitle cue counts, PDF header/footer byte markers + page count + xref
    self-consistency, EPUB zip structure via container.xml -> OPF -> spine, and presence of
    each distinctive retrieval phrase in its declared location) and writes a
    generated/manifest.actual.json recording the measured properties and pass/fail result
    per check. No benchmark, accuracy, or performance metric is invented or recorded --
    only objectively verifiable structural facts.

    Verification is read-only. Generate accepts only an absent/empty destination or one
    carrying this generator's ownership marker, then recreates it. UpdateChecksums stages
    and validates the complete corpus before atomically replacing the checksum file.

.PARAMETER ManifestPath
    Path to the source manifest. Defaults to assets/fixtures/manifest.json next to this
    script.

.PARAMETER OutputRoot
    Directory the generated library and manifest.actual.json are written under. Verify and
    UpdateChecksums default to the tracked canonical corpus; Generate must use another path.

.PARAMETER Mode
    Verify performs read-only validation. Generate recreates a non-canonical
    output directory using the locked toolchain. UpdateChecksums regenerates
    the complete canonical corpus in a staging directory and replaces the
    corpus/checksum lock only after every validation succeeds.

.EXAMPLE
    pwsh ./scripts/Generate-Fixtures.ps1 -Mode Verify

.EXAMPLE
    docker run --rm --mount type=bind,source=<repo>,target=/repo `
      jellyfin-semantic-fixture-generator:locked `
      -Mode UpdateChecksums
#>
[CmdletBinding()]
param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\assets\fixtures\manifest.json'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\assets\fixtures\generated'),
    [ValidateSet('Verify', 'Generate', 'UpdateChecksums')]
    [string]$Mode = 'Verify'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

# ---------------------------------------------------------------------------
# Small shared utilities
# ---------------------------------------------------------------------------

function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Reset-OutputRoot {
    param(
        [string]$Path,
        [string]$GeneratorIdentity
    )

    $fullPath = Resolve-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root) -or
        $fullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) -eq
            $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar)) {
        throw "Refusing to reset filesystem root '$fullPath'."
    }

    if (Test-Path -LiteralPath $fullPath) {
        $children = @(Get-ChildItem -LiteralPath $fullPath -Force)
        if ($children.Count -gt 0) {
            $markerPath = Join-Path $fullPath 'manifest.actual.json'
            if (-not (Test-Path -LiteralPath $markerPath)) {
                throw "Refusing to reset non-empty unowned output '$fullPath'."
            }

            $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
            if ($marker.generatorOwnedOutput -ne $true -or
                $marker.generatorIdentity -ne $GeneratorIdentity) {
                throw "Refusing to reset output '$fullPath' without this generator's ownership marker."
            }
        }

        $children |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
    } else {
        New-Item -ItemType Directory -Force -Path $fullPath | Out-Null
    }
}

function ConvertTo-OutputRelativePath {
    param([string]$OutputRootPath, [string]$Path)
    return [System.IO.Path]::GetRelativePath($OutputRootPath, $Path).Replace('\', '/')
}

function Get-CanonicalHashes {
    param([string]$Root)

    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $Root 'library') -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = ConvertTo-OutputRelativePath -OutputRootPath $Root -Path $_.FullName
            $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }

        return $hashes
}

function Get-NormalizedTextSha256 {
        param([string]$Path)

        $text = [System.IO.File]::ReadAllText($Path)
        $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
        $bytes = (New-Utf8NoBomEncoding).GetBytes($normalized)
        return [Convert]::ToHexStringLower(
            [Security.Cryptography.SHA256]::HashData($bytes))
}

function Assert-GeneratorToolchain {
    param($Toolchain)

    if (-not (Test-Path -LiteralPath $Toolchain.lockPath)) {
        throw "Fixture generator toolchain lock is missing."
    }
    $lockHash = Get-NormalizedTextSha256 $Toolchain.lockPath
    if ($lockHash -ne $Toolchain.lockSha256) {
        throw "Fixture generator toolchain lock hash does not match the manifest."
    }
    $lock = Get-Content -Raw -LiteralPath $Toolchain.lockPath | ConvertFrom-Json

    $actualPowerShell = "PowerShell $($PSVersionTable.PSVersion.ToString())"
    if ($actualPowerShell -ne $lock.powershellVersion) {
        throw "Unapproved PowerShell: expected '$($lock.powershellVersion)', found '$actualPowerShell'."
    }

    $ffmpegPath = (Get-Command ffmpeg -ErrorAction Stop).Source
    $ffprobePath = (Get-Command ffprobe -ErrorAction Stop).Source
    $ffmpegVersion = (& $ffmpegPath -version 2>&1 | Select-Object -First 1)
    $ffprobeVersion = (& $ffprobePath -version 2>&1 | Select-Object -First 1)
    $ffmpegHash = (Get-FileHash -LiteralPath $ffmpegPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $ffprobeHash = (Get-FileHash -LiteralPath $ffprobePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageCommand =
        'dpkg-query -W -f="\${Package}=\${Version}\n" | LC_ALL=C sort | sha256sum'
    $libraryCommand =
        "{ ldd /usr/bin/ffmpeg; ldd /usr/bin/ffprobe; } | " +
        "awk '/=> \// || /^\// { for (i=1;i<=NF;i++) if (`$i ~ /^\//) print `$i }' | " +
        "LC_ALL=C sort -u | xargs sha256sum | LC_ALL=C sort | sha256sum"
    $packageOutput = & bash -lc $packageCommand
    $libraryOutput = & bash -lc $libraryCommand
    $packageClosure = $packageOutput.Split(
        ' ',
        [System.StringSplitOptions]::RemoveEmptyEntries)[0]
    $linkedLibraryClosure = $libraryOutput.Split(
        ' ',
        [System.StringSplitOptions]::RemoveEmptyEntries)[0]

    if ($ffmpegVersion -ne $lock.ffmpegVersion -or
        $ffprobeVersion -ne $lock.ffprobeVersion -or
        $ffmpegHash -ne $lock.ffmpegSha256 -or
        $ffprobeHash -ne $lock.ffprobeSha256 -or
        $packageClosure -ne $lock.packageClosureSha256 -or
        $linkedLibraryClosure -ne $lock.linkedLibraryClosureSha256) {
        throw "The active FFmpeg toolchain does not match the locked fixture generator."
    }

    foreach ($library in $lock.criticalLinkedLibraries.PSObject.Properties) {
        if (-not (Test-Path -LiteralPath $library.Name)) {
            throw "Critical generator library '$($library.Name)' is missing."
        }
        $libraryHash = Get-FileHash -LiteralPath $library.Name -Algorithm SHA256
        if ($libraryHash.Hash.ToLowerInvariant() -ne $library.Value) {
            throw "Critical generator library '$($library.Name)' does not match its lock."
        }
    }
}

function Test-CanonicalHashes {
    param(
        [System.Collections.IDictionary]$Actual,
        [string]$ChecksumsPath
    )

    if (-not (Test-Path -LiteralPath $ChecksumsPath)) {
        throw "Canonical checksum file not found at '$ChecksumsPath'."
    }

    $expected = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $ChecksumsPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Invalid checksum line: '$line'."
        }
        if ($expected.Contains($Matches[2])) {
            throw "Duplicate checksum path '$($Matches[2])'."
        }
        $expected[$Matches[2]] = $Matches[1]
    }

    if ($expected.Count -ne $Actual.Count) {
        return $false
    }
    foreach ($path in $expected.Keys) {
        if (-not $Actual.Contains($path) -or $Actual[$path] -ne $expected[$path]) {
            return $false
        }
    }
    return $true
}

function Write-CanonicalHashes {
    param(
        [System.Collections.IDictionary]$Hashes,
        [string]$Path
    )

    $lines = foreach ($filePath in $Hashes.Keys) {
        "$($Hashes[$filePath])  $filePath"
    }
    [System.IO.File]::WriteAllLines(
        $Path,
        $lines,
        (New-Utf8NoBomEncoding))
}

function New-Latin1Encoding {
    return [System.Text.Encoding]::GetEncoding('ISO-8859-1')
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function New-XmlWriterSettingsNoBom {
    param([switch]$OmitDeclaration)
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.Encoding = New-Utf8NoBomEncoding
    $settings.OmitXmlDeclaration = [bool]$OmitDeclaration
    return $settings
}

function ConvertTo-PSCustomHashtable {
    # Converts nested PSCustomObject (from ConvertFrom-Json) member access is already
    # supported natively via dotted property access, so this helper is only used where an
    # ordered hashtable is more convenient to build up for JSON re-serialization.
    param($InputObject)
    return $InputObject
}

# ---------------------------------------------------------------------------
# Subtitle (SRT/VTT) generation and validation
# ---------------------------------------------------------------------------

function Format-SrtTimestamp {
    param([double]$Seconds)
    $ts = [TimeSpan]::FromSeconds($Seconds)
    return ('{0:00}:{1:00}:{2:00},{3:000}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds, $ts.Milliseconds)
}

function Format-VttTimestamp {
    param([double]$Seconds)
    $ts = [TimeSpan]::FromSeconds($Seconds)
    return ('{0:00}:{1:00}:{2:00}.{3:000}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds, $ts.Milliseconds)
}

function Write-SrtFile {
    param([string]$Path, [array]$Cues)
    $lines = New-Object System.Collections.Generic.List[string]
    $i = 1
    foreach ($cue in $Cues) {
        $lines.Add("$i")
        $lines.Add("$(Format-SrtTimestamp $cue.start) --> $(Format-SrtTimestamp $cue.end)")
        $lines.Add([string]$cue.text)
        $lines.Add('')
        $i++
    }
    $content = ($lines -join "`r`n").TrimEnd() + "`r`n"
    [System.IO.File]::WriteAllText($Path, $content, (New-Utf8NoBomEncoding))
}

function Write-VttFile {
    param([string]$Path, [array]$Cues)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('WEBVTT')
    $lines.Add('')
    foreach ($cue in $Cues) {
        $lines.Add("$(Format-VttTimestamp $cue.start) --> $(Format-VttTimestamp $cue.end)")
        $lines.Add([string]$cue.text)
        $lines.Add('')
    }
    $content = ($lines -join "`r`n").TrimEnd() + "`r`n"
    [System.IO.File]::WriteAllText($Path, $content, (New-Utf8NoBomEncoding))
}

function Test-SubtitleFiles {
    param([string]$SrtPath, [string]$VttPath, [int]$ExpectedCueCount)
    $result = [ordered]@{
        srtExists     = (Test-Path -LiteralPath $SrtPath)
        vttExists     = (Test-Path -LiteralPath $VttPath)
        srtCueCount   = 0
        vttCueCount   = 0
        srtCueCountOk = $false
        vttCueCountOk = $false
    }
    if ($result.srtExists) {
        $srtText = Get-Content -Raw -LiteralPath $SrtPath
        $result.srtCueCount = ([regex]::Matches($srtText, '\d\d:\d\d:\d\d,\d\d\d\s*-->\s*\d\d:\d\d:\d\d,\d\d\d')).Count
        $result.srtCueCountOk = ($result.srtCueCount -eq $ExpectedCueCount)
    }
    if ($result.vttExists) {
        $vttText = Get-Content -Raw -LiteralPath $VttPath
        $result.vttCueCount = ([regex]::Matches($vttText, '\d\d:\d\d:\d\d\.\d\d\d\s*-->\s*\d\d:\d\d:\d\d\.\d\d\d')).Count
        $result.vttCueCountOk = ($result.vttCueCount -eq $ExpectedCueCount)
    }
    $result.success = $result.srtExists -and $result.vttExists -and $result.srtCueCountOk -and $result.vttCueCountOk
    return $result
}

# ---------------------------------------------------------------------------
# Jellyfin NFO / OPF sidecar generation (System.Xml.XmlWriter -- safe escaping)
# ---------------------------------------------------------------------------

function Write-MovieNfo {
    param([string]$Path, $Item)
    $writer = [System.Xml.XmlWriter]::Create($Path, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('movie')
        $writer.WriteElementString('title', $Item.title)
        $writer.WriteElementString('sorttitle', $Item.sortTitle)
        $writer.WriteElementString('year', "$($Item.year)")
        $writer.WriteElementString('premiered', $Item.premiered)
        $writer.WriteElementString('plot', $Item.metadata.plot)
        $writer.WriteElementString('outline', $Item.metadata.outline)
        $writer.WriteElementString('genre', $Item.genre)
        $writer.WriteElementString('studio', $Item.studio)
        if ($Item.officialRating) {
            $writer.WriteElementString('mpaa', $Item.officialRating)
        }
        foreach ($tag in @($Item.tags)) {
            $writer.WriteElementString('tag', $tag)
        }
        $writer.WriteStartElement('uniqueid')
        $writer.WriteAttributeString('type', 'fixture')
        $writer.WriteAttributeString('default', 'true')
        $writer.WriteString($Item.uniqueId)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    } finally {
        $writer.Dispose()
    }
}

function Write-TvShowNfo {
    param([string]$Path, $Item)
    $writer = [System.Xml.XmlWriter]::Create($Path, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('tvshow')
        $writer.WriteElementString('title', $Item.seriesTitle)
        $writer.WriteElementString('year', "$($Item.year)")
        $writer.WriteElementString('premiered', $Item.premiered)
        $writer.WriteElementString('plot', $Item.metadata.seriesPlot)
        $writer.WriteElementString('genre', $Item.genre)
        $writer.WriteElementString('studio', $Item.studio)
        $writer.WriteStartElement('uniqueid')
        $writer.WriteAttributeString('type', 'fixture')
        $writer.WriteAttributeString('default', 'true')
        $writer.WriteString($Item.uniqueIdSeries)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    } finally {
        $writer.Dispose()
    }
}

function Write-SeasonNfo {
    param([string]$Path, $Item)
    $writer = [System.Xml.XmlWriter]::Create($Path, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('season')
        $writer.WriteElementString('seasonnumber', "$($Item.seasonNumber)")
        $writer.WriteElementString('title', "Season $($Item.seasonNumber)")
        $writer.WriteElementString('plot', $Item.metadata.seasonPlot)
        $writer.WriteEndElement()
    } finally {
        $writer.Dispose()
    }
}

function Write-EpisodeNfo {
    param([string]$Path, $Item)
    $writer = [System.Xml.XmlWriter]::Create($Path, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('episodedetails')
        $writer.WriteElementString('title', $Item.episodeTitle)
        $writer.WriteElementString('season', "$($Item.seasonNumber)")
        $writer.WriteElementString('episode', "$($Item.episodeNumber)")
        $writer.WriteElementString('plot', $Item.metadata.episodePlot)
        $writer.WriteStartElement('uniqueid')
        $writer.WriteAttributeString('type', 'fixture')
        $writer.WriteAttributeString('default', 'true')
        $writer.WriteString($Item.uniqueIdEpisode)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    } finally {
        $writer.Dispose()
    }
}

function Write-BookOpf {
    param([string]$Path, $Item, [string]$MediaFile, [string]$MediaType)
    $writer = [System.Xml.XmlWriter]::Create($Path, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('package', 'http://www.idpf.org/2007/opf')
        $writer.WriteAttributeString('unique-identifier', 'BookId')
        $writer.WriteAttributeString('version', '2.0')

        $writer.WriteStartElement('metadata')
        $writer.WriteAttributeString('xmlns', 'dc', $null, 'http://purl.org/dc/elements/1.1/')
        $writer.WriteAttributeString('xmlns', 'opf', $null, 'http://www.idpf.org/2007/opf')
        $writer.WriteStartElement('dc', 'identifier', 'http://purl.org/dc/elements/1.1/')
        $writer.WriteAttributeString('id', 'BookId')
        $writer.WriteString($Item.uniqueId)
        $writer.WriteEndElement()
        $writer.WriteElementString('dc', 'title', 'http://purl.org/dc/elements/1.1/', $Item.title)
        $writer.WriteElementString('dc', 'creator', 'http://purl.org/dc/elements/1.1/', $Item.author)
        $writer.WriteElementString('dc', 'language', 'http://purl.org/dc/elements/1.1/', $Item.language)
        $writer.WriteElementString('dc', 'date', 'http://purl.org/dc/elements/1.1/', "$($Item.year)")
        $writer.WriteElementString('dc', 'description', 'http://purl.org/dc/elements/1.1/', $Item.description)
        $writer.WriteEndElement() # metadata

        $writer.WriteStartElement('manifest')
        $writer.WriteStartElement('item')
        $writer.WriteAttributeString('id', 'book')
        $writer.WriteAttributeString('href', $MediaFile)
        $writer.WriteAttributeString('media-type', $MediaType)
        $writer.WriteEndElement()
        $writer.WriteEndElement() # manifest

        $writer.WriteEndElement() # package
    } finally {
        $writer.Dispose()
    }
}

# ---------------------------------------------------------------------------
# ffmpeg / ffprobe helpers
# ---------------------------------------------------------------------------

function Invoke-FfmpegGenerate {
    param(
        [string]$OutputPath, [string]$Pattern, [int]$Width, [int]$Height, [int]$Fps,
        [double]$DurationSeconds, [int]$ToneHz, $VideoDefaults
    )
    $ffmpeg = Get-Command ffmpeg -ErrorAction Stop
    $videoLavfi = "$Pattern=size=${Width}x${Height}:rate=$Fps`:duration=$DurationSeconds"
    $audioLavfi = "sine=frequency=$ToneHz`:sample_rate=$($VideoDefaults.audioSampleRate):duration=$DurationSeconds"
    $ffmpegArgs = @(
        '-y', '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', $videoLavfi,
        '-f', 'lavfi', '-i', $audioLavfi,
        '-map', '0:v:0', '-map', '1:a:0',
        '-c:v', $VideoDefaults.videoCodec,
        '-pix_fmt', $VideoDefaults.pixelFormat,
        '-preset', $VideoDefaults.encodePreset,
        '-crf', "$($VideoDefaults.crf)",
        '-profile:v', 'baseline',
        '-level', '3.0',
        '-x264-params', 'asm=0:threads=1:lookahead_threads=1:sliced_threads=0',
        '-c:a', $VideoDefaults.audioCodec,
        '-b:a', '96k',
        '-ac', "$($VideoDefaults.audioChannels)",
        '-movflags', '+faststart',
        '-metadata', 'creation_time=2024-01-01T00:00:00.000000Z',
        '-map_metadata', '-1',
        '-threads', '1',
        '-shortest',
        $OutputPath
    )
    & $ffmpeg.Source @ffmpegArgs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed for '$OutputPath' (exit code $LASTEXITCODE)"
    }
}

function Get-FfprobeInfo {
    param([string]$Path)
    $ffprobe = Get-Command ffprobe -ErrorAction Stop
    $jsonLines = & $ffprobe.Source -v error -print_format json -show_format -show_streams -- $Path
    return ($jsonLines -join "`n") | ConvertFrom-Json
}

function Test-VideoAgainstExpectation {
    param($Probe, $Item, $VideoDefaults)
    $result = [ordered]@{
        durationSeconds = $null
        durationOk      = $false
        containerOk     = $false
        videoCodecOk    = $false
        audioCodecOk    = $false
        pixelFormatOk   = $false
        resolutionOk    = $false
    }
    $format = $Probe.format
    $videoStream = $Probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $audioStream = $Probe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1

    $duration = [double]$format.duration
    $result.durationSeconds = $duration
    $expectedDuration = [double]$Item.media.durationSeconds
    $result.durationOk = ([Math]::Abs($duration - $expectedDuration) -le [double]$VideoDefaults.durationToleranceSeconds)
    $result.containerOk = ($format.format_name -like '*mp4*')
    $result.videoCodecOk = ($null -ne $videoStream -and $videoStream.codec_name -eq $VideoDefaults.videoCodecExpectedName)
    $result.audioCodecOk = ($null -ne $audioStream -and $audioStream.codec_name -eq $VideoDefaults.audioCodecExpectedName)
    $result.pixelFormatOk = ($null -ne $videoStream -and $videoStream.pix_fmt -eq $VideoDefaults.pixelFormat)
    $result.resolutionOk = ($null -ne $videoStream -and $videoStream.width -eq $Item.media.width -and $videoStream.height -eq $Item.media.height)
    $result.success = $result.durationOk -and $result.containerOk -and $result.videoCodecOk -and $result.audioCodecOk -and $result.pixelFormatOk -and $result.resolutionOk
    return $result
}

# ---------------------------------------------------------------------------
# Distinctive-phrase self checks (manifest authoring sanity, run before
# generation so a mistake in the source manifest fails fast and loudly)
# ---------------------------------------------------------------------------

function Test-MovieDistinctivePhrases {
    param($Item)
    $plotText = [string]$Item.metadata.plot
    $outlineText = [string]$Item.metadata.outline
    $cueTexts = @($Item.subtitles.cues | ForEach-Object { [string]$_.text })

    foreach ($dp in $Item.distinctivePhrases) {
        $phrase = $dp.phrase
        if ([string]::IsNullOrEmpty($phrase)) { continue }

        if ($dp.location -eq 'metadata.plot') {
            if ($plotText.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Movie '$($Item.id)': metadata-only phrase '$phrase' was not found in metadata.plot"
            }
        } elseif ($dp.location -match '^subtitles\.cues\[(\d+)\]\.text$') {
            $idx = [int]$Matches[1]
            if ($cueTexts[$idx].IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Movie '$($Item.id)': subtitle-only phrase '$phrase' was not found in cue index $idx"
            }
        }

        foreach ($forbiddenLoc in @($dp.mustNotAppearIn)) {
            if ($forbiddenLoc -eq 'subtitles.cues[*].text') {
                foreach ($ct in $cueTexts) {
                    if ($ct.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        throw "Movie '$($Item.id)': phrase '$phrase' unexpectedly found in a subtitle cue (should be metadata-only)"
                    }
                }
            } elseif ($forbiddenLoc -eq 'metadata.plot') {
                if ($plotText.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Movie '$($Item.id)': phrase '$phrase' unexpectedly found in metadata.plot (should be subtitle-only)"
                }
            } elseif ($forbiddenLoc -eq 'metadata.outline') {
                if ($outlineText.IndexOf($phrase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Movie '$($Item.id)': phrase '$phrase' unexpectedly found in metadata.outline (should be subtitle-only)"
                }
            }
        }
    }
}

function Test-SeriesIsNegativeControl {
    param($Item)
    $allDp = @($Item.distinctivePhrases | Where-Object { $_.phrase })
    if ($allDp.Count -gt 0) {
        throw "Series '$($Item.id)' is marked as a negative-noise control but declares non-null distinctive phrases."
    }
}

# ---------------------------------------------------------------------------
# Minimal PDF construction (raw bytes, no third-party libraries) + validation
# ---------------------------------------------------------------------------

function ConvertTo-PdfEscapedString {
    param([string]$Text)
    $t = $Text.Replace('\', '\\')
    $t = $t.Replace('(', '\(')
    $t = $t.Replace(')', '\)')
    return $t
}

function New-MinimalPdfBytes {
    param([array]$Pages)

    $encoding = New-Latin1Encoding
    $sb = New-Object System.Text.StringBuilder
    $offsets = @{}

    $pageCount = $Pages.Count
    $catalogObj = 1
    $pagesObj = 2
    $pageObjNums = @()
    $contentObjNums = @()
    for ($i = 0; $i -lt $pageCount; $i++) {
        $pageObjNums += (3 + $i * 2)
        $contentObjNums += (4 + $i * 2)
    }
    $fontObj = 3 + $pageCount * 2
    $totalObjs = $fontObj

    [void]$sb.Append("%PDF-1.4`n")

    # 1: Catalog
    $offsets[$catalogObj] = $sb.Length
    [void]$sb.Append("$catalogObj 0 obj`n<< /Type /Catalog /Pages $pagesObj 0 R >>`nendobj`n")

    # 2: Pages
    $kids = ($pageObjNums | ForEach-Object { "$_ 0 R" }) -join ' '
    $offsets[$pagesObj] = $sb.Length
    [void]$sb.Append("$pagesObj 0 obj`n<< /Type /Pages /Kids [$kids] /Count $pageCount >>`nendobj`n")

    # Per-page Page + Contents objects
    for ($i = 0; $i -lt $pageCount; $i++) {
        $pageObj = $pageObjNums[$i]
        $contentObj = $contentObjNums[$i]
        $lines = $Pages[$i]

        $contentSb = New-Object System.Text.StringBuilder
        [void]$contentSb.Append("BT`n/F1 12 Tf`n20 170 Td`n")
        for ($li = 0; $li -lt $lines.Count; $li++) {
            if ($li -gt 0) { [void]$contentSb.Append("0 -16 Td`n") }
            $escaped = ConvertTo-PdfEscapedString ([string]$lines[$li])
            [void]$contentSb.Append("($escaped) Tj`n")
        }
        [void]$contentSb.Append("ET`n")
        $contentText = $contentSb.ToString()
        $contentLength = $contentText.Length

        $offsets[$pageObj] = $sb.Length
        [void]$sb.Append("$pageObj 0 obj`n<< /Type /Page /Parent $pagesObj 0 R /MediaBox [0 0 300 220] /Resources << /Font << /F1 $fontObj 0 R >> >> /Contents $contentObj 0 R >>`nendobj`n")

        $offsets[$contentObj] = $sb.Length
        [void]$sb.Append("$contentObj 0 obj`n<< /Length $contentLength >>`nstream`n")
        [void]$sb.Append($contentText)
        [void]$sb.Append("endstream`nendobj`n")
    }

    # Font
    $offsets[$fontObj] = $sb.Length
    [void]$sb.Append("$fontObj 0 obj`n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>`nendobj`n")

    $xrefOffset = $sb.Length
    [void]$sb.Append("xref`n0 $($totalObjs + 1)`n")
    [void]$sb.Append("0000000000 65535 f `n")
    for ($n = 1; $n -le $totalObjs; $n++) {
        [void]$sb.Append("{0:D10} 00000 n `n" -f $offsets[$n])
    }
    [void]$sb.Append("trailer`n<< /Size $($totalObjs + 1) /Root $catalogObj 0 R >>`n")
    [void]$sb.Append("startxref`n$xrefOffset`n%%EOF")

    return , $encoding.GetBytes($sb.ToString())
}

function Test-PdfFixture {
    param([string]$Path, [int]$ExpectedPageCount, [string]$ExpectedHeaderMarker, [string]$ExpectedFooterMarker, [array]$DistinctivePhrases)

    $encoding = New-Latin1Encoding
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = $encoding.GetString($bytes)

    $result = [ordered]@{
        headerOk        = $text.StartsWith($ExpectedHeaderMarker)
        footerOk        = $text.TrimEnd().EndsWith($ExpectedFooterMarker)
        pageCountActual = 0
        pageCountOk     = $false
        xrefOk          = $false
        rootOk          = $false
        noStrayEscapesOk = (-not $text.Contains('`'))
        phrasesOk       = $true
    }

    $pageMatches = [regex]::Matches($text, '/Type\s*/Page(?!s)')
    $result.pageCountActual = $pageMatches.Count
    $result.pageCountOk = ($result.pageCountActual -eq $ExpectedPageCount)

    # Structurally validate the xref table rather than just sniffing the
    # 'xref' keyword: every in-use entry must be exactly 20 bytes and must
    # resolve to an offset where the corresponding "<n> 0 obj" actually
    # starts. This catches subtle byte-formatting bugs (e.g. a stray literal
    # backtick-n instead of a real newline) that a looser check would miss.
    $xrefMatch = [regex]::Match($text, 'startxref\s+(\d+)\s+%%EOF')
    if ($xrefMatch.Success) {
        $xrefOffset = [int]$xrefMatch.Groups[1].Value
        if ($xrefOffset -ge 0 -and $xrefOffset -lt $text.Length) {
            $sectionMatch = [regex]::Match(
                $text.Substring($xrefOffset),
                '^xref\r?\n0 (\d+)\r?\n((?:\d{10} \d{5} [nf] \r?\n)+)trailer'
            )
            if ($sectionMatch.Success) {
                $entryCount = [int]$sectionMatch.Groups[1].Value
                $entriesBlock = $sectionMatch.Groups[2].Value
                $entryLines = [regex]::Matches($entriesBlock, '(\d{10}) (\d{5}) ([nf]) \r?\n')
                $allEntriesValid = ($entryLines.Count -eq $entryCount)
                if ($allEntriesValid) {
                    for ($objNum = 1; $objNum -lt $entryCount; $objNum++) {
                        $entry = $entryLines[$objNum]
                        if ($entry.Groups[3].Value -ne 'n') { continue }
                        $offset = [int]$entry.Groups[1].Value
                        $expectedPrefix = "$objNum 0 obj"
                        if ($offset -lt 0 -or $offset -ge $text.Length -or
                            -not $text.Substring($offset, [Math]::Min($expectedPrefix.Length, $text.Length - $offset)).Equals($expectedPrefix)) {
                            $allEntriesValid = $false
                            break
                        }
                    }
                }
                $result.xrefOk = $allEntriesValid
            }
            $result.rootOk = [regex]::IsMatch($text, 'trailer\s*<<[^>]*\/Root\s+1\s+0\s+R[^>]*>>')
        }
    }

    foreach ($dp in $DistinctivePhrases) {
        if ($dp.phrase -and $text.IndexOf($dp.phrase, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $result.phrasesOk = $false
        }
    }

    $result.success = $result.headerOk -and $result.footerOk -and $result.pageCountOk -and $result.xrefOk -and $result.rootOk -and $result.noStrayEscapesOk -and $result.phrasesOk
    return $result
}

# ---------------------------------------------------------------------------
# Minimal EPUB construction (System.IO.Compression.ZipArchive) + validation
# ---------------------------------------------------------------------------

function New-ChapterXhtmlBytes {
    param($Chapter)
    $ms = New-Object System.IO.MemoryStream
    $settings = New-XmlWriterSettingsNoBom
    $writer = [System.Xml.XmlWriter]::Create($ms, $settings)
    try {
        $writer.WriteDocType('html', $null, $null, $null)
        $writer.WriteStartElement('html', 'http://www.w3.org/1999/xhtml')
        $writer.WriteStartElement('head')
        $writer.WriteElementString('title', $Chapter.title)
        $writer.WriteEndElement() # head
        $writer.WriteStartElement('body')
        $writer.WriteElementString('h1', $Chapter.title)
        foreach ($p in $Chapter.paragraphs) {
            $writer.WriteElementString('p', [string]$p)
        }
        $writer.WriteEndElement() # body
        $writer.WriteEndElement() # html
    } finally {
        $writer.Dispose()
    }
    return , $ms.ToArray()
}

function New-ContainerXmlBytes {
    $ms = New-Object System.IO.MemoryStream
    $writer = [System.Xml.XmlWriter]::Create($ms, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('container', 'urn:oasis:names:tc:opendocument:xmlns:container')
        $writer.WriteAttributeString('version', '1.0')
        $writer.WriteStartElement('rootfiles')
        $writer.WriteStartElement('rootfile')
        $writer.WriteAttributeString('full-path', 'OEBPS/content.opf')
        $writer.WriteAttributeString('media-type', 'application/oebps-package+xml')
        $writer.WriteEndElement()
        $writer.WriteEndElement() # rootfiles
        $writer.WriteEndElement() # container
    } finally {
        $writer.Dispose()
    }
    return , $ms.ToArray()
}

function New-ContentOpfBytes {
    param($Item)
    $ms = New-Object System.IO.MemoryStream
    $writer = [System.Xml.XmlWriter]::Create($ms, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('package', 'http://www.idpf.org/2007/opf')
        $writer.WriteAttributeString('unique-identifier', 'BookId')
        $writer.WriteAttributeString('version', '2.0')

        $writer.WriteStartElement('metadata')
        $writer.WriteAttributeString('xmlns', 'dc', $null, 'http://purl.org/dc/elements/1.1/')
        $writer.WriteAttributeString('xmlns', 'opf', $null, 'http://www.idpf.org/2007/opf')
        $writer.WriteStartElement('dc', 'identifier', 'http://purl.org/dc/elements/1.1/')
        $writer.WriteAttributeString('id', 'BookId')
        $writer.WriteString($Item.uniqueId)
        $writer.WriteEndElement()
        $writer.WriteElementString('dc', 'title', 'http://purl.org/dc/elements/1.1/', $Item.title)
        $writer.WriteElementString('dc', 'creator', 'http://purl.org/dc/elements/1.1/', $Item.author)
        $writer.WriteElementString('dc', 'language', 'http://purl.org/dc/elements/1.1/', $Item.language)
        $writer.WriteElementString('dc', 'date', 'http://purl.org/dc/elements/1.1/', "$($Item.year)")
        $writer.WriteElementString('dc', 'description', 'http://purl.org/dc/elements/1.1/', $Item.description)
        $writer.WriteEndElement() # metadata

        $writer.WriteStartElement('manifest')
        $writer.WriteStartElement('item')
        $writer.WriteAttributeString('id', 'ncx')
        $writer.WriteAttributeString('href', 'toc.ncx')
        $writer.WriteAttributeString('media-type', 'application/x-dtbncx+xml')
        $writer.WriteEndElement()
        foreach ($chapter in $Item.chapters) {
            $writer.WriteStartElement('item')
            $writer.WriteAttributeString('id', $chapter.id)
            $writer.WriteAttributeString('href', $chapter.file)
            $writer.WriteAttributeString('media-type', 'application/xhtml+xml')
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement() # manifest

        $writer.WriteStartElement('spine')
        $writer.WriteAttributeString('toc', 'ncx')
        foreach ($chapter in $Item.chapters) {
            $writer.WriteStartElement('itemref')
            $writer.WriteAttributeString('idref', $chapter.id)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement() # spine

        $writer.WriteEndElement() # package
    } finally {
        $writer.Dispose()
    }
    return , $ms.ToArray()
}

function New-TocNcxBytes {
    param($Item)
    $ms = New-Object System.IO.MemoryStream
    $writer = [System.Xml.XmlWriter]::Create($ms, (New-XmlWriterSettingsNoBom))
    try {
        $writer.WriteStartElement('ncx', 'http://www.daisy.org/z3986/2005/ncx/')
        $writer.WriteAttributeString('version', '2005-1')
        $writer.WriteStartElement('head')
        $writer.WriteStartElement('meta')
        $writer.WriteAttributeString('name', 'dtb:uid')
        $writer.WriteAttributeString('content', $Item.uniqueId)
        $writer.WriteEndElement()
        $writer.WriteEndElement() # head
        $writer.WriteStartElement('docTitle')
        $writer.WriteElementString('text', $Item.title)
        $writer.WriteEndElement()
        $writer.WriteStartElement('navMap')
        $order = 1
        foreach ($chapter in $Item.chapters) {
            $writer.WriteStartElement('navPoint')
            $writer.WriteAttributeString('id', "navpoint-$order")
            $writer.WriteAttributeString('playOrder', "$order")
            $writer.WriteStartElement('navLabel')
            $writer.WriteElementString('text', $chapter.title)
            $writer.WriteEndElement()
            $writer.WriteStartElement('content')
            $writer.WriteAttributeString('src', $chapter.file)
            $writer.WriteEndElement()
            $writer.WriteEndElement() # navPoint
            $order++
        }
        $writer.WriteEndElement() # navMap
        $writer.WriteEndElement() # ncx
    } finally {
        $writer.Dispose()
    }
    return , $ms.ToArray()
}

function New-MinimalEpubFile {
    param([string]$Path, $Item)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }

    $asciiNoBom = [System.Text.Encoding]::ASCII
    $entries = [ordered]@{
        'mimetype'                 = $asciiNoBom.GetBytes('application/epub+zip')
        'META-INF/container.xml'   = New-ContainerXmlBytes
        'OEBPS/content.opf'        = New-ContentOpfBytes -Item $Item
        'OEBPS/toc.ncx'            = New-TocNcxBytes -Item $Item
    }
    foreach ($chapter in $Item.chapters) {
        $entries["OEBPS/$($chapter.file)"] = New-ChapterXhtmlBytes -Chapter $chapter
    }

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $entries.Keys) {
                $bytes = $entries[$name]
                $level = if ($name -eq 'mimetype') { [System.IO.Compression.CompressionLevel]::NoCompression } else { [System.IO.Compression.CompressionLevel]::Optimal }
                $entry = $zip.CreateEntry($name, $level)
                $entry.LastWriteTime = [DateTimeOffset]::Parse(
                    '1980-01-01T00:00:00Z',
                    [Globalization.CultureInfo]::InvariantCulture)
                $entryStream = $entry.Open()
                try {
                    $entryStream.Write($bytes, 0, $bytes.Length)
                } finally {
                    $entryStream.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fs.Dispose()
    }
}

function Test-EpubFixture {
    param([string]$Path, [int]$ExpectedChapterCount, [string]$ExpectedMimetypeValue, [array]$DistinctivePhrases)

    $result = [ordered]@{
        mimetypeFirstOk    = $false
        mimetypeStoredOk   = $false
        mimetypeValueOk    = $false
        containerOk        = $false
        opfOk              = $false
        chapterCountOk     = $false
        chaptersReadableOk = $false
        phrasesOk          = $true
    }

    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            $entries = $zip.Entries
            if ($entries.Count -gt 0 -and $entries[0].FullName -eq 'mimetype') {
                $result.mimetypeFirstOk = $true
                $mimeEntry = $entries[0]
                $result.mimetypeStoredOk = ($mimeEntry.CompressedLength -eq $mimeEntry.Length)
                $mimeStream = $mimeEntry.Open()
                $reader = New-Object System.IO.StreamReader($mimeStream)
                $mimeText = $reader.ReadToEnd()
                $reader.Dispose()
                $result.mimetypeValueOk = ($mimeText -eq $ExpectedMimetypeValue)
            }

            $containerEntry = $zip.GetEntry('META-INF/container.xml')
            if ($containerEntry) {
                $cStream = $containerEntry.Open()
                $cReader = New-Object System.IO.StreamReader($cStream)
                [xml]$containerXml = $cReader.ReadToEnd()
                $cReader.Dispose()
                $ns = New-Object System.Xml.XmlNamespaceManager($containerXml.NameTable)
                $ns.AddNamespace('c', 'urn:oasis:names:tc:opendocument:xmlns:container')
                $rootfileNode = $containerXml.SelectSingleNode('//c:rootfile/@full-path', $ns)
                if ($rootfileNode) {
                    $opfPath = $rootfileNode.Value
                    $result.containerOk = $true
                    $opfEntry = $zip.GetEntry($opfPath)
                    if ($opfEntry) {
                        $result.opfOk = $true
                        $oStream = $opfEntry.Open()
                        $oReader = New-Object System.IO.StreamReader($oStream)
                        [xml]$opfXml = $oReader.ReadToEnd()
                        $oReader.Dispose()
                        $opfNs = New-Object System.Xml.XmlNamespaceManager($opfXml.NameTable)
                        $opfNs.AddNamespace('o', 'http://www.idpf.org/2007/opf')
                        $itemrefs = $opfXml.SelectNodes('//o:spine/o:itemref', $opfNs)
                        $result.chapterCountOk = ($itemrefs.Count -eq $ExpectedChapterCount)

                        $manifestItems = @{}
                        foreach ($it in $opfXml.SelectNodes('//o:manifest/o:item', $opfNs)) {
                            $manifestItems[$it.id] = $it.href
                        }
                        $opfDir = Split-Path $opfPath -Parent
                        $allReadable = $true
                        $chapterTexts = New-Object System.Collections.Generic.List[string]
                        foreach ($iref in $itemrefs) {
                            $href = $manifestItems[$iref.idref]
                            if (-not $href) { $allReadable = $false; continue }
                            $fullPath = if ($opfDir) { "$opfDir/$href" } else { $href }
                            $chapEntry = $zip.GetEntry($fullPath)
                            if (-not $chapEntry) { $allReadable = $false; continue }
                            $chStream = $chapEntry.Open()
                            $chReader = New-Object System.IO.StreamReader($chStream)
                            $chapterContent = $chReader.ReadToEnd()
                            $chReader.Dispose()
                            if ([string]::IsNullOrWhiteSpace($chapterContent)) { $allReadable = $false }
                            $chapterTexts.Add($chapterContent)
                        }
                        $result.chaptersReadableOk = $allReadable

                        foreach ($dp in $DistinctivePhrases) {
                            if ($dp.phrase) {
                                $found = $false
                                foreach ($ct in $chapterTexts) {
                                    if ($ct.IndexOf($dp.phrase, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $found = $true; break }
                                }
                                if (-not $found) { $result.phrasesOk = $false }
                            }
                        }
                    }
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fs.Dispose()
    }

    $result.success = $result.mimetypeFirstOk -and $result.mimetypeStoredOk -and $result.mimetypeValueOk -and $result.containerOk -and $result.opfOk -and $result.chapterCountOk -and $result.chaptersReadableOk -and $result.phrasesOk
    return $result
}

# ---------------------------------------------------------------------------
# Per-item builders
# ---------------------------------------------------------------------------

function Build-MovieFixture {
    param($Item, [string]$LibraryRoot, $VideoDefaults, [switch]$VerifyOnly)

    Test-MovieDistinctivePhrases -Item $Item

    $folder = Join-Path $LibraryRoot $Item.folder
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $videoPath = Join-Path $folder $Item.videoFile
    $nfoPath = Join-Path $folder $Item.nfoFile
    $srtPath = Join-Path $folder ("$($Item.subtitleBaseName).$($Item.subtitleLanguage).srt")
    $vttPath = Join-Path $folder ("$($Item.subtitleBaseName).$($Item.subtitleLanguage).vtt")

    $existingOk = $false
    if ((Test-Path $videoPath) -and (Test-Path $nfoPath) -and (Test-Path $srtPath) -and (Test-Path $vttPath)) {
        try {
            $probe = Get-FfprobeInfo -Path $videoPath
            $v = Test-VideoAgainstExpectation -Probe $probe -Item $Item -VideoDefaults $VideoDefaults
            $s = Test-SubtitleFiles -SrtPath $srtPath -VttPath $vttPath -ExpectedCueCount $Item.subtitles.expectedCueCount
            $existingOk = $v.success -and $s.success
        } catch {
            $existingOk = $false
        }
    }

    $generated = $false
    if (-not $existingOk) {
        if ($VerifyOnly) {
            throw "Movie fixture '$($Item.id)' failed read-only verification."
        }
        $generated = $true
        Invoke-FfmpegGenerate -OutputPath $videoPath -Pattern $Item.media.pattern -Width $Item.media.width -Height $Item.media.height -Fps $Item.media.fps -DurationSeconds $Item.media.durationSeconds -ToneHz $Item.media.toneHz -VideoDefaults $VideoDefaults
        Write-SrtFile -Path $srtPath -Cues $Item.subtitles.cues
        Write-VttFile -Path $vttPath -Cues $Item.subtitles.cues
        Write-MovieNfo -Path $nfoPath -Item $Item
    }

    $probe = Get-FfprobeInfo -Path $videoPath
    $videoValidation = Test-VideoAgainstExpectation -Probe $probe -Item $Item -VideoDefaults $VideoDefaults
    $subtitleValidation = Test-SubtitleFiles -SrtPath $srtPath -VttPath $vttPath -ExpectedCueCount $Item.subtitles.expectedCueCount
    $nfoExists = Test-Path -LiteralPath $nfoPath

    $success = $videoValidation.success -and $subtitleValidation.success -and $nfoExists

    return [ordered]@{
        id         = $Item.id
        kind       = 'movie'
        paths      = [ordered]@{
            video = ConvertTo-OutputRelativePath $outputRootFullPath $videoPath
            nfo = ConvertTo-OutputRelativePath $outputRootFullPath $nfoPath
            srt = ConvertTo-OutputRelativePath $outputRootFullPath $srtPath
            vtt = ConvertTo-OutputRelativePath $outputRootFullPath $vttPath
        }
        video      = $videoValidation
        subtitles  = $subtitleValidation
        nfoExists  = $nfoExists
        success    = $success
    }
}

function Build-SeriesFixture {
    param($Item, [string]$LibraryRoot, $VideoDefaults, [switch]$VerifyOnly)

    Test-SeriesIsNegativeControl -Item $Item

    $seriesFolder = Join-Path $LibraryRoot $Item.seriesFolder
    $seasonFolder = Join-Path $seriesFolder $Item.seasonFolder
    New-Item -ItemType Directory -Force -Path $seasonFolder | Out-Null

    $tvshowNfoPath = Join-Path $seriesFolder $Item.tvshowNfoFile
    $seasonNfoPath = Join-Path $seasonFolder $Item.seasonNfoFile
    $episodeNfoPath = Join-Path $seasonFolder $Item.episodeNfoFile
    $videoPath = Join-Path $seasonFolder $Item.videoFile
    $srtPath = Join-Path $seasonFolder ("$($Item.episodeBaseName).$($Item.subtitleLanguage).srt")
    $vttPath = Join-Path $seasonFolder ("$($Item.episodeBaseName).$($Item.subtitleLanguage).vtt")

    $existingOk = $false
    if ((Test-Path $videoPath) -and (Test-Path $tvshowNfoPath) -and (Test-Path $seasonNfoPath) -and (Test-Path $episodeNfoPath) -and (Test-Path $srtPath) -and (Test-Path $vttPath)) {
        try {
            $probe = Get-FfprobeInfo -Path $videoPath
            $v = Test-VideoAgainstExpectation -Probe $probe -Item $Item -VideoDefaults $VideoDefaults
            $s = Test-SubtitleFiles -SrtPath $srtPath -VttPath $vttPath -ExpectedCueCount $Item.subtitles.expectedCueCount
            $existingOk = $v.success -and $s.success
        } catch {
            $existingOk = $false
        }
    }

    $generated = $false
    if (-not $existingOk) {
        if ($VerifyOnly) {
            throw "Series fixture '$($Item.id)' failed read-only verification."
        }
        $generated = $true
        Invoke-FfmpegGenerate -OutputPath $videoPath -Pattern $Item.media.pattern -Width $Item.media.width -Height $Item.media.height -Fps $Item.media.fps -DurationSeconds $Item.media.durationSeconds -ToneHz $Item.media.toneHz -VideoDefaults $VideoDefaults
        Write-SrtFile -Path $srtPath -Cues $Item.subtitles.cues
        Write-VttFile -Path $vttPath -Cues $Item.subtitles.cues
        Write-TvShowNfo -Path $tvshowNfoPath -Item $Item
        Write-SeasonNfo -Path $seasonNfoPath -Item $Item
        Write-EpisodeNfo -Path $episodeNfoPath -Item $Item
    }

    $probe = Get-FfprobeInfo -Path $videoPath
    $videoValidation = Test-VideoAgainstExpectation -Probe $probe -Item $Item -VideoDefaults $VideoDefaults
    $subtitleValidation = Test-SubtitleFiles -SrtPath $srtPath -VttPath $vttPath -ExpectedCueCount $Item.subtitles.expectedCueCount
    $nfosExist = (Test-Path $tvshowNfoPath) -and (Test-Path $seasonNfoPath) -and (Test-Path $episodeNfoPath)

    $success = $videoValidation.success -and $subtitleValidation.success -and $nfosExist

    return [ordered]@{
        id        = $Item.id
        kind      = 'series'
        paths     = [ordered]@{
            video = ConvertTo-OutputRelativePath $outputRootFullPath $videoPath
            tvshowNfo = ConvertTo-OutputRelativePath $outputRootFullPath $tvshowNfoPath
            seasonNfo = ConvertTo-OutputRelativePath $outputRootFullPath $seasonNfoPath
            episodeNfo = ConvertTo-OutputRelativePath $outputRootFullPath $episodeNfoPath
            srt = ConvertTo-OutputRelativePath $outputRootFullPath $srtPath
            vtt = ConvertTo-OutputRelativePath $outputRootFullPath $vttPath
        }
        video     = $videoValidation
        subtitles = $subtitleValidation
        nfosExist = $nfosExist
        success   = $success
    }
}

function Build-PdfFixture {
    param($Item, [string]$LibraryRoot, [switch]$VerifyOnly)

    $folder = Join-Path $LibraryRoot $Item.folder
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $pdfPath = Join-Path $folder $Item.pdfFile
    $opfPath = Join-Path $folder $Item.opfFile

    $existingOk = $false
    if ((Test-Path $pdfPath) -and (Test-Path $opfPath)) {
        try {
            $chk = Test-PdfFixture -Path $pdfPath -ExpectedPageCount $Item.expected.pageCount -ExpectedHeaderMarker $Item.expected.headerMarker -ExpectedFooterMarker $Item.expected.footerMarker -DistinctivePhrases $Item.distinctivePhrases
            $existingOk = $chk.success
        } catch {
            $existingOk = $false
        }
    }

    $generated = $false
    if (-not $existingOk) {
        if ($VerifyOnly) {
            throw "PDF fixture '$($Item.id)' failed read-only verification."
        }
        $generated = $true
        $bytes = New-MinimalPdfBytes -Pages $Item.pages
        [System.IO.File]::WriteAllBytes($pdfPath, $bytes)
        Write-BookOpf -Path $opfPath -Item $Item -MediaFile $Item.pdfFile -MediaType 'application/pdf'
    }

    $structure = Test-PdfFixture -Path $pdfPath -ExpectedPageCount $Item.expected.pageCount -ExpectedHeaderMarker $Item.expected.headerMarker -ExpectedFooterMarker $Item.expected.footerMarker -DistinctivePhrases $Item.distinctivePhrases
    $opfExists = Test-Path -LiteralPath $opfPath
    $success = $structure.success -and $opfExists

    return [ordered]@{
        id        = $Item.id
        kind      = 'pdf'
        paths     = [ordered]@{
            pdf = ConvertTo-OutputRelativePath $outputRootFullPath $pdfPath
            opf = ConvertTo-OutputRelativePath $outputRootFullPath $opfPath
        }
        structure = $structure
        opfExists = $opfExists
        success   = $success
    }
}

function Build-EpubFixture {
    param($Item, [string]$LibraryRoot, [switch]$VerifyOnly)

    $folder = Join-Path $LibraryRoot $Item.folder
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $epubPath = Join-Path $folder $Item.epubFile
    $opfPath = Join-Path $folder $Item.opfFile

    $existingOk = $false
    if ((Test-Path $epubPath) -and (Test-Path $opfPath)) {
        try {
            $chk = Test-EpubFixture -Path $epubPath -ExpectedChapterCount $Item.expected.chapterCount -ExpectedMimetypeValue $Item.expected.mimetypeValue -DistinctivePhrases $Item.distinctivePhrases
            $existingOk = $chk.success
        } catch {
            $existingOk = $false
        }
    }

    $generated = $false
    if (-not $existingOk) {
        if ($VerifyOnly) {
            throw "EPUB fixture '$($Item.id)' failed read-only verification."
        }
        $generated = $true
        New-MinimalEpubFile -Path $epubPath -Item $Item
        Write-BookOpf -Path $opfPath -Item $Item -MediaFile $Item.epubFile -MediaType 'application/epub+zip'
    }

    $structure = Test-EpubFixture -Path $epubPath -ExpectedChapterCount $Item.expected.chapterCount -ExpectedMimetypeValue $Item.expected.mimetypeValue -DistinctivePhrases $Item.distinctivePhrases
    $opfExists = Test-Path -LiteralPath $opfPath
    $success = $structure.success -and $opfExists

    return [ordered]@{
        id        = $Item.id
        kind      = 'epub'
        paths     = [ordered]@{
            epub = ConvertTo-OutputRelativePath $outputRootFullPath $epubPath
            opf = ConvertTo-OutputRelativePath $outputRootFullPath $opfPath
        }
        structure = $structure
        opfExists = $opfExists
        success   = $success
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$manifestFullPath = Resolve-FullPath $ManifestPath
if (-not (Test-Path -LiteralPath $manifestFullPath)) {
    throw "Manifest not found at '$manifestFullPath'"
}
$manifest = Get-Content -Raw -LiteralPath $manifestFullPath | ConvertFrom-Json
$requestedOutputRoot = Resolve-FullPath $OutputRoot
$canonicalOutputRoot = Resolve-FullPath (Join-Path (Split-Path -Parent $manifestFullPath) 'generated')
$checksumsPath = Join-Path (Split-Path -Parent $manifestFullPath) $manifest.canonicalChecksumsFile
$isVerify = $Mode -eq 'Verify'
$isUpdate = $Mode -eq 'UpdateChecksums'
if ($isUpdate -and $requestedOutputRoot -ne $canonicalOutputRoot) {
    throw "UpdateChecksums must target the canonical output '$canonicalOutputRoot'."
}
if ($Mode -eq 'Generate' -and $requestedOutputRoot -eq $canonicalOutputRoot) {
    throw "Use UpdateChecksums, not Generate, to replace the canonical corpus."
}

$outputRootFullPath = if ($isUpdate) {
    "$canonicalOutputRoot.update-$([Guid]::NewGuid().ToString('N'))"
} else {
    $requestedOutputRoot
}
$updateCommitted = $false

try {
    Write-Host "Generate-Fixtures: mode='$Mode' manifest='$manifestFullPath' outputRoot='$outputRootFullPath'"

    if ($isVerify) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputRootFullPath 'library'))) {
            throw "Fixture library not found at '$outputRootFullPath'."
        }
        $existingHashes = Get-CanonicalHashes -Root $outputRootFullPath
        if (-not (Test-CanonicalHashes -Actual $existingHashes -ChecksumsPath $checksumsPath)) {
            throw "Fixture bytes do not match '$checksumsPath'; verification never rewrites them."
        }
    } else {
        Assert-GeneratorToolchain -Toolchain $manifest.generatorToolchain
        Reset-OutputRoot `
            -Path $outputRootFullPath `
            -GeneratorIdentity $manifest.generatorIdentity
    }

    $libraryRoot = Join-Path $outputRootFullPath 'library'
    if (-not $isVerify) {
        New-Item -ItemType Directory -Force -Path $libraryRoot | Out-Null
    }

    $results = New-Object System.Collections.Generic.List[object]
    $overallSuccess = $true
    foreach ($item in $manifest.items) {
        Write-Host "Processing '$($item.id)' (kind=$($item.kind))..."
        $r = switch ($item.kind) {
            'movie' {
                Build-MovieFixture -Item $item -LibraryRoot $libraryRoot `
                    -VideoDefaults $manifest.video -VerifyOnly:$isVerify
            }
            'series' {
                Build-SeriesFixture -Item $item -LibraryRoot $libraryRoot `
                    -VideoDefaults $manifest.video -VerifyOnly:$isVerify
            }
            'pdf' {
                Build-PdfFixture -Item $item -LibraryRoot $libraryRoot `
                    -VerifyOnly:$isVerify
            }
            'epub' {
                Build-EpubFixture -Item $item -LibraryRoot $libraryRoot `
                    -VerifyOnly:$isVerify
            }
            default {
                throw "Unknown fixture kind '$($item.kind)' for item '$($item.id)'"
            }
        }
        $results.Add($r)
        $status = if ($r.success) { 'OK' } else { 'FAILED' }
        Write-Host "  -> $status"
        if (-not $r.success) {
            $overallSuccess = $false
        }
    }

    $generatedHashes = Get-CanonicalHashes -Root $outputRootFullPath
    $canonicalHashesMatch = if ($isUpdate) {
        $true
    } else {
        Test-CanonicalHashes -Actual $generatedHashes -ChecksumsPath $checksumsPath
    }
    $overallSuccess = $overallSuccess -and $canonicalHashesMatch
    if (-not $overallSuccess) {
        throw "One or more fixtures failed validation; canonical checksums were not changed."
    }

    if (-not $isVerify) {
        $actualManifest = [ordered]@{
            generatorOwnedOutput  = $true
            manifestVersion       = $manifest.manifestVersion
            generatorIdentity     = $manifest.generatorIdentity
            manifestSha256        = Get-NormalizedTextSha256 $manifestFullPath
            generatorScriptSha256 = Get-NormalizedTextSha256 $PSCommandPath
            canonicalHashesMatch  = $canonicalHashesMatch
            overallSuccess        = $overallSuccess
            items                 = $results
        }
        $actualPath = Join-Path $outputRootFullPath 'manifest.actual.json'
        $actualJson = ($actualManifest | ConvertTo-Json -Depth 20).Replace("`r`n", "`n") + "`n"
        [System.IO.File]::WriteAllText(
            $actualPath,
            $actualJson,
            (New-Utf8NoBomEncoding))
    }

    if ($isUpdate) {
        $temporaryChecksums = "$checksumsPath.$([Guid]::NewGuid().ToString('N')).tmp"
        $backupOutput = "$canonicalOutputRoot.backup-$([Guid]::NewGuid().ToString('N'))"
        $movedOriginal = $false
        $movedReplacement = $false
        try {
            Write-CanonicalHashes -Hashes $generatedHashes -Path $temporaryChecksums
            if (Test-Path -LiteralPath $canonicalOutputRoot) {
                [System.IO.Directory]::Move($canonicalOutputRoot, $backupOutput)
                $movedOriginal = $true
            }
            [System.IO.Directory]::Move($outputRootFullPath, $canonicalOutputRoot)
            $movedReplacement = $true
            [System.IO.File]::Move($temporaryChecksums, $checksumsPath, $true)
            $updateCommitted = $true
        } catch {
            if ($movedReplacement -and (Test-Path -LiteralPath $canonicalOutputRoot)) {
                Remove-Item -LiteralPath $canonicalOutputRoot -Recurse -Force
            }
            if ($movedOriginal -and (Test-Path -LiteralPath $backupOutput)) {
                [System.IO.Directory]::Move($backupOutput, $canonicalOutputRoot)
            }
            throw
        } finally {
            if (Test-Path -LiteralPath $temporaryChecksums) {
                Remove-Item -LiteralPath $temporaryChecksums -Force
            }
        }
        if (Test-Path -LiteralPath $backupOutput) {
            Remove-Item -LiteralPath $backupOutput -Recurse -Force
        }
        Write-Host "Canonical fixtures and checksums replaced after full validation."
    } elseif ($isVerify) {
        Write-Host "All canonical fixtures passed read-only verification."
    } else {
        Write-Host "All fixtures generated and matched canonical checksums."
    }
} finally {
    if ($isUpdate -and -not $updateCommitted -and
        (Test-Path -LiteralPath $outputRootFullPath)) {
        Remove-Item -LiteralPath $outputRootFullPath -Recurse -Force
    }
}
