namespace DevDash.Core.Tests;

internal static class Fixture
{
    /// <summary>Reads a captured git output file from the Fixtures folder.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
