using Jellyfin.SemanticDiscovery.Setup;

return await RunAsync(
    args,
    Console.Out,
    Console.Error,
    CancellationToken.None);

static async Task<int> RunAsync(
    string[] args,
    TextWriter output,
    TextWriter error,
    CancellationToken cancellationToken)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        await output.WriteLineAsync(
            "Usage: setup plugin|extract|jellyfin [options]".AsMemory(),
            cancellationToken);
        return 0;
    }

    try
    {
        switch (args[0])
        {
            case "plugin":
                PluginInstaller.Install(
                    GetRequiredPathOption(args, "--source"),
                    GetRequiredPathOption(args, "--destination"));
                await output.WriteLineAsync(
                    "Plugin files installed.".AsMemory(),
                    cancellationToken);
                return 0;

            case "jellyfin":
                using (HttpClient httpClient = new()
                {
                    Timeout = TimeSpan.FromMinutes(10),
                })
                {
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
                                    Environment.GetEnvironmentVariable(
                                        "JELLYFIN_ADMIN_USER")
                                    ?? "semantic-admin"),
                                AdminPassword = GetStringOption(
                                    args,
                                    "--admin-password",
                                    Environment.GetEnvironmentVariable(
                                        "JELLYFIN_ADMIN_PASSWORD")
                                    ?? throw new InvalidOperationException(
                                        "JELLYFIN_ADMIN_PASSWORD is required.")),
                                CredentialsPath =
                                    Environment.GetEnvironmentVariable(
                                        "JELLYFIN_CREDENTIALS_PATH"),
                                LibraryManifestPath =
                                    Environment.GetEnvironmentVariable(
                                        "JELLYFIN_LIBRARY_MANIFEST"),
                                ReadyTimeout = TimeSpan.FromSeconds(
                                    int.Parse(
                                        Environment.GetEnvironmentVariable(
                                            "JELLYFIN_READY_TIMEOUT_SECONDS")
                                        ?? "180",
                                        System.Globalization.CultureInfo
                                            .InvariantCulture)),
                            })
                        .BootstrapAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return 0;

            case "extract":
                using (HttpClient extractionClient = new()
                {
                    Timeout = TimeSpan.FromMinutes(30),
                })
                {
                    await new ExtractionBootstrapper(
                            extractionClient,
                            new ExtractionBootstrapOptions
                            {
                                Endpoint = new Uri(
                                    GetStringOption(
                                        args,
                                        "--endpoint",
                                        Environment.GetEnvironmentVariable(
                                            "EXTRACTION_ENDPOINT")
                                        ?? "http://127.0.0.1:8080"),
                                    UriKind.Absolute),
                                ManifestPath = GetRequiredPathOption(
                                    args,
                                    "--manifest"),
                                Profile = GetStringOption(
                                    args,
                                    "--profile",
                                    Environment.GetEnvironmentVariable(
                                        "EXTRACTION_PROFILE")
                                    ?? "Hybrid"),
                            })
                        .RunAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return 0;

            default:
                await error.WriteLineAsync(
                    $"Unknown command '{args[0]}'.".AsMemory(),
                    cancellationToken);
                return 2;
        }
    }
    catch (Exception exception)
        when (exception is not OperationCanceledException)
    {
        await error.WriteLineAsync(
            exception.ToString().AsMemory(),
            cancellationToken);
        return 1;
    }
}

static string GetRequiredPathOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0 ||
        index == args.Length - 1 ||
        string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException(
            $"Option '{name}' requires a value.");
    }

    return Path.GetFullPath(args[index + 1]);
}

static string GetStringOption(
    string[] args,
    string name,
    string fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index == args.Length - 1 ||
        string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException(
            $"Option '{name}' requires a value.");
    }

    return args[index + 1];
}
