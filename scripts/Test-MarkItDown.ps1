#requires -version 7.0
[CmdletBinding()]
param(
    [string] $ImageTag = "jellyfin-semantic-markitdown:smoke"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fixtureRoot = Join-Path $repoRoot "assets\fixtures\generated\library\Books"

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

function Invoke-Mcp {
    param(
        [int] $Port,
        [string] $Source,
        [int] $Id
    )

    $body = @{
        jsonrpc = "2.0"
        id = $Id
        method = "tools/call"
        params = @{
            name = "convert_to_markdown"
            arguments = @{ source = $Source }
        }
    } | ConvertTo-Json -Depth 6 -Compress

    return Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:$Port/mcp" `
        -ContentType "application/json" `
        -Body $body
}

function Invoke-RawHttp {
    param(
        [int] $Port,
        [string] $Request
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 5000
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Request)
        $stream.Write($bytes, 0, $bytes.Length)
        $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::ASCII,
            $false,
            1024,
            $true)
        try {
            return $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
    } finally {
        $client.Dispose()
    }
}

function Invoke-SlowRawHttp {
    param(
        [int] $Port,
        [string] $Request
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 5000
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Request)
        $stream.Write($bytes, 0, $bytes.Length)
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.Encoding]::ASCII,
            $false,
            1024,
            $true)
        try {
            return $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }
    } finally {
        $client.Dispose()
    }
}

function Send-AndDisconnect {
    param(
        [int] $Port,
        [string] $Request
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($Request)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally {
        $client.Dispose()
    }
}

function ConvertTo-DataUri {
    param([string] $Path, [string] $MediaType)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return "data:$MediaType;base64,$([Convert]::ToBase64String($bytes))"
}

function New-EpubContainerDataUri {
    param([string] $ContainerXml)

    $memory = [System.IO.MemoryStream]::new()
    $zip = [System.IO.Compression.ZipArchive]::new(
        $memory,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $true)
    try {
        $mimetype = $zip.CreateEntry(
            "mimetype",
            [System.IO.Compression.CompressionLevel]::NoCompression)
        $mimetype.LastWriteTime = [DateTimeOffset]::Parse(
            "1980-01-01T00:00:00Z",
            [Globalization.CultureInfo]::InvariantCulture)
        $writer = [System.IO.StreamWriter]::new(
            $mimetype.Open(),
            [System.Text.UTF8Encoding]::new($false))
        try {
            $writer.Write("application/epub+zip")
        } finally {
            $writer.Dispose()
        }

        $container = $zip.CreateEntry("META-INF/container.xml")
        $container.LastWriteTime = $mimetype.LastWriteTime
        $writer = [System.IO.StreamWriter]::new(
            $container.Open(),
            [System.Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($ContainerXml)
        } finally {
            $writer.Dispose()
        }
    } finally {
        $zip.Dispose()
    }

    try {
        $bytes = $memory.ToArray()
        return "data:application/epub+zip;base64,$([Convert]::ToBase64String($bytes))"
    } finally {
        $memory.Dispose()
    }
}

function New-ParserControlledEpubDataUri {
    param([string] $Marker)

    return New-EpubContainerDataUri (
        "<?xml version=`"1.0`" encoding=`"UTF-8`"?>" +
        "<container version=`"1.0`" xmlns=`"urn:oasis:names:tc:opendocument:xmlns:container`">" +
        "<rootfiles><rootfile full-path=`"$Marker`" media-type=`"application/oebps-package+xml`"/>" +
        "</rootfiles></container>")
}

function ConvertTo-UnsupportedCompressionEpubDataUri {
    param([string] $DataUri)

    $bytes = [Convert]::FromBase64String($DataUri.Split(",", 2)[1])
    for ($index = 0; $index -le $bytes.Length - 12; $index++) {
        if ($bytes[$index] -eq 0x50 -and
            $bytes[$index + 1] -eq 0x4B -and
            $bytes[$index + 2] -eq 0x03 -and
            $bytes[$index + 3] -eq 0x04) {
            $bytes[$index + 8] = 99
            $bytes[$index + 9] = 0
        } elseif ($bytes[$index] -eq 0x50 -and
            $bytes[$index + 1] -eq 0x4B -and
            $bytes[$index + 2] -eq 0x01 -and
            $bytes[$index + 3] -eq 0x02) {
            $bytes[$index + 10] = 99
            $bytes[$index + 11] = 0
        }
    }

    return "data:application/epub+zip;base64,$([Convert]::ToBase64String($bytes))"
}

Push-Location $repoRoot
$containerId = $null
try {
    & (Join-Path $PSScriptRoot "Generate-Fixtures.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture generation or checksum validation failed."
    }

    docker build --tag $ImageTag (Join-Path $repoRoot "containers\markitdown")
    if ($LASTEXITCODE -ne 0) {
        throw "MarkItDown image build failed."
    }

    $port = Get-FreePort
    $containerId = docker run `
        --detach `
        --publish "127.0.0.1:${port}:8080" `
        --env "BODY_READ_TIMEOUT_SECONDS=1" `
        --env "HEADER_READ_TIMEOUT_SECONDS=1" `
        $ImageTag
    if ($LASTEXITCODE -ne 0 -or -not $containerId) {
        throw "MarkItDown container failed to start."
    }
    $containerId = $containerId.Trim()

    $lastError = "no response"
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2
            if ($health.status -eq "ok") {
                $lastError = $null
                break
            }
        } catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 1
    }
    if ($lastError) {
        throw "MarkItDown health check failed: $lastError"
    }
    $egress = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$port/egress-canary"
    if ($egress.blocked -ne $true) {
        throw "MarkItDown outbound-canary access was not blocked."
    }
    $expectedEgressProbes = @(
        "getaddrinfo",
        "gethostbyname",
        "gethostbyname_ex",
        "gethostbyaddr",
        "getnameinfo",
        "create_connection",
        "socket_connect",
        "socket_connect_ex",
        "socket_sendto",
        "socket_sendmsg")
    foreach ($probe in $expectedEgressProbes) {
        if ($egress.probes.$probe -ne $true) {
            throw "MarkItDown direct-socket egress probe '$probe' was not blocked."
        }
    }
    $capacityJson = docker exec $containerId python -c (
        "import json,server;" +
        "held=[server._conversion_slots.acquire(False) for _ in range(server.MAX_CONCURRENT_CONVERSIONS)];" +
        "print(json.dumps(server._handle_tools_call(99,{'name':server.TOOL_NAME,'arguments':{'source':'blocked-before-parse'}})));" +
        "[server._conversion_slots.release() for acquired in held if acquired]")
    if ($LASTEXITCODE -ne 0) {
        throw "MarkItDown capacity control probe failed to execute."
    }
    $capacity = $capacityJson | ConvertFrom-Json
    if ($capacity.error.code -ne -32001) {
        throw "MarkItDown capacity exhaustion did not report -32001."
    }
    $infrastructureJson = docker exec $containerId python -c (
        "import base64,json,server;" +
        "from markitdown import FileConversionException,FailedConversionAttempt;" +
        "failure=FileConversionException(attempts=[FailedConversionAttempt(None,(OSError,OSError('disk full'),None))]);" +
        "server._markitdown_instance=type('FailingConverter',(),{'convert':lambda self,path:(_ for _ in ()).throw(failure)})();" +
        "source='data:application/pdf;base64,'+base64.b64encode(b'%PDF-1.7').decode();" +
        "print(json.dumps(server._handle_tools_call(100,{'name':server.TOOL_NAME,'arguments':{'source':source}})))")
    if ($LASTEXITCODE -ne 0) {
        throw "MarkItDown infrastructure taxonomy probe failed to execute."
    }
    $infrastructure = $infrastructureJson | ConvertFrom-Json
    if ($infrastructure.error.code -ne -32603) {
        throw "MarkItDown infrastructure failure did not remain transient/internal."
    }

    $pdfPath = Join-Path $fixtureRoot "Field Notes on Static (2024)\Field Notes on Static (2024).pdf"
    $epubPath = Join-Path $fixtureRoot "Notes from the Choir Loft (2024)\Notes from the Choir Loft (2024).epub"
    $pdf = Invoke-Mcp $port (ConvertTo-DataUri $pdfPath "application/pdf") 1
    $epub = Invoke-Mcp $port (ConvertTo-DataUri $epubPath "application/epub+zip") 2
    $pdfText = ($pdf.result.content | ForEach-Object text) -join "`n"
    $epubText = ($epub.result.content | ForEach-Object text) -join "`n"
    if ($pdfText -notmatch "marigold ledger returns before the frost") {
        throw "PDF conversion omitted its distinctive phrase."
    }
    if ($epubText -notmatch "seventeen lanterns of quiet static gather at the loft door") {
        throw "EPUB conversion omitted its distinctive phrase."
    }

    $localPath = Invoke-Mcp $port "/etc/hostname" 3
    $fileUri = Invoke-Mcp $port "file:///etc/hostname" 4
    $loopbackUrl = Invoke-Mcp $port "http://127.0.0.1:8080/health" 5
    $httpsUrl = Invoke-Mcp $port "https://example.com/private" 6
    $privateUrl = Invoke-Mcp $port "http://10.0.0.1/private" 7
    if ($localPath.error.code -ne -32602 -or
        $fileUri.error.code -ne -32602 -or
        $loopbackUrl.error.code -ne -32602 -or
        $httpsUrl.error.code -ne -32602 -or
        $privateUrl.error.code -ne -32602) {
        throw "A local-path, file URI, HTTPS, or private-network source was not rejected."
    }

    $secretMarker = "PARSER_CONTROLLED_SECRET_ROOTFILE_MARKER"
    $invalidData = New-ParserControlledEpubDataUri $secretMarker
    $encodedMarker = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($secretMarker))
    if ([string]::IsNullOrWhiteSpace($encodedMarker) -or
        [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($encodedMarker)) -ne $secretMarker) {
        throw "The parser-controlled marker Base64 control is invalid."
    }
    $completePayload = $invalidData.Split(",", 2)[1]
    $invalid = Invoke-Mcp $port $invalidData 8
    if ($invalid.error.code -ne -32002) {
        throw "Invalid-document negative control did not report a conversion error."
    }
    $malformedEpubData = New-EpubContainerDataUri (
        "<?xml version=`"1.0`" encoding=`"UTF-8`"?>" +
        "<container xmlns=`"urn:oasis:names:tc:opendocument:xmlns:container`"><rootfiles>")
    $missingRootfileData = New-EpubContainerDataUri (
        "<?xml version=`"1.0`" encoding=`"UTF-8`"?>" +
        "<container version=`"1.0`" xmlns=`"urn:oasis:names:tc:opendocument:xmlns:container`">" +
        "<rootfiles /></container>")
    $malformedEpub = Invoke-Mcp $port $malformedEpubData 9
    $missingRootfile = Invoke-Mcp $port $missingRootfileData 10
    $unsupportedCompressionData =
        ConvertTo-UnsupportedCompressionEpubDataUri $missingRootfileData
    $unsupportedCompression =
        Invoke-Mcp $port $unsupportedCompressionData 11
    if ($malformedEpub.error.code -ne -32002 -or
        $missingRootfile.error.code -ne -32002 -or
        $unsupportedCompression.error.code -ne -32002) {
        throw "Malformed EPUB XML, missing rootfile, or unsupported ZIP compression did not report permanent conversion failure."
    }
    $corruptPdfPath = Join-Path $repoRoot ".data\corrupt-markitdown.pdf"
    $malformedEpubPath = Join-Path $repoRoot ".data\malformed-markitdown.epub"
    $missingRootfilePath = Join-Path $repoRoot ".data\missing-rootfile-markitdown.epub"
    $unsupportedCompressionPath =
        Join-Path $repoRoot ".data\unsupported-compression-markitdown.epub"
    [IO.File]::WriteAllBytes(
        $corruptPdfPath,
        [Text.Encoding]::ASCII.GetBytes("%PDF-1.7`n%%EOF"))
    [IO.File]::WriteAllBytes(
        $malformedEpubPath,
        [Convert]::FromBase64String($malformedEpubData.Split(",", 2)[1]))
    [IO.File]::WriteAllBytes(
        $missingRootfilePath,
        [Convert]::FromBase64String($missingRootfileData.Split(",", 2)[1]))
    [IO.File]::WriteAllBytes(
        $unsupportedCompressionPath,
        [Convert]::FromBase64String(
            $unsupportedCompressionData.Split(",", 2)[1]))
    foreach ($probe in @(
            @{ Path = $corruptPdfPath; Kind = "pdf" },
            @{ Path = $malformedEpubPath; Kind = "epub" },
            @{ Path = $missingRootfilePath; Kind = "epub" },
            @{ Path = $unsupportedCompressionPath; Kind = "epub" })) {
        $probeOutput = & dotnet run `
            --project src\Jellyfin.SemanticDiscovery.Setup `
            --no-restore `
            -- markitdown-probe `
            --endpoint "http://127.0.0.1:$port/mcp" `
            --document $probe.Path `
            --kind $probe.Kind 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or
            $probeOutput -notmatch "MARKITDOWN_PROBE_PERMANENT_FAILURE") {
            throw "The live .NET reader did not classify corrupt $($probe.Kind) as permanent.`n$probeOutput"
        }
    }
    $directMarkerData =
        "data:application/octet-stream;base64,$encodedMarker"
    if (-not $directMarkerData.Contains(
            $encodedMarker,
            [StringComparison]::Ordinal)) {
        throw "The exact marker Base64 is not present in its submitted control source."
    }
    $directMarker = Invoke-Mcp $port $directMarkerData 12
    if ($directMarker.error.code -ne -32602) {
        throw "Direct marker invalid-media control was not rejected."
    }
    $missingLength = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`n`r`n")
    $nonNumericLength = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: nope`r`n`r`n")
    $negativeLength = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: -1`r`n`r`n")
    $chunked = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nTransfer-Encoding: chunked`r`n`r`n0`r`n`r`n")
    $duplicateLength = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: 0`r`nContent-Length: 0`r`n`r`n")
    $querySecret = "QUERY_SECRET_MUST_NOT_BE_LOGGED"
    $queryRejected = Invoke-RawHttp $port (
        "POST /mcp?token=$querySecret HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: 0`r`n`r`n")
    $truncated = Invoke-RawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: 10`r`n`r`n{}")
    $timedOut = Invoke-SlowRawHttp $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: 10`r`n`r`n")
    $slowHeaderSecret = "SLOW_HEADER_SECRET_MUST_NOT_BE_LOGGED"
    $slowHeaders = Invoke-SlowRawHttp $port (
        "POST /mcp HTTP/1.0`r`nX-Demo: $slowHeaderSecret")
    Send-AndDisconnect $port (
        "POST /mcp HTTP/1.0`r`nHost: 127.0.0.1`r`nContent-Length: 100`r`n`r`n{}")
    Start-Sleep -Milliseconds 250
    if (-not $missingLength.StartsWith("HTTP/1.0 411", [StringComparison]::Ordinal) -or
        -not $nonNumericLength.StartsWith("HTTP/1.0 400", [StringComparison]::Ordinal) -or
        -not $negativeLength.StartsWith("HTTP/1.0 400", [StringComparison]::Ordinal) -or
        -not $chunked.StartsWith("HTTP/1.0 411", [StringComparison]::Ordinal) -or
        -not $duplicateLength.StartsWith("HTTP/1.0 411", [StringComparison]::Ordinal) -or
        -not $queryRejected.StartsWith("HTTP/1.0 400", [StringComparison]::Ordinal) -or
        -not $truncated.StartsWith("HTTP/1.0 400", [StringComparison]::Ordinal) -or
        -not $timedOut.StartsWith("HTTP/1.0 408", [StringComparison]::Ordinal) -or
        -not [string]::IsNullOrEmpty($slowHeaders)) {
        throw "Malformed or unbounded HTTP request framing was not rejected."
    }
    $stdoutPath = Join-Path $repoRoot ".data\markitdown-stdout.log"
    $stderrPath = Join-Path $repoRoot ".data\markitdown-stderr.log"
    docker logs $containerId 1> $stdoutPath 2> $stderrPath
    $stdoutLogs = Get-Content -Raw -LiteralPath $stdoutPath
    $stderrLogs = Get-Content -Raw -LiteralPath $stderrPath
    $logs = $stdoutLogs + $stderrLogs
    if ($logs.Contains($secretMarker, [StringComparison]::Ordinal) -or
        $logs.Contains($encodedMarker, [StringComparison]::Ordinal) -or
        $logs.Contains($completePayload, [StringComparison]::Ordinal) -or
        $logs.Contains($invalidData, [StringComparison]::Ordinal) -or
        $logs.Contains($directMarkerData, [StringComparison]::Ordinal) -or
        $logs.Contains($querySecret, [StringComparison]::Ordinal) -or
        $logs.Contains($slowHeaderSecret, [StringComparison]::Ordinal)) {
        throw "MarkItDown logged sensitive document content."
    }
    if ($logs -notmatch "source_kind=data.*decoded_size=.*sha256=.*exception_type=" -or
        $logs -match "Traceback") {
        throw "MarkItDown did not emit the expected non-sensitive failure metadata."
    }
    if ($stdoutLogs -notmatch "conversion completed" -or
        $stderrLogs -match "conversion completed") {
        throw "Successful MarkItDown INFO records were not isolated to stdout."
    }

    Write-Host "MarkItDown conversion, source-boundary, framing, and logging controls passed."
} finally {
    if ($containerId) {
        docker rm --force $containerId | Out-Null
    }
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\markitdown-stdout.log") `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\markitdown-stderr.log") `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\corrupt-markitdown.pdf") `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\malformed-markitdown.epub") `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\missing-rootfile-markitdown.epub") `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot ".data\unsupported-compression-markitdown.epub") `
        -Force -ErrorAction SilentlyContinue
    Pop-Location
}
