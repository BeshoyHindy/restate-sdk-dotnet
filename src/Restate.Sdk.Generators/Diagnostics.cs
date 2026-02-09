using Microsoft.CodeAnalysis;

namespace Restate.Sdk.Generators;

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor MultipleInputParameters = new(
        "RESTATE001",
        "Multiple input parameters",
        "Handler '{0}' has multiple non-context parameters. Only the first ({1}) will be used as the handler input.",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor MissingContextParameter = new(
        "RESTATE002",
        "Missing context parameter",
        "Handler '{0}' has no Context parameter. Add a Context (or derived) parameter.",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor NestedServiceClass = new(
        "RESTATE003",
        "Nested service class",
        "Service class '{0}' is nested inside another type, which is not supported",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor WorkflowMissingRunHandler = new(
        "RESTATE004",
        "Workflow missing Run handler",
        "Workflow '{0}' has no handler named 'Run'. Workflows should define a Run handler as the main entry point.",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    // RESTATE005: Reserved for future use.
    // RESTATE006: Reserved for future use.

    public static readonly DiagnosticDescriptor SharedHandlerOnService = new(
        "RESTATE007",
        "[SharedHandler] on [Service]",
        "[SharedHandler] on handler '{0}' has no effect on a [Service]. All service handlers are concurrent by default.",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor ServiceNotPublicOrAbstract = new(
        "RESTATE008",
        "Service class is not public or is abstract",
        "Service class '{0}' must be public and non-abstract",
        "Restate",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor InvalidTimeSpanFormat = new(
        "RESTATE009",
        "Invalid TimeSpan format",
        "Handler '{0}' has an invalid TimeSpan value '{1}' for property '{2}'. Use a valid TimeSpan format (e.g. \"00:05:00\" for 5 minutes).",
        "Restate",
        DiagnosticSeverity.Error,
        true);
}