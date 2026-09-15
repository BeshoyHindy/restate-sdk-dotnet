using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Restate.Sdk.Generators.Tests;

internal static class GeneratorTestHelper
{
    private static readonly MetadataReference[] References = LoadMetadataReferences();

    public static (GeneratorDriver Driver, Compilation OutputCompilation, ImmutableArray<Diagnostic> Diagnostics)
        RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new RestateClientGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        return (driver, outputCompilation, diagnostics);
    }

    public static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(GeneratorDriver driver)
    {
        return driver.GetRunResult().Diagnostics;
    }

    public static string? GetGeneratedSource(GeneratorDriver driver, string hintName)
    {
        var result = driver.GetRunResult();
        foreach (var generatedTree in result.GeneratedTrees)
            if (generatedTree.FilePath.EndsWith(hintName, StringComparison.Ordinal))
                return generatedTree.GetText().ToString();
        return null;
    }

    /// <summary>
    ///     Asserts that the generated sources compile. A handler the generator accepts but cannot
    ///     emit an invoker for shows up here as an error in a .g.cs file rather than as a silent
    ///     break in the consuming project.
    /// </summary>
    public static void AssertGeneratedCodeCompiles(Compilation outputCompilation)
    {
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) == true)
            .Select(d => $"{d.Location.SourceTree!.FilePath}: {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}")
            .ToArray();

        Assert.True(errors.Length == 0,
            $"Generated sources did not compile:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    private static MetadataReference[] LoadMetadataReferences()
    {
        // Reference everything the test host itself runs against, so the generated sources see
        // the same framework and SDK assemblies a real consuming project would.
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";

        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
