using System.ComponentModel;
using System.Diagnostics;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     An xUnit fact that is skipped when no <c>bash</c> is on PATH, so the shell-script tests
///     never fail on hosts without one. CI runs on Linux, where they always execute.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BashFactAttribute : FactAttribute
{
    public BashFactAttribute()
    {
        if (!BashDetection.IsBashAvailable)
            Skip = "bash is not available on this machine.";
    }
}

/// <summary>Detects (once per test run) whether bash can be launched.</summary>
internal static class BashDetection
{
    public static bool IsBashAvailable { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("bash", "-c \"exit 0\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
                return false;

            process.WaitForExit(15_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
///     Covers <c>.github/scripts/roll-public-api.sh</c>, the step the release workflow runs on the
///     release pull request to move unshipped public API entries into the shipped file. The script
///     runs against a throwaway tree, so the repository's own API files are never touched.
/// </summary>
public sealed class PublicApiRollTests : IDisposable
{
    private readonly List<string> _fixtureRoots = [];

    public void Dispose()
    {
        foreach (var root in _fixtureRoots)
            Directory.Delete(root, recursive: true);
    }

    [BashFact]
    public void Roll_moves_every_project_entry_into_shipped()
    {
        var root = CreateFixture(
            ("Core", ["Api.Beta", "Api.Alpha"]),
            ("Extras", ["Extras.One"]));

        var output = RunRollScript(root);

        Assert.Contains("Rolled 2 entries", output, StringComparison.Ordinal);
        Assert.Contains("Rolled 1 entries", output, StringComparison.Ordinal);

        // Entries land in the shipped file, sorted and behind the nullable header.
        Assert.Equal(
            ["#nullable enable", "Api.Alpha", "Api.Beta", "Existing.Core"],
            ReadLines(root, "Core", "PublicAPI.Shipped.txt"));
        Assert.Equal(
            ["#nullable enable", "Existing.Extras", "Extras.One"],
            ReadLines(root, "Extras", "PublicAPI.Shipped.txt"));

        // ...and the unshipped files are emptied back to the header.
        Assert.Equal(["#nullable enable"], ReadLines(root, "Core", "PublicAPI.Unshipped.txt"));
        Assert.Equal(["#nullable enable"], ReadLines(root, "Extras", "PublicAPI.Unshipped.txt"));
    }

    [BashFact]
    public void Roll_is_idempotent_when_nothing_is_unshipped()
    {
        var root = CreateFixture(("Core", []));

        var output = RunRollScript(root);

        Assert.Contains("No unshipped public API entries to roll.", output, StringComparison.Ordinal);
        Assert.Equal(["#nullable enable", "Existing.Core"], ReadLines(root, "Core", "PublicAPI.Shipped.txt"));
        Assert.Equal(["#nullable enable"], ReadLines(root, "Core", "PublicAPI.Unshipped.txt"));
    }

    [Fact]
    public void ReleaseWorkflow_runs_the_roll_script()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepoRoot(), ".github", "workflows", "release-please.yml"));

        Assert.Contains("roll-public-api.sh", workflow, StringComparison.Ordinal);
    }

    /// <summary>Builds a throwaway src tree: one directory per project, each with both API files.</summary>
    private string CreateFixture(params (string Project, string[] Unshipped)[] projects)
    {
        var root = Path.Combine(Path.GetTempPath(), $"publicapi-roll-{Guid.NewGuid():N}");
        _fixtureRoots.Add(root);

        foreach (var (project, unshipped) in projects)
        {
            var directory = Path.Combine(root, "src", project);
            Directory.CreateDirectory(directory);

            File.WriteAllLines(
                Path.Combine(directory, "PublicAPI.Shipped.txt"), ["#nullable enable", $"Existing.{project}"]);
            File.WriteAllLines(
                Path.Combine(directory, "PublicAPI.Unshipped.txt"), ["#nullable enable", .. unshipped]);
        }

        return root;
    }

    private static string RunRollScript(string root)
    {
        var script = Path.Combine(RepoRoot(), ".github", "scripts", "roll-public-api.sh");

        using var process = Process.Start(new ProcessStartInfo("bash", $"\"{script}\" \"{root}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"roll-public-api.sh failed ({process.ExitCode}): {stderr}");
        return stdout;
    }

    private static string[] ReadLines(string root, string project, string fileName)
    {
        return File.ReadAllLines(Path.Combine(root, "src", project, fileName));
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
