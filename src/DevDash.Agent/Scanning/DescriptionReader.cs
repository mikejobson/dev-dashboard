using System.Text.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;

namespace DevDash.Agent.Scanning;

/// <summary>Reads the inputs a description is built from: the README's first paragraph and project manifests.</summary>
public static partial class DescriptionReader
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxExcerpt = 400;
    private const int MaxManifests = 12;

    private static readonly string[] ReadmeNames = ["README.md", "README", "README.txt", "README.rst", "README.markdown"];

    [GeneratedRegex(@"<Description>\s*(?<v>[^<]+?)\s*</Description>", RegexOptions.IgnoreCase)]
    private static partial Regex CsprojDescription();

    [GeneratedRegex(@"^\s*name\s*=\s*""(?<v>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex TomlName();

    [GeneratedRegex(@"^\s*description\s*=\s*""(?<v>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex TomlDescription();

    [GeneratedRegex(@"!?\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s")]
    private static partial Regex ListItem();

    public static DescriptionInputs? Read(string path)
    {
        string? excerpt = null;
        var manifests = new List<ManifestInfo>();
        try
        {
            var files = Directory.EnumerateFiles(path).ToList();
            var readme = files.FirstOrDefault(f => ReadmeNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
            if (readme is not null)
            {
                excerpt = FirstParagraph(ReadText(readme));
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                AddManifest(manifests, path, file);
            }

            // One level down catches the usual src/Project/Project.csproj and apps/web/package.json layouts.
            foreach (var dir in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (manifests.Count >= MaxManifests || name.StartsWith('.') || name is "node_modules" or "bin" or "obj")
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    AddManifest(manifests, path, file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }

        return excerpt is null && manifests.Count == 0
            ? null
            : new DescriptionInputs { ReadmeExcerpt = excerpt, Manifests = manifests };
    }

    /// <summary>The first paragraph of prose, skipping headings, badges, HTML and front matter.</summary>
    public static string? FirstParagraph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var paragraph = new List<string>();
        var inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                if (paragraph.Count > 0) { break; }
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var skip = line.Length == 0
                || line.StartsWith('#')
                || line.StartsWith('<')
                || line.StartsWith("[![", StringComparison.Ordinal)
                || line.StartsWith("![", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("===", StringComparison.Ordinal)
                || line.StartsWith('|');
            if (skip)
            {
                if (paragraph.Count > 0) { break; }
                continue;
            }

            // Lists are structure (file trees, feature bullets), not a description. A lone lead-in such as
            // "Key folders:" belongs to the list, so drop it and keep looking for real prose.
            if (ListItem().IsMatch(line))
            {
                if (paragraph.Count == 1 && paragraph[0].EndsWith(':')) { paragraph.Clear(); }
                if (paragraph.Count > 0) { break; }
                continue;
            }

            paragraph.Add(line);
        }

        if (paragraph.Count == 0)
        {
            return null;
        }

        var result = MarkdownLink().Replace(string.Join(' ', paragraph), "${text}").Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
        if (result.Length == 0)
        {
            return null;
        }

        return result.Length <= MaxExcerpt ? result : result[..MaxExcerpt].TrimEnd() + "…";
    }

    private static void AddManifest(List<ManifestInfo> manifests, string root, string file)
    {
        if (manifests.Count >= MaxManifests)
        {
            return;
        }

        var name = Path.GetFileName(file);
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var ext = Path.GetExtension(name).ToLowerInvariant();

        try
        {
            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(ReadText(file) ?? "{}", new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                manifests.Add(new ManifestInfo
                {
                    File = relative,
                    Name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    Description = doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? NullIfEmpty(d.GetString()) : null,
                });
            }
            else if (ext is ".csproj" or ".fsproj")
            {
                var m = CsprojDescription().Match(ReadText(file) ?? "");
                manifests.Add(new ManifestInfo { File = relative, Name = Path.GetFileNameWithoutExtension(name), Description = m.Success ? m.Groups["v"].Value : null });
            }
            else if (ext is ".sln" or ".slnx")
            {
                manifests.Add(new ManifestInfo { File = relative, Name = Path.GetFileNameWithoutExtension(name) });
            }
            else if (name is "pyproject.toml" or "Cargo.toml")
            {
                var text = ReadText(file) ?? "";
                var n = TomlName().Match(text);
                var d = TomlDescription().Match(text);
                manifests.Add(new ManifestInfo { File = relative, Name = n.Success ? n.Groups["v"].Value : null, Description = d.Success ? NullIfEmpty(d.Groups["v"].Value) : null });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            manifests.Add(new ManifestInfo { File = relative });
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadText(string file)
    {
        using var stream = File.OpenRead(file);
        var buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿');
    }
}
