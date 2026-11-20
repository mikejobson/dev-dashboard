using DevDash.Contracts;
using DevDash.Core.Toolchains;

namespace DevDash.Agent.Toolchains;

/// <summary>
/// Reads the tool versions a working tree pins, from the files at its top: <c>global.json</c>,
/// <c>.nvmrc</c>, <c>.node-version</c>, <c>package.json</c>, <c>.python-version</c>, <c>.tool-versions</c>.
/// Only the version fields leave the machine.
/// </summary>
public static class PinReader
{
    /// <summary>Null when the folder pins nothing.</summary>
    public static IReadOnlyList<ToolchainPin>? Read(string path)
    {
        var pins = new List<ToolchainPin>();
        foreach (var name in PinFiles.Names)
        {
            try
            {
                var file = new FileInfo(Path.Combine(path, name));
                // A link could point anywhere; these are small files that live in the repo.
                if (!file.Exists || file.Length > PinFiles.MaxBytes || file.LinkTarget is not null)
                {
                    continue;
                }

                pins.AddRange(PinFiles.Parse(name, File.ReadAllText(file.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable: as if it were not there.
            }
        }

        return pins.Count == 0 ? null : pins;
    }
}
