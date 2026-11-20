namespace DevDash.Agent.Toolchains;

/// <summary>
/// The parts of the machine that finding a tool depends on. <see cref="Current"/> reads the real ones;
/// tests build one over a temporary folder.
/// </summary>
public sealed record ToolEnvironment
{
    public required string Home { get; init; }
    public bool IsWindows { get; init; }
    public bool IsMacOS { get; init; }

    /// <summary>The folders of <c>PATH</c>, in order.</summary>
    public IReadOnlyList<string> PathEntries { get; init; } = [];

    /// <summary>Environment variables that move a tool's home: <c>NVM_DIR</c>, <c>DOTNET_ROOT</c>, <c>ProgramFiles</c>…</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The root that absolute Unix locations (<c>/opt/homebrew/bin</c>) hang off. Only tests change it.</summary>
    public string SystemRoot { get; init; } = "/";

    public static ToolEnvironment Current()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (string[])["NVM_DIR", "NVM_HOME", "NVM_SYMLINK", "FNM_DIR", "VOLTA_HOME", "PYENV_ROOT", "PYENV", "ASDF_DATA_DIR", "MISE_DATA_DIR", "DOTNET_ROOT", "JAVA_HOME", "GOROOT", "CARGO_HOME",
                     "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA", "APPDATA", "XDG_DATA_HOME"])
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                variables[name] = value;
            }
        }

        return new ToolEnvironment
        {
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            IsWindows = OperatingSystem.IsWindows(),
            IsMacOS = OperatingSystem.IsMacOS(),
            PathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Variables = variables,
        };
    }

    public string? Var(string name) => Variables.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public string InHome(params string[] parts) => Path.Combine([Home, .. parts]);

    /// <summary>An absolute Unix path, under <see cref="SystemRoot"/>.</summary>
    public string Rooted(string absolute) => SystemRoot == "/"
        ? absolute
        : Path.Combine([SystemRoot, .. absolute.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
}

/// <summary>
/// Finds a tool's executable. A LaunchAgent, a systemd user unit or a scheduled task starts with a bare
/// <c>PATH</c>, so after <c>PATH</c> this looks where the installers put things: Homebrew,
/// <c>~/.dotnet</c>, <c>/usr/local/share/dotnet</c>, <c>%ProgramFiles%</c>, the version managers' folders.
/// </summary>
public sealed class ToolLocator(ToolEnvironment env)
{
    public ToolEnvironment Environment => env;

    /// <summary>Every place the tool's executable was found, <c>PATH</c> first, each file once.</summary>
    public IReadOnlyList<string> FindAll(string tool)
    {
        var found = new List<string>();
        var names = env.IsWindows ? new[] { tool + ".exe" } : [tool];
        foreach (var dir in env.PathEntries.Concat(KnownDirectories(tool)))
        {
            // A relative PATH entry would be looked up in whatever folder the agent happens to be in.
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir))
            {
                continue;
            }

            foreach (var name in names)
            {
                string file;
                try
                {
                    file = Path.Combine(dir, name);
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!found.Contains(file, env.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) && !IsStub(tool, file))
                {
                    found.Add(file);
                }
            }
        }

        return found;
    }

    public string? Find(string tool) => FindAll(tool).FirstOrDefault();

    /// <summary>
    /// macOS ships <c>/usr/bin/python3</c> and <c>/usr/bin/java</c> as stand-ins that, when the real thing
    /// is missing, offer to install it in a dialog. Those are never run unless the real thing is there.
    /// </summary>
    private bool IsStub(string tool, string file)
    {
        if (!env.IsMacOS || !string.Equals(Path.GetDirectoryName(file), env.Rooted("/usr/bin"), StringComparison.Ordinal))
        {
            return false;
        }

        return tool switch
        {
            "python3" or "python" => !Directory.Exists(env.Rooted("/Library/Developer/CommandLineTools/usr/bin")) && !Directory.Exists(env.Rooted("/Applications/Xcode.app")),
            "java" => !HasEntries(env.Rooted("/Library/Java/JavaVirtualMachines")) && !HasEntries(env.InHome("Library", "Java", "JavaVirtualMachines")),
            _ => false,
        };
    }

    private IEnumerable<string> KnownDirectories(string tool)
    {
        if (env.IsWindows)
        {
            var programFiles = env.Var("ProgramFiles") ?? @"C:\Program Files";
            var local = env.Var("LOCALAPPDATA") ?? env.InHome("AppData", "Local");
            return tool switch
            {
                "dotnet" => [Opt(env.Var("DOTNET_ROOT")), Path.Combine(programFiles, "dotnet"), env.InHome(".dotnet")],
                "node" => [Path.Combine(programFiles, "nodejs"), Opt(env.Var("NVM_SYMLINK")), Path.Combine(local, "Volta", "bin"), .. ManagedNodeBins()],
                "python" or "python3" or "py" => [.. Children(Path.Combine(local, "Programs", "Python")), .. Children(programFiles, "Python3*"), Path.Combine(env.Var("PYENV") ?? env.InHome(".pyenv", "pyenv-win"), "shims")],
                "go" => [Opt(env.Var("GOROOT"), "bin"), Path.Combine(programFiles, "Go", "bin")],
                "rustc" => [Path.Combine(env.Var("CARGO_HOME") ?? env.InHome(".cargo"), "bin")],
                "java" => [Opt(env.Var("JAVA_HOME"), "bin"), .. Children(Path.Combine(programFiles, "Java")).Select(d => Path.Combine(d, "bin")), .. Children(Path.Combine(programFiles, "Eclipse Adoptium")).Select(d => Path.Combine(d, "bin")), .. Children(Path.Combine(programFiles, "Microsoft"), "jdk*").Select(d => Path.Combine(d, "bin"))],
                _ => [],
            };
        }

        string[] common = [env.Rooted("/opt/homebrew/bin"), env.Rooted("/usr/local/bin"), env.Rooted("/usr/bin"), env.Rooted("/home/linuxbrew/.linuxbrew/bin"), env.InHome(".local", "bin")];
        return tool switch
        {
            "dotnet" => [Opt(env.Var("DOTNET_ROOT")), env.InHome(".dotnet"), env.Rooted("/usr/local/share/dotnet"), env.Rooted("/usr/share/dotnet"), env.Rooted("/usr/lib/dotnet"), .. common],
            "node" or "npm" => [.. common, Path.Combine(env.Var("VOLTA_HOME") ?? env.InHome(".volta"), "bin"), .. ManagedNodeBins()],
            "python3" or "python" => [.. common, Path.Combine(env.Var("PYENV_ROOT") ?? env.InHome(".pyenv"), "shims")],
            "go" => [Opt(env.Var("GOROOT"), "bin"), .. common, env.Rooted("/usr/local/go/bin")],
            "rustc" => [Path.Combine(env.Var("CARGO_HOME") ?? env.InHome(".cargo"), "bin"), .. common],
            "java" => [Opt(env.Var("JAVA_HOME"), "bin"), env.Rooted("/opt/homebrew/opt/openjdk/bin"), env.Rooted("/usr/local/opt/openjdk/bin"), .. common],
            _ => common,
        };
    }

    /// <summary>The <c>bin</c> folder of the newest Node each version manager holds: a last resort when no <c>node</c> is on the PATH.</summary>
    private IEnumerable<string> ManagedNodeBins()
    {
        foreach (var (_, root, bin) in NodeManagerRoots())
        {
            var newest = Children(root)
                .Select(d => (Dir: d, Version: Core.Toolchains.ToolVersion.Parse(Path.GetFileName(d))))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => x.Dir)
                .FirstOrDefault();
            if (newest is not null)
            {
                yield return bin.Length == 0 ? newest : Path.Combine(newest, bin);
            }
        }
    }

    /// <summary>Where each Node version manager keeps its versions (one folder per version), and where the executable is inside one.</summary>
    public IEnumerable<(string Manager, string Root, string Bin)> NodeManagerRoots()
    {
        if (env.IsWindows)
        {
            var roaming = env.Var("APPDATA") ?? env.InHome("AppData", "Roaming");
            var local = env.Var("LOCALAPPDATA") ?? env.InHome("AppData", "Local");
            yield return ("nvm", env.Var("NVM_HOME") ?? Path.Combine(roaming, "nvm"), "");
            yield return ("fnm", Path.Combine(env.Var("FNM_DIR") ?? Path.Combine(roaming, "fnm"), "node-versions"), "installation");
            yield return ("volta", Path.Combine(env.Var("VOLTA_HOME") ?? Path.Combine(local, "Volta"), "tools", "image", "node"), "");
            yield break;
        }

        yield return ("nvm", Path.Combine(env.Var("NVM_DIR") ?? env.InHome(".nvm"), "versions", "node"), "bin");
        var data = env.Var("XDG_DATA_HOME") ?? env.InHome(".local", "share");
        if (env.Var("FNM_DIR") is { } fnm)
        {
            yield return ("fnm", Path.Combine(fnm, "node-versions"), Path.Combine("installation", "bin"));
        }

        yield return ("fnm", Path.Combine(data, "fnm", "node-versions"), Path.Combine("installation", "bin"));
        yield return ("fnm", env.InHome("Library", "Application Support", "fnm", "node-versions"), Path.Combine("installation", "bin"));
        yield return ("fnm", env.InHome(".fnm", "node-versions"), Path.Combine("installation", "bin"));
        yield return ("volta", Path.Combine(env.Var("VOLTA_HOME") ?? env.InHome(".volta"), "tools", "image", "node"), "bin");
    }

    /// <summary>Where pyenv, asdf and mise keep installed versions of a tool: manager, then the folder with one sub-folder per version.</summary>
    public IEnumerable<(string Manager, string Root)> ManagedVersionRoots(string tool)
    {
        if (tool == Contracts.ToolNames.Python)
        {
            yield return ("pyenv", env.IsWindows
                ? Path.Combine(env.Var("PYENV") ?? env.InHome(".pyenv", "pyenv-win"), "versions")
                : Path.Combine(env.Var("PYENV_ROOT") ?? env.InHome(".pyenv"), "versions"));
        }

        var asdf = Path.Combine(env.Var("ASDF_DATA_DIR") ?? env.InHome(".asdf"), "installs");
        var mise = Path.Combine(env.Var("MISE_DATA_DIR") ?? Path.Combine(env.Var("XDG_DATA_HOME") ?? env.InHome(".local", "share"), "mise"), "installs");
        string[] plugins = tool switch
        {
            Contracts.ToolNames.Node => ["nodejs", "node"],
            Contracts.ToolNames.Python => ["python"],
            Contracts.ToolNames.Dotnet => ["dotnet-core", "dotnet"],
            Contracts.ToolNames.Go => ["golang", "go"],
            Contracts.ToolNames.Rust => ["rust"],
            Contracts.ToolNames.Java => ["java"],
            _ => [],
        };
        foreach (var plugin in plugins)
        {
            yield return ("asdf", Path.Combine(asdf, plugin));
            yield return ("mise", Path.Combine(mise, plugin));
        }
    }

    /// <summary>The sub-folders of a folder, or none when it is missing or unreadable.</summary>
    public static IReadOnlyList<string> Children(string dir, string pattern = "*")
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool HasEntries(string dir) => Children(dir).Count > 0;

    private static string Opt(string? root, string? child = null) =>
        string.IsNullOrEmpty(root) ? "" : child is null ? root : Path.Combine(root, child);
}
