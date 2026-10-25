using System.Text.RegularExpressions;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     <c>Validate PR title</c> is a required check, so it has to run on every pull request event
///     that can put a new title in front of a reviewer. A release pull request raised with the
///     default token gets no checks until it is closed and reopened, so <c>reopened</c> belongs in
///     the trigger list next to the events that create or change a title. This test only reads the
///     workflow file.
/// </summary>
public sealed class PrTitleWorkflowTests
{
    [Fact]
    public void Title_lint_triggers_on_open_edit_synchronize_and_reopen()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryLayout.RepoRoot(), ".github", "workflows", "pr-title.yml"));

        var trigger = Regex.Match(workflow, @"pull_request:\s*\r?\n\s*types:\s*\[(?<types>[^\]]*)\]");
        Assert.True(trigger.Success, "pr-title.yml no longer declares a pull_request types list.");

        var types = trigger.Groups["types"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Order()
            .ToArray();

        Assert.Equal(["edited", "opened", "reopened", "synchronize"], types);
    }
}
