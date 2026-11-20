using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevDash.Agent.Service;

/// <summary>
/// Writes what `install` was given into <c>~/.config/devdash/agent.json</c>, the same file the agent has
/// always read, and the API key into a separate file only the user can read.
/// </summary>
public static class AgentConfigWriter
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep paths and URLs readable
    };

    /// <summary>
    /// Merges the request into the existing config text (null when there is no file yet). Settings the
    /// request does not mention are kept, so re-running install to change one thing is safe.
    /// </summary>
    public static string Merge(string? existingJson, InstallRequest request, string apiKeyFile)
    {
        var root = Parse(existingJson);
        if (root[AgentOptions.SectionName] is not JsonObject agent)
        {
            agent = [];
            root[AgentOptions.SectionName] = agent;
        }

        if (request.HubUrl is not null)
        {
            agent["HubUrl"] = request.HubUrl;
        }

        if (request.ApiKey is not null)
        {
            // The key itself goes in its own file; an inline key would take precedence over it.
            agent.Remove("ApiKey");
            agent["ApiKeyFile"] = apiKeyFile;
        }

        if (request.Roots.Count > 0)
        {
            agent["Roots"] = new JsonArray([.. request.Roots.Select(r => JsonValue.Create(r))]);
        }

        if (request.MachineId is not null)
        {
            agent["MachineId"] = request.MachineId;
        }

        foreach (var (key, value) in request.Settings)
        {
            Set(agent, key, value);
        }

        return root.ToJsonString(Indented) + "\n";
    }

    /// <summary>True when the config (after merging) names a hub and has some way to find an API key.</summary>
    public static (string? HubUrl, bool HasKey) Summarise(string json)
    {
        var agent = Parse(json)[AgentOptions.SectionName] as JsonObject;
        var hasKey = !string.IsNullOrWhiteSpace(agent?["ApiKey"]?.ToString()) || !string.IsNullOrWhiteSpace(agent?["ApiKeyFile"]?.ToString());
        return (agent?["HubUrl"]?.ToString(), hasKey);
    }

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonNode.Parse(json, documentOptions: options) as JsonObject
            ?? throw new JsonException("The existing agent.json is not a JSON object.");
    }

    /// <summary>Sets <c>A:B:C</c> under the Agent section, creating objects on the way down.</summary>
    private static void Set(JsonObject agent, string key, string value)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var node = agent;
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = [];
                node[part] = child;
            }

            node = child;
        }

        node[parts[^1]] = Typed(value);
    }

    private static JsonNode Typed(string value)
    {
        if (bool.TryParse(value, out var flag))
        {
            return JsonValue.Create(flag);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return JsonValue.Create(whole);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    public static void WriteConfig(string path, string json)
    {
        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));

        // The config can hold an inline ApiKey or alert settings someone added by hand: keep it to this user.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// Stores the API key readable by the current user only: mode 0600 on macOS and Linux, and on
    /// Windows an ACL with inheritance removed and a single entry for the user.
    /// </summary>
    public static void WriteApiKey(string path, string apiKey)
    {
        CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path); // permissions are only applied when the file is created
        var bytes = new UTF8Encoding(false).GetBytes(apiKey.Trim() + "\n");
        if (OperatingSystem.IsWindows())
        {
            WriteWindows(path, bytes);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory); // inherits the profile's ACL: user, SYSTEM, Administrators
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, byte[] bytes)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Could not determine the current Windows user.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        using var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        stream.Write(bytes);
    }
}
