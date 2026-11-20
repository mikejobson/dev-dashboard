using DevDash.Contracts;
using DevDash.Core.Toolchains;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>A repo pins a tool version (global.json, .nvmrc, .python-version…) that the machine it is on does not have.</summary>
public sealed class ToolchainMismatchRule : ICleanupRule
{
    public string Id => "toolchain-mismatch";
    public string Title => "Pinned tool version not installed";
    public string Description => "The repo asks for an SDK or runtime version (global.json, .nvmrc, .node-version, package.json engines, .python-version, .tool-versions) that was not found on that machine, so it will not build or run there as it stands.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var clone in context.Clones())
        {
            var missing = ToolchainCheck.For(clone.Entry, clone.Machine).Where(c => c.Status == PinStatus.Missing).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            var path = clone.Entry.Path;
            var managers = clone.Machine.Toolchains!.Tools.SelectMany(t => t.Managers).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commands = new List<FindingCommand>();
            foreach (var check in missing)
            {
                var pin = check.Pin;
                var file = pin.Source.Split(' ')[0];
                commands.Add(Inspect($"What {file} asks for", $"cat {Quote(path.TrimEnd('/', '\\') + "/" + file)}"));
                commands.Add(Inspect($"What {ToolNames.Display(pin.Tool)} versions are installed", Installed(pin.Tool)));
                if (Install(pin, managers) is { } install)
                {
                    commands.Add(Fix($"Install {ToolNames.Display(pin.Tool)} {pin.Version}", install));
                }
            }

            // A missing .NET SDK stops every build; a different Node or Python patch usually only warns.
            var dotnet = missing.FirstOrDefault(c => c.Pin.Tool == ToolNames.Dotnet);
            var first = missing[0];
            yield return this.ForClone(clone, dotnet is not null ? FindingSeverity.Medium : FindingSeverity.Low,
                missing.Count == 1
                    ? $"{clone.Entry.Name}: {ToolNames.Display(first.Pin.Tool)} {first.Pin.Version} is not on {clone.Machine.Hostname}"
                    : $"{clone.Entry.Name}: {Plural(missing.Count, "pinned tool version")} not on {clone.Machine.Hostname}",
                string.Join(" ", missing.Select(c => $"{c.Pin.Source} {c.Detail}.")),
                commands.DistinctBy(c => c.Command),
                [.. missing.Select(c => $"{c.Pin.Tool}:{c.Pin.Source}:{c.Pin.Version}:{c.Pin.RollForward}").Order(StringComparer.Ordinal)])
                with
            { Url = dotnet is not null && ToolVersion.Parse(dotnet.Pin.Version) is { } v ? $"https://dotnet.microsoft.com/download/dotnet/{v.Major}.{v.Minor}" : null };
        }
    }

    private static string Installed(string tool) => tool switch
    {
        ToolNames.Dotnet => "dotnet --list-sdks",
        ToolNames.Node => "node --version",
        ToolNames.Python => "python3 --version",
        ToolNames.Go => "go version",
        ToolNames.Rust => "rustc --version",
        ToolNames.Java => "java -version",
        _ => tool + " --version",
    };

    /// <summary>The install command of a version manager that machine has, when the pin is a plain version it would understand.</summary>
    private static string? Install(ToolchainPin pin, HashSet<string> managers)
    {
        var version = pin.Version.Split(' ')[0];
        if (ToolVersion.Parse(version) is null)
        {
            return null;
        }

        return pin.Tool switch
        {
            ToolNames.Node when managers.Contains("nvm") => $"nvm install {Quote(version)}",
            ToolNames.Node when managers.Contains("fnm") => $"fnm install {Quote(version)}",
            ToolNames.Node when managers.Contains("volta") => $"volta install {Quote("node@" + version)}",
            ToolNames.Python when managers.Contains("pyenv") => $"pyenv install --skip-existing {Quote(version)}",
            _ => null,
        };
    }
}
