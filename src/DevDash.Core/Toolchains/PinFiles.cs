using System.Text.Json;
using DevDash.Contracts;

namespace DevDash.Core.Toolchains;

/// <summary>
/// Reads the version a repository pins out of the text of its own files. Pure: the agent reads the
/// files (at the top of the working tree only) and hands the text over.
/// </summary>
public static class PinFiles
{
    public const string GlobalJson = "global.json";
    public const string Nvmrc = ".nvmrc";
    public const string NodeVersion = ".node-version";
    public const string PackageJson = "package.json";
    public const string PythonVersion = ".python-version";
    public const string ToolVersions = ".tool-versions";

    /// <summary>The files looked at, in the order their pins are listed.</summary>
    public static readonly IReadOnlyList<string> Names = [GlobalJson, Nvmrc, NodeVersion, PackageJson, PythonVersion, ToolVersions];

    /// <summary>Files larger than this are not read: none of these is ever that big.</summary>
    public const int MaxBytes = 256 * 1024;

    private const int MaxVersionLength = 100;

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<ToolchainPin> Parse(string fileName, string content) => fileName switch
    {
        GlobalJson => ParseGlobalJson(content),
        Nvmrc => FirstLine(content) is { } v ? [new ToolchainPin { Tool = ToolNames.Node, Version = v, Source = Nvmrc }] : [],
        NodeVersion => FirstLine(content) is { } v ? [new ToolchainPin { Tool = ToolNames.Node, Version = v, Source = NodeVersion }] : [],
        PackageJson => ParsePackageJson(content),
        PythonVersion => FirstLine(content) is { } v ? [new ToolchainPin { Tool = ToolNames.Python, Version = v, Source = PythonVersion }] : [],
        ToolVersions => ParseToolVersions(content),
        _ => [],
    };

    private static IReadOnlyList<ToolchainPin> ParseGlobalJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, Lenient);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object
                || Text(sdk, "version") is not { } version)
            {
                // No version means "any SDK": nothing is pinned.
                return [];
            }

            bool? prerelease = sdk.TryGetProperty("allowPrerelease", out var allow) && allow.ValueKind is JsonValueKind.True or JsonValueKind.False ? allow.GetBoolean() : null;
            return [new ToolchainPin { Tool = ToolNames.Dotnet, Version = version, Source = GlobalJson, RollForward = Text(sdk, "rollForward"), AllowPrerelease = prerelease }];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ToolchainPin> ParsePackageJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, Lenient);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var pins = new List<ToolchainPin>();
            if (doc.RootElement.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object && Text(engines, "node") is { } range)
            {
                pins.Add(new ToolchainPin { Tool = ToolNames.Node, Version = range, Source = "package.json engines.node" });
            }

            if (doc.RootElement.TryGetProperty("volta", out var volta) && volta.ValueKind == JsonValueKind.Object && Text(volta, "node") is { } exact)
            {
                pins.Add(new ToolchainPin { Tool = ToolNames.Node, Version = exact, Source = "package.json volta.node" });
            }

            return pins;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>asdf and mise: <c>nodejs 20.11.0</c>, <c>python 3.12.1 3.11.8</c>. Plugins this dashboard has no opinion on are left out.</summary>
    private static IReadOnlyList<ToolchainPin> ParseToolVersions(string content)
    {
        var pins = new List<ToolchainPin>();
        foreach (var raw in content.Split('\n').Take(200))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var parts = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var tool = parts[0].ToLowerInvariant() switch
            {
                "nodejs" or "node" => ToolNames.Node,
                "python" => ToolNames.Python,
                "dotnet" or "dotnet-core" => ToolNames.Dotnet,
                "golang" or "go" => ToolNames.Go,
                "rust" => ToolNames.Rust,
                "java" => ToolNames.Java,
                _ => null,
            };
            var versions = string.Join(' ', parts.Skip(1));
            if (tool is not null && versions.Length <= MaxVersionLength && pins.All(p => p.Tool != tool))
            {
                pins.Add(new ToolchainPin { Tool = tool, Version = versions, Source = ToolVersions });
            }
        }

        return pins;
    }

    private static string? FirstLine(string content)
    {
        foreach (var raw in content.Split('\n').Take(50))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            line = line.Trim().Trim('﻿');
            if (line.Length > 0)
            {
                return line.Length <= MaxVersionLength ? line : null;
            }
        }

        return null;
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 and <= MaxVersionLength } text
            ? text
            : null;
}
