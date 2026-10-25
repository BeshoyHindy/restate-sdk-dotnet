using System.Text.Json;
using System.Text.RegularExpressions;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     The compatibility table in <c>README.md</c> ends with a row for the version being shipped,
///     and release-please keeps that row current: <c>README.md</c> is an <c>extra-files</c> entry
///     with the generic updater, and the newest row carries an <c>x-release-please-version</c>
///     marker. 0.4.1 reached NuGet without a row because nothing checked the two against each
///     other, so these tests do. They only read files.
/// </summary>
public sealed class CompatibilityTableTests
{
    /// <summary>The marker release-please's generic updater looks for on a line.</summary>
    private const string Marker = "x-release-please-version";

    /// <summary>
    ///     What the generic updater substitutes: its <c>VERSION_REGEX</c>, which carries no
    ///     <c>g</c> flag, so <c>String.prototype.replace</c> rewrites the first match on the line
    ///     and leaves the rest of the line alone.
    /// </summary>
    private const string UpdaterVersionPattern = @"\d+\.\d+\.\d+(-[\w.]+)?(\+[-\w.]+)?";

    [Fact]
    public void Newest_compatibility_row_names_the_version_the_props_carry()
    {
        var newest = CompatibilityRows()[^1];

        Assert.Equal(PropsVersion(), FirstCell(newest));
    }

    /// <summary>
    ///     The updater rewrites every marked line, so exactly one row may be marked — the newest.
    ///     A marker left on an older row would rewrite history on the next release.
    /// </summary>
    [Fact]
    public void Only_the_newest_compatibility_row_carries_the_marker()
    {
        var marked = ReadmeLines()
            .Where(line => line.Contains(Marker, StringComparison.Ordinal))
            .ToArray();

        Assert.Single(marked);
        Assert.Equal(CompatibilityRows()[^1], marked[0]);
    }

    /// <summary>
    ///     Only the first semver on the marked line is replaced, so the SDK version has to come
    ///     before the <c>1.6.0+</c> in the server column — otherwise a release would bump the
    ///     minimum server version instead of the row.
    /// </summary>
    [Fact]
    public void Release_please_rewrites_the_row_version_and_not_the_server_column()
    {
        var newest = CompatibilityRows()[^1];

        var replaced = Regex.Match(newest, UpdaterVersionPattern);

        Assert.True(replaced.Success, $"No version for release-please to replace on: {newest}");
        Assert.Equal(PropsVersion(), replaced.Value);
    }

    [Fact]
    public void ReleasePlease_updates_the_readme_with_the_generic_updater()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryLayout.RepoRoot(), "release-please-config.json")));

        var extraFiles = document.RootElement
            .GetProperty("packages")
            .GetProperty(".")
            .GetProperty("extra-files")
            .EnumerateArray()
            .Select(file => (Type: file.GetProperty("type").GetString(), Path: file.GetProperty("path").GetString()))
            .ToArray();

        Assert.Contains(("generic", "README.md"), extraFiles);
    }

    /// <summary>Reads the table's data rows, oldest first, from the compatibility section.</summary>
    private static string[] CompatibilityRows()
    {
        var lines = ReadmeLines();
        var heading = Array.FindIndex(lines, line => line.Trim() == "## Compatibility");
        Assert.True(heading >= 0, "README.md no longer has a '## Compatibility' heading.");

        var rows = lines
            .Skip(heading)
            .SkipWhile(line => !line.StartsWith('|'))
            .TakeWhile(line => line.StartsWith('|'))
            .Skip(2) // The header row and the alignment row.
            .ToArray();

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string FirstCell(string row)
    {
        return row.Split('|', StringSplitOptions.TrimEntries)[1];
    }

    private static string PropsVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryLayout.RepoRoot(), "Directory.Build.props"));

        var version = Regex.Match(props, @"<Version>(?<version>[^<]+)</Version>");
        Assert.True(version.Success, "Directory.Build.props no longer carries a <Version> element.");

        return version.Groups["version"].Value;
    }

    private static string[] ReadmeLines()
    {
        return File.ReadAllLines(Path.Combine(RepositoryLayout.RepoRoot(), "README.md"));
    }
}
