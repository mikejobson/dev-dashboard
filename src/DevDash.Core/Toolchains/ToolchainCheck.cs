using DevDash.Contracts;

namespace DevDash.Core.Toolchains;

public enum PinStatus
{
    /// <summary>A version that satisfies the pin is installed on the machine.</summary>
    Satisfied,

    /// <summary>The pin could be read, and nothing installed on the machine satisfies it.</summary>
    Missing,

    /// <summary>The pin is not something this can judge (a virtualenv name, <c>system</c>, a range it cannot read). Never reported as a problem.</summary>
    Unknown,
}

/// <summary>One pin of one working tree, checked against the machine it is on.</summary>
/// <param name="Resolved">The installed version that satisfies it, when one does.</param>
/// <param name="Detail">A sentence fragment for the page: "uses 10.0.103", or what is needed and what is there instead.</param>
public sealed record PinCheck(ToolchainPin Pin, PinStatus Status, string? Resolved, string Detail);

/// <summary>
/// Says whether a machine has what a repository pins. Pure, and deliberately one-sided: anything it
/// cannot read is <see cref="PinStatus.Unknown"/>, so a finding always means "this was looked for and
/// is not there".
/// </summary>
public static class ToolchainCheck
{
    /// <summary>Node's LTS code names and the major version each one is.</summary>
    private static readonly Dictionary<string, int> LtsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = 4, ["boron"] = 6, ["carbon"] = 8, ["dubnium"] = 10, ["erbium"] = 12, ["fermium"] = 14,
        ["gallium"] = 16, ["hydrogen"] = 18, ["iron"] = 20, ["jod"] = 22, ["krypton"] = 24,
    };

    /// <summary>Every pin of an entry against its machine. Empty when the machine sent no inventory: nothing can be said.</summary>
    public static IReadOnlyList<PinCheck> For(DevEntry entry, MachineInfo machine) =>
        machine.Toolchains is { } inventory && entry.Pins is { Count: > 0 } pins
            ? pins.Select(pin => Check(pin, inventory)).ToList()
            : [];

    public static PinCheck Check(ToolchainPin pin, ToolchainInventory inventory)
    {
        var tool = inventory.Find(pin.Tool);
        var installed = ToolVersion.ParseAll(tool?.Versions ?? []);
        if (pin.Tool == ToolNames.Dotnet && pin.Source == PinFiles.GlobalJson)
        {
            return CheckGlobalJson(pin, tool?.Versions ?? []);
        }

        var (status, resolved, needs) = pin.Tool == ToolNames.Node ? CheckNode(pin, installed) : CheckPrefix(pin.Version, installed);
        return status switch
        {
            PinStatus.Satisfied => new PinCheck(pin, status, resolved?.ToString(), resolved is null ? "installed" : $"uses {resolved}"),
            PinStatus.Missing => new PinCheck(pin, status, null, $"needs {needs ?? pin.Version}; {Has(pin.Tool, installed)}"),
            _ => new PinCheck(pin, status, null, "not something DevDash can check"),
        };
    }

    private static PinCheck CheckGlobalJson(ToolchainPin pin, IReadOnlyList<string> sdks)
    {
        var resolution = DotnetSdkResolver.Resolve(pin.Version, pin.RollForward, pin.AllowPrerelease, sdks);
        if (resolution.Invalid is { } invalid)
        {
            return new PinCheck(pin, PinStatus.Unknown, null, invalid);
        }

        if (resolution.Sdk is { } sdk)
        {
            return new PinCheck(pin, PinStatus.Satisfied, sdk.ToString(), $"uses {sdk}");
        }

        var installed = ToolVersion.ParseAll(sdks);
        var why = pin.AllowPrerelease == false && installed.Any(v => v.IsPrerelease) ? " (previews are not allowed)" : "";
        return new PinCheck(pin, PinStatus.Missing, null, $"needs {DotnetSdkResolver.Describe(pin.Version, pin.RollForward)}{why}; {Has(ToolNames.Dotnet, installed)}");
    }

    private static (PinStatus, ToolVersion?, string?) CheckNode(ToolchainPin pin, List<ToolVersion> installed)
    {
        var text = pin.Version.Trim();
        var lower = text.ToLowerInvariant();

        // nvm's aliases.
        if (lower is "node" or "stable" or "latest" or "current" or "lts" or "lts/*" or "lts/latest" or "system" or "default")
        {
            if (lower.StartsWith("lts", StringComparison.Ordinal))
            {
                // Which release lines are LTS changes every year; an even major is one, and that is all this can know.
                return installed.FirstOrDefault(v => v.Major % 2 == 0 && v.Major >= 4) is { } lts
                    ? (PinStatus.Satisfied, lts, null)
                    : installed.Count == 0 ? (PinStatus.Missing, null, "an LTS release of Node") : (PinStatus.Unknown, null, null);
            }

            return installed.Count > 0 ? (PinStatus.Satisfied, installed[0], null) : (PinStatus.Missing, null, "Node");
        }

        if (lower.StartsWith("lts/", StringComparison.Ordinal))
        {
            if (!LtsNames.TryGetValue(lower[4..], out var major))
            {
                return (PinStatus.Unknown, null, null);
            }

            return installed.FirstOrDefault(v => v.Major == major) is { } match
                ? (PinStatus.Satisfied, match, null)
                : (PinStatus.Missing, null, $"Node {major} ({text})");
        }

        // .tool-versions may list several; any one will do. A plain or partial version is a prefix.
        var words = pin.Source == PinFiles.ToolVersions ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [text];
        if (words.All(w => ToolVersion.Parse(w) is not null))
        {
            return CheckPrefix(text, installed);
        }

        // Otherwise an npm range: engines.node, or a range someone put in .nvmrc.
        ToolVersion? satisfied = null;
        foreach (var version in installed)
        {
            switch (NodeRange.Satisfies(text, version))
            {
                case null:
                    return (PinStatus.Unknown, null, null);
                case true:
                    satisfied ??= version;
                    break;
            }
        }

        if (installed.Count == 0 && NodeRange.Satisfies(text, ToolVersion.Parse("0.0.0")!) is null)
        {
            return (PinStatus.Unknown, null, null);
        }

        return satisfied is not null ? (PinStatus.Satisfied, satisfied, null) : (PinStatus.Missing, null, $"Node {text}");
    }

    /// <summary>
    /// One or more versions, any of which will do, each read as a prefix: <c>3.12</c> is met by any
    /// 3.12.x, <c>3.12.4</c> only by 3.12.4 (which is what pyenv, asdf and mise would look for).
    /// </summary>
    private static (PinStatus, ToolVersion?, string?) CheckPrefix(string text, List<ToolVersion> installed)
    {
        var wanted = new List<ToolVersion>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // "3.12.4/envs/api" is pyenv-virtualenv's way of naming an environment of 3.12.4.
            var slash = word.IndexOf("/envs/", StringComparison.Ordinal);
            if (ToolVersion.Parse(slash > 0 ? word[..slash] : word) is { IsPrerelease: false } version)
            {
                wanted.Add(version);
            }
        }

        if (wanted.Count == 0)
        {
            return (PinStatus.Unknown, null, null);
        }

        foreach (var version in wanted)
        {
            if (installed.FirstOrDefault(version.IsPrefixOf) is { } match)
            {
                return (PinStatus.Satisfied, match, null);
            }
        }

        return (PinStatus.Missing, null, string.Join(" or ", wanted));
    }

    private static string Has(string tool, List<ToolVersion> installed) =>
        installed.Count == 0
            ? $"no {ToolNames.Display(tool)} was found on this machine"
            : $"this machine has {string.Join(", ", installed.Take(6))}{(installed.Count > 6 ? $" and {installed.Count - 6} more" : "")}";
}
