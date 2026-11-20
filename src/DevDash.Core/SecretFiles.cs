namespace DevDash.Core;

/// <summary>
/// Recognises file names that usually hold a secret: <c>.env*</c>, <c>*.pem</c>, <c>*.key</c>,
/// <c>*.p12</c>, <c>*.pfx</c>, <c>id_rsa*</c> (and the other SSH key types) and
/// <c>credentials*.json</c>. It goes by the name alone; nothing ever opens the file.
/// </summary>
public static class SecretFiles
{
    private static readonly string[] Extensions = [".pem", ".key", ".p12", ".pfx"];
    private static readonly string[] SshKeys = ["id_rsa", "id_ed25519", "id_ecdsa", "id_dsa"];
    private static readonly string[] Templates = [".example", ".sample", ".template", ".dist"];

    /// <summary>True for a file name (not a path) that looks like a secret.</summary>
    public static bool LooksSecret(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name.Length == 0)
        {
            return false;
        }

        if (name.StartsWith(".env", StringComparison.Ordinal))
        {
            // .env, .env.local, .env.production; not .envrc-style names, and not the templates people commit.
            return (name.Length == 4 || name[4] == '.') && !Templates.Any(t => name.EndsWith(t, StringComparison.Ordinal));
        }

        if (Extensions.Any(e => name.EndsWith(e, StringComparison.Ordinal) && name.Length > e.Length))
        {
            return true;
        }

        if (SshKeys.Any(k => name.StartsWith(k, StringComparison.Ordinal)))
        {
            return !name.EndsWith(".pub", StringComparison.Ordinal); // the public half is not a secret
        }

        return name.StartsWith("credentials", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
    }

    /// <summary>The last segment of a path that uses either separator.</summary>
    public static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
