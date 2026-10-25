namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     The release tests read the repository's own files — workflows, <c>Directory.Build.props</c>,
///     <c>README.md</c> — rather than anything the build copies next to the test assembly, so they
///     all need the repository root. One walk up from the test output directory serves them all.
/// </summary>
internal static class RepositoryLayout
{
    /// <summary>
    ///     Walks up from the test assembly's output directory to the directory holding the solution
    ///     file, which only the repository root does.
    /// </summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Restate.Sdk.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
