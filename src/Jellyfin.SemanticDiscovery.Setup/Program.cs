using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.Setup;
using Jellyfin.SemanticDiscovery.Setup.Showcase;

return await RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

static async Task<int> RunAsync(
    string[] args,
    TextWriter output,
    TextWriter error,
    CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        await output.WriteLineAsync(
            "Usage: setup model|plugin|smoke|markitdown-probe|jellyfin|showcase|showcase-acquisition-evidence|showcase-evidence|showcase-evidence-manifest|showcase-lock-hashes|showcase-purge|showcase-runtime-ensure|showcase-runtime-purge [options]");
        return 0;
    }

    string modelDirectory = GetPathOption(
        args,
        "--model-dir",
        Path.Combine(Environment.CurrentDirectory, ".data", "models", "all-MiniLM-L6-v2"));
    string manifestPath = GetPathOption(
        args,
        "--manifest",
        Path.Combine(AppContext.BaseDirectory, "assets", "model-manifest.json"));

    try
    {
        using HttpClient httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        ModelManifest manifest = await ModelInstaller.LoadManifestAsync(
            manifestPath,
            cancellationToken);

        switch (args[0])
        {
            case "model":
                await new ModelInstaller(httpClient).InstallAsync(
                    manifest,
                    modelDirectory,
                    cancellationToken);
                return 0;

            case "smoke":
                await new ModelInstaller(httpClient).InstallAsync(
                    manifest,
                    modelDirectory,
                    cancellationToken);
                using (MiniLmEmbeddingGenerator generator = new(
                           new MiniLmEmbeddingOptions
                           {
                               ModelPath = Path.Combine(modelDirectory, "model.onnx"),
                               VocabularyPath = Path.Combine(modelDirectory, "vocab.txt"),
                               ModelId = $"{manifest.Repository}@{manifest.Revision}",
                           }))
                {
                    MiniLmParityResult parity =
                        await MiniLmParityVerifier.VerifyAsync(
                            generator,
                            cancellationToken);
                    await output.WriteLineAsync(
                        "SEMANTIC_DISCOVERY_ONNX_SMOKE_OK " +
                        $"dimensions={parity.Dimensions} " +
                        $"max_abs_error={parity.MaximumAbsoluteError:E3} " +
                        $"max_cosine_error={parity.MaximumCosineError:E3} " +
                        $"max_norm_error={parity.MaximumNormError:E3}");
                }

                return 0;

            case "plugin":
                PluginInstaller.Install(
                    GetRequiredOption(args, "--source"),
                    GetRequiredOption(args, "--destination"));
                await output.WriteLineAsync("Plugin files installed.");
                return 0;

            case "markitdown-probe":
                string documentPath =
                    GetRequiredOption(args, "--document");
                string kindValue = GetRequiredStringOption(args, "--kind");
                ContentSourceKind documentKind = kindValue switch
                {
                    "pdf" => ContentSourceKind.Pdf,
                    "epub" => ContentSourceKind.Epub,
                    _ => throw new ArgumentException(
                        "Option '--kind' must be 'pdf' or 'epub'."),
                };
                using (HttpClient probeClient =
                       MarkItDownMcpReader.CreateHttpClient(
                           TimeSpan.FromSeconds(30)))
                using (MarkItDownMcpReader reader = new(
                           probeClient,
                           new Uri(
                               GetRequiredStringOption(args, "--endpoint"),
                               UriKind.Absolute),
                           new ProbeTokenCounter()))
                {
                    try
                    {
                        _ = await reader.ReadAsync(
                            new ContentSourceDescriptor
                            {
                                OwnerId = Guid.Empty,
                                Kind = documentKind,
                                Path = documentPath,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (ContentIngestionException exception)
                        when (!exception.IsTransient)
                    {
                        await output.WriteLineAsync(
                            "MARKITDOWN_PROBE_PERMANENT_FAILURE");
                        return 0;
                    }
                    catch (ContentIngestionException exception)
                    {
                        throw new InvalidDataException(
                            "The live MarkItDown client classified the " +
                            "corrupt document as transient.",
                            exception);
                    }
                }

                throw new InvalidDataException(
                    "The corrupt document unexpectedly converted.");

            case "jellyfin":
                string jellyfinUrl = GetStringOption(
                    args,
                    "--jellyfin-url",
                    Environment.GetEnvironmentVariable("JELLYFIN_URL")
                    ?? "http://127.0.0.1:8096");
                await new JellyfinBootstrapper(
                        httpClient,
                        new JellyfinBootstrapOptions
                        {
                            BaseAddress = new Uri(
                                jellyfinUrl.TrimEnd('/') + '/',
                                UriKind.Absolute),
                            AdminUserName = GetStringOption(
                                args,
                                "--admin-user",
                                Environment.GetEnvironmentVariable("JELLYFIN_ADMIN_USER")
                                ?? "semantic-admin"),
                            AdminPassword = GetStringOption(
                                args,
                                "--admin-password",
                                Environment.GetEnvironmentVariable("JELLYFIN_ADMIN_PASSWORD")
                                ?? throw new InvalidOperationException(
                                    "JELLYFIN_ADMIN_PASSWORD is required.")),
                            CredentialsPath = Environment.GetEnvironmentVariable(
                                "JELLYFIN_CREDENTIALS_PATH"),
                            LibraryManifestPath =
                                Environment.GetEnvironmentVariable(
                                    "JELLYFIN_LIBRARY_MANIFEST"),
                            ReadyTimeout = TimeSpan.FromSeconds(
                                int.Parse(
                                    Environment.GetEnvironmentVariable(
                                        "JELLYFIN_READY_TIMEOUT_SECONDS")
                                    ?? "180",
                                    System.Globalization.CultureInfo.InvariantCulture)),
                        })
                    .BootstrapAsync(cancellationToken)
                    .ConfigureAwait(false);
                return 0;

            case "showcase":
                string sourceLockPath = GetPathOption(
                    args,
                    "--source-lock",
                    Path.Combine(
                        Environment.CurrentDirectory,
                        "assets",
                        "showcase",
                        "sources.lock.json"));
                string showcaseOutput = GetPathOption(
                    args,
                    "--output",
                    Path.Combine(
                        Environment.CurrentDirectory,
                        ".data",
                        "showcase"));
                ShowcaseSourceLock sourceLock =
                    await ShowcaseSourceLock.LoadAsync(
                        sourceLockPath,
                        cancellationToken);
                await output.WriteLineAsync(
                    $"SHOWCASE_DOWNLOAD_BYTES {sourceLock.SelectedDownloadBytes}");
                using (HttpClient showcaseClient =
                       ShowcaseInstaller.CreateHttpClient())
                {
                    ShowcaseInstallResult result =
                        await new ShowcaseInstaller(showcaseClient)
                            .InstallAsync(
                                sourceLock,
                                showcaseOutput,
                                cancellationToken);
                    await output.WriteLineAsync(
                        $"SHOWCASE_READY artifacts={result.Artifacts.Count} " +
                        $"downloaded={result.Artifacts.Count(static item => !item.Reused)} " +
                        $"reused={result.Artifacts.Count(static item => item.Reused)}");
                }
                return 0;

            case "showcase-evidence":
                await ShowcaseEvidenceWriter.WriteAsync(
                    GetRequiredOption(args, "--source-lock"),
                    GetRequiredOption(args, "--snapshot"),
                    GetRequiredOption(args, "--showcase-root"),
                    GetRequiredOption(args, "--model-dir"),
                    GetRequiredOption(args, "--output"),
                    float.Parse(
                        GetRequiredStringOption(
                            args,
                            "--recommendation-floor"),
                        System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken);
                await output.WriteLineAsync(
                    "SHOWCASE_RETRIEVAL_EVIDENCE_WRITTEN");
                return 0;

            case "showcase-acquisition-evidence":
                await ShowcaseAcquisitionEvidenceWriter.WriteAsync(
                    GetRequiredOption(args, "--source-lock"),
                    GetRequiredOption(args, "--actual"),
                    GetRequiredStringOption(args, "--phase"),
                    GetRequiredOption(args, "--output"),
                    cancellationToken);
                await output.WriteLineAsync(
                    "SHOWCASE_ACQUISITION_EVIDENCE_WRITTEN");
                return 0;

            case "showcase-evidence-manifest":
                await ShowcaseEvidenceManifestWriter.WriteAsync(
                    GetRequiredOption(args, "--repository-root"),
                    GetRequiredOption(args, "--evidence-root"),
                    GetRequiredStringOption(args, "--base-commit"),
                    GetRequiredOption(args, "--output"),
                    cancellationToken);
                await output.WriteLineAsync(
                    "SHOWCASE_EVIDENCE_MANIFEST_WRITTEN");
                return 0;

            case "showcase-lock-hashes":
                ShowcaseSourceLock hashLock =
                    await ShowcaseSourceLock.LoadUncheckedAsync(
                        GetRequiredOption(args, "--source-lock"),
                        cancellationToken);
                foreach (ShowcaseItem item in hashLock.Items)
                {
                    byte[] metadata = item.JellyfinType == "Movie"
                        ? ShowcaseInstaller.CreateMovieNfo(item)
                        : ShowcaseInstaller.CreateBookOpf(
                            item,
                            item.Artifacts.Single(static artifact =>
                                artifact.Role == "book"));
                    await output.WriteLineAsync(
                        $"{item.Id} " +
                        $"metadata={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(metadata))} " +
                        $"sidecar={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ShowcaseInstaller.CreateMetadataSidecarBytes(item)))} " +
                        $"projection={Jellyfin.SemanticDiscovery.Core.Identity.ContentHash.Compute(ShowcaseInstaller.CreateProjectedMetadata(item))} " +
                        $"rights={ShowcaseSourceLock.ComputeRightsEvidenceHash(item.Rights)}");
                }
                return 0;

            case "showcase-purge":
                ShowcasePurger.Purge(
                    GetRequiredOption(args, "--output"));
                await output.WriteLineAsync("SHOWCASE_PURGED");
                return 0;

            case "showcase-runtime-purge":
                ShowcaseRuntimePurger.Purge(
                    GetRequiredOption(args, "--output"));
                await output.WriteLineAsync("SHOWCASE_RUNTIME_PURGED");
                return 0;

            case "showcase-runtime-ensure":
                ShowcaseRuntimePurger.EnsureOwned(
                    GetRequiredOption(args, "--output"));
                await output.WriteLineAsync("SHOWCASE_RUNTIME_OWNED");
                return 0;

            default:
                await error.WriteLineAsync($"Unknown command '{args[0]}'.");
                return 2;
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        await error.WriteLineAsync(exception.ToString());
        return 1;
    }
}

static string GetRequiredOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0 || index == args.Length - 1 ||
        string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException($"Option '{name}' requires a value.");
    }

    return Path.GetFullPath(args[index + 1]);
}

static string GetRequiredStringOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0 || index == args.Length - 1 ||
        string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException($"Option '{name}' requires a value.");
    }

    return args[index + 1];
}

static string GetPathOption(string[] args, string name, string fallback) =>
    Path.GetFullPath(GetStringOption(args, name, fallback));

static string GetStringOption(string[] args, string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index == args.Length - 1 || string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException($"Option '{name}' requires a value.");
    }

    return args[index + 1];
}

file sealed class ProbeTokenCounter : ITokenCounter
{
    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).Length;
}
