using System.Text.RegularExpressions;

namespace DevDash.Hub.Tests;

/// <summary>
/// The stylesheet is not compiled, so nothing else notices a lost brace: one unclosed <c>@media</c> block once hid
/// every rule after it on desktop widths (the History charts' labels came out black on black).
/// </summary>
public sealed partial class StylesheetTests
{
    [Fact]
    public void Every_block_in_app_css_is_closed()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DevDash.Hub", "wwwroot", "app.css"));
        var code = Comments().Replace(css, "");

        var depth = 0;
        var line = 1;
        foreach (var c in code)
        {
            if (c == '\n')
            {
                line++;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                Assert.True(depth >= 0, $"app.css closes a block it never opened, around line {line} (comments removed).");
            }
        }

        Assert.Equal(0, depth);

        // Only @media, @supports and the like nest rules; a selector opening inside another rule means a lost '}'.
        Assert.DoesNotMatch(NestedRule(), code);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No repository root above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>A rule body (no '}' yet) that opens another rule: <c>.a { color: red; .b {</c>.</summary>
    [GeneratedRegex(@"\{[^{}]*;\s*[^{}@;]+\{")]
    private static partial Regex NestedRule();
}
