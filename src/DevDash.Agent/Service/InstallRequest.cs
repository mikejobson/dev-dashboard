namespace DevDash.Agent.Service;

/// <summary>The parsed arguments of <c>devdash-agent install</c> and <c>uninstall</c>.</summary>
public sealed record InstallRequest
{
    public string? HubUrl { get; init; }
    public string? ApiKey { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
    public string? MachineId { get; init; }

    /// <summary>
    /// Extra settings under the <c>Agent</c> section, keyed by configuration path (<c>AutoFetch:Enabled</c>).
    /// This is how options the installer knows nothing about reach the config file: <c>--set Key=Value</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>Print what would be written and run; change nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Write the config and service definition but do not register or start the service.</summary>
    public bool NoStart { get; init; }

    /// <summary>On uninstall, also delete the config, API key and logs.</summary>
    public bool Purge { get; init; }

    public string? Error { get; init; }

    /// <param name="readFile">Reads <c>--api-key-file</c>; replaced in tests.</param>
    /// <param name="environmentKey">Value of <c>DEVDASH_API_KEY</c>, used when no key option is given.</param>
    public static InstallRequest Parse(IReadOnlyList<string> args, Func<string, string>? readFile = null, string? environmentKey = null)
    {
        readFile ??= path => File.ReadAllText(AgentOptions.ExpandHome(path));
        string? hubUrl = null, apiKey = null, machineId = null;
        var roots = new List<string>();
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool dryRun = false, noStart = false, purge = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            string? Value() => index + 1 < args.Count ? args[++index] : null;

            switch (arg)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-start":
                    noStart = true;
                    break;
                case "--purge":
                    purge = true;
                    break;
                case "--no-fetch":
                    settings["AutoFetch:Enabled"] = "false";
                    break;
                case "--no-watch":
                    settings["Watch"] = "false";
                    break;
                case "--no-deps":
                    settings["Dependencies:Enabled"] = "false";
                    break;
                case "--enable-actions":
                    settings["Actions:Enabled"] = "true";
                    break;
                case "--hub-url" or "--api-key" or "--api-key-file" or "--root" or "--machine-id" or "--set" or "--updates":
                    if (Value() is not { } value)
                    {
                        return Fail($"{arg} needs a value.");
                    }

                    switch (arg)
                    {
                        case "--hub-url":
                            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                            {
                                return Fail($"--hub-url must be an http(s) URL, got '{value}'.");
                            }

                            hubUrl = value.TrimEnd('/');
                            break;
                        case "--api-key":
                            apiKey = value.Trim();
                            break;
                        case "--api-key-file":
                            try
                            {
                                apiKey = readFile(value).Trim();
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                return Fail($"Could not read --api-key-file: {ex.Message}");
                            }

                            break;
                        case "--root":
                            roots.Add(value);
                            break;
                        case "--machine-id":
                            machineId = value.Trim();
                            break;
                        case "--updates":
                            if (DevDash.Contracts.AgentUpdateModes.Normalize(value) is not { } mode)
                            {
                                return Fail($"--updates must be off, notify or auto, got '{value}'.");
                            }

                            settings["Updates:Mode"] = mode;
                            break;
                        default:
                            var cut = value.IndexOf('=');
                            if (cut <= 0)
                            {
                                return Fail($"--set expects Key=Value, got '{value}'.");
                            }

                            var key = value[..cut].Trim().Replace("__", ":", StringComparison.Ordinal);
                            if (key.StartsWith(AgentOptions.SectionName + ":", StringComparison.OrdinalIgnoreCase))
                            {
                                key = key[(AgentOptions.SectionName.Length + 1)..];
                            }

                            settings[key] = value[(cut + 1)..];
                            break;
                    }

                    break;
                default:
                    return Fail($"Unknown option '{arg}'.");
            }
        }

        if (apiKey is null && !string.IsNullOrWhiteSpace(environmentKey))
        {
            apiKey = environmentKey.Trim();
        }

        if (apiKey is { Length: 0 })
        {
            return Fail("The API key is empty.");
        }

        return new InstallRequest
        {
            HubUrl = hubUrl,
            ApiKey = apiKey,
            Roots = roots,
            MachineId = machineId,
            Settings = settings,
            DryRun = dryRun,
            NoStart = noStart,
            Purge = purge,
        };

        static InstallRequest Fail(string message) => new() { Error = message };
    }
}
