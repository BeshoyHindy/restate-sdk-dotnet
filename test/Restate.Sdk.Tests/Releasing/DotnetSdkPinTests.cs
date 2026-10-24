using System.Text.Json;
using System.Text.RegularExpressions;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     <c>global.json</c> is the single source of truth for the .NET SDK version: every workflow
///     hands its <c>actions/setup-dotnet</c> step <c>global-json-file: global.json</c> instead of a
///     version of its own, so CI and a local build always resolve the same SDK. These tests fail as
///     soon as a workflow carries a literal <c>dotnet-version</c> again. They only read files.
/// </summary>
public sealed class DotnetSdkPinTests
{
    /// <summary>Matches the <c>actions/setup-dotnet</c> input that names a version directly.</summary>
    private const string LiteralVersionPattern = @"dotnet-version\s*:";

    /// <summary>Matches the input that defers to the pin instead.</summary>
    private const string GlobalJsonInputPattern = @"global-json-file\s*:\s*global\.json";

    [Fact]
    public void Workflows_name_no_sdk_version_of_their_own()
    {
        var root = RepoRoot();

        var offenders = WorkflowFiles(root)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), LiteralVersionPattern))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These workflows pin a .NET SDK version instead of reading global.json: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_setup_dotnet_step_reads_global_json()
    {
        var root = RepoRoot();

        var offenders = WorkflowFiles(root)
            .Select(file => (Path: Path.GetRelativePath(root, file), Text: File.ReadAllText(file)))
            .Where(workflow =>
                Regex.Count(workflow.Text, "actions/setup-dotnet@")
                != Regex.Count(workflow.Text, GlobalJsonInputPattern))
            .Select(workflow => workflow.Path)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These workflows set up .NET without pointing at global.json: {string.Join(", ", offenders)}");
    }

    /// <summary>
    ///     <c>setup-dotnet</c> installs the version <c>global.json</c> names, so the pin has to be an
    ///     exact servicing release, while <c>rollForward</c> keeps a newer SDK on a developer machine
    ///     usable.
    /// </summary>
    [Fact]
    public void GlobalJson_pins_an_exact_sdk_version()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "global.json")));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Matches(@"^\d+\.\d+\.\d+$", sdk.GetProperty("version").GetString());
        Assert.Equal("latestMinor", sdk.GetProperty("rollForward").GetString());
    }

    private static IEnumerable<string> WorkflowFiles(string root)
    {
        return Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Restate.Sdk.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
