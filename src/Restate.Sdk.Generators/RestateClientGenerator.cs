using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Restate.Sdk.Generators.Emitters;
using Restate.Sdk.Generators.Models;

namespace Restate.Sdk.Generators;

[Generator]
public sealed class RestateClientGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var serviceDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (s, _) => IsServiceCandidate(s),
                static (ctx, _) => ExtractServiceInfo(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        var collected = serviceDeclarations.Collect();

        context.RegisterSourceOutput(collected,
            static (spc, services) => Execute(services, spc));

        // Separate pipeline for diagnostics (uses Location which can't be cached)
        var diagnosticCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (s, _) => IsServiceCandidate(s),
                static (ctx, _) => ExtractDiagnostics(ctx))
            .Where(static diags => diags.Length > 0);

        context.RegisterSourceOutput(diagnosticCandidates,
            static (spc, diags) =>
            {
                foreach (var diag in diags)
                    spc.ReportDiagnostic(diag);
            });
    }

    private static bool IsServiceCandidate(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl
               && classDecl.AttributeLists.Count > 0;
    }

    private static (string? ServiceKindAttr, string? ServiceName, ServiceKind Kind, string? WorkflowRetention) GetServiceInfo(
        INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var fullName = attr.AttributeClass?.ToDisplayString();

            if (fullName == "Restate.Sdk.ServiceAttribute")
                return (fullName, GetAttributeNameArg(attr) ?? symbol.Name, ServiceKind.Service, null);

            if (fullName == "Restate.Sdk.VirtualObjectAttribute")
                return (fullName, GetAttributeNameArg(attr) ?? symbol.Name, ServiceKind.VirtualObject, null);

            if (fullName == "Restate.Sdk.WorkflowAttribute")
                return (fullName, GetAttributeNameArg(attr) ?? symbol.Name, ServiceKind.Workflow, GetAttributeStringArg(attr, "WorkflowRetention"));
        }

        return (null, null, ServiceKind.Service, null);
    }

    private static ServiceInfo? ExtractServiceInfo(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol symbol)
            return null;

        var (_, serviceName, kind, workflowRetention) = GetServiceInfo(symbol);

        if (serviceName is null)
            return null;

        // Only generate code for top-level, public, non-abstract classes.
        // File-scoped, internal, nested, and abstract classes are not valid Restate service types.
        if (symbol.DeclaredAccessibility != Accessibility.Public || symbol.IsAbstract ||
            symbol.ContainingType is not null)
            return null;

        var handlers = ImmutableArray.CreateBuilder<HandlerInfo>();

        foreach (var member in symbol.GetMembers())
        {
            if (member is not IMethodSymbol method || method.DeclaredAccessibility != Accessibility.Public)
                continue;

            foreach (var attr in method.GetAttributes())
            {
                var fullName = attr.AttributeClass?.ToDisplayString();
                if (fullName != "Restate.Sdk.HandlerAttribute" && fullName != "Restate.Sdk.SharedHandlerAttribute")
                    continue;

                var handlerName = GetAttributeNameArg(attr) ?? method.Name;
                var isShared = fullName == "Restate.Sdk.SharedHandlerAttribute";

                string? inputType = null;
                var contextParamType = "global::Restate.Sdk.Context";
                var hasCt = false;

                foreach (var param in method.Parameters)
                {
                    if (IsContextType(param.Type))
                    {
                        contextParamType = param.Type.ToDisplayString(FullyQualifiedFormat);
                        continue;
                    }

                    if (param.Type.Name == "CancellationToken")
                    {
                        hasCt = true;
                        continue;
                    }

                    inputType ??= param.Type.ToDisplayString(FullyQualifiedFormat);
                }

                var outputType = UnwrapReturnType(method.ReturnType);
                var returnType = method.ReturnType.ToDisplayString(FullyQualifiedFormat);

                var inactivityTimeout = GetAttributeStringArg(attr, "InactivityTimeout");
                var abortTimeout = GetAttributeStringArg(attr, "AbortTimeout");
                var idempotencyRetention = GetAttributeStringArg(attr, "IdempotencyRetention");
                var journalRetention = GetAttributeStringArg(attr, "JournalRetention");
                var ingressPrivate = GetAttributeBoolArg(attr, "IngressPrivate");

                handlers.Add(new HandlerInfo(
                    handlerName, method.Name, isShared, inputType, outputType,
                    returnType, contextParamType, hasCt,
                    inactivityTimeout, abortTimeout, idempotencyRetention, journalRetention, ingressPrivate));
            }
        }

        if (handlers.Count == 0)
            return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? ""
            : symbol.ContainingNamespace.ToDisplayString();

        return new ServiceInfo(ns, symbol.Name, serviceName, kind, handlers.ToImmutable(), workflowRetention);
    }

    private static ImmutableArray<Diagnostic> ExtractDiagnostics(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol symbol)
            return ImmutableArray<Diagnostic>.Empty;

        var (serviceKindAttr, _, _, _) = GetServiceInfo(symbol);

        if (serviceKindAttr is null)
            return ImmutableArray<Diagnostic>.Empty;

        // Emit diagnostics for nested, non-public, or abstract classes and return early
        // since no code generation or further handler diagnostics apply.
        if (symbol.ContainingType is not null)
            return ImmutableArray.Create(Diagnostic.Create(
                Diagnostics.NestedServiceClass,
                classDecl.Identifier.GetLocation(),
                symbol.Name));

        if (symbol.DeclaredAccessibility != Accessibility.Public || symbol.IsAbstract)
            return ImmutableArray.Create(Diagnostic.Create(
                Diagnostics.ServiceNotPublicOrAbstract,
                classDecl.Identifier.GetLocation(),
                symbol.Name));

        var isPlainService = serviceKindAttr == "Restate.Sdk.ServiceAttribute";
        var isWorkflow = serviceKindAttr == "Restate.Sdk.WorkflowAttribute";

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var hasRunHandler = false;

        foreach (var member in symbol.GetMembers())
        {
            if (member is not IMethodSymbol method || method.DeclaredAccessibility != Accessibility.Public)
                continue;

            string? handlerAttrName = null;
            foreach (var attr in method.GetAttributes())
            {
                var fullName = attr.AttributeClass?.ToDisplayString();
                if (fullName == "Restate.Sdk.HandlerAttribute" || fullName == "Restate.Sdk.SharedHandlerAttribute")
                {
                    handlerAttrName = fullName;
                    break;
                }
            }

            if (handlerAttrName is null) continue;

            var handlerName = method.Name;
            foreach (var attr in method.GetAttributes())
                if (attr.AttributeClass?.ToDisplayString() == handlerAttrName)
                {
                    handlerName = GetAttributeNameArg(attr) ?? method.Name;
                    break;
                }

            if (handlerName == "Run")
                hasRunHandler = true;

            var isShared = handlerAttrName == "Restate.Sdk.SharedHandlerAttribute";

            // RESTATE007: [SharedHandler] on [Service]
            if (isShared && isPlainService)
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.SharedHandlerOnService,
                    method.Locations.FirstOrDefault() ?? classDecl.GetLocation(),
                    method.Name));

            var hasContext = false;
            var inputParamCount = 0;
            string? firstInputName = null;

            foreach (var param in method.Parameters)
            {
                if (IsContextType(param.Type))
                {
                    hasContext = true;
                    continue;
                }

                if (param.Type.Name == "CancellationToken") continue;
                inputParamCount++;
                firstInputName ??= param.Name;
            }

            // RESTATE002: Missing context parameter
            if (!hasContext)
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.MissingContextParameter,
                    method.Locations.FirstOrDefault() ?? classDecl.GetLocation(),
                    method.Name));

            // RESTATE001: Multiple input parameters
            if (inputParamCount > 1)
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.MultipleInputParameters,
                    method.Locations.FirstOrDefault() ?? classDecl.GetLocation(),
                    method.Name, firstInputName ?? "unknown"));

            // RESTATE009: Invalid TimeSpan format
            foreach (var attr2 in method.GetAttributes())
            {
                var attrFullName = attr2.AttributeClass?.ToDisplayString();
                if (attrFullName != "Restate.Sdk.HandlerAttribute" && attrFullName != "Restate.Sdk.SharedHandlerAttribute")
                    continue;

                var timeSpanProps = new[] { "InactivityTimeout", "AbortTimeout", "IdempotencyRetention", "JournalRetention" };
                foreach (var prop in timeSpanProps)
                {
                    var value = GetAttributeStringArg(attr2, prop);
                    if (value is not null && !System.TimeSpan.TryParse(value, out _))
                        diagnostics.Add(Diagnostic.Create(
                            Diagnostics.InvalidTimeSpanFormat,
                            method.Locations.FirstOrDefault() ?? classDecl.GetLocation(),
                            method.Name, value, prop));
                }

                break;
            }
        }

        // RESTATE004: Workflow missing Run handler
        if (isWorkflow && !hasRunHandler)
            diagnostics.Add(Diagnostic.Create(
                Diagnostics.WorkflowMissingRunHandler,
                classDecl.Identifier.GetLocation(),
                symbol.Name));

        return diagnostics.ToImmutable();
    }

    private static string? GetAttributeNameArg(AttributeData attr)
    {
        foreach (var namedArg in attr.NamedArguments)
            if (namedArg.Key == "Name" && namedArg.Value.Value is string n)
                return n;
        return null;
    }

    private static string? GetAttributeStringArg(AttributeData attr, string key)
    {
        foreach (var namedArg in attr.NamedArguments)
            if (namedArg.Key == key && namedArg.Value.Value is string s)
                return s;
        return null;
    }

    private static bool GetAttributeBoolArg(AttributeData attr, string key)
    {
        foreach (var namedArg in attr.NamedArguments)
            if (namedArg.Key == key && namedArg.Value.Value is bool b)
                return b;
        return false;
    }

    private static bool IsContextType(ITypeSymbol type)
    {
        // Walk the base class chain looking for Context
        var current = type;
        while (current is not null)
        {
            if (current.Name == "Context" && current.ContainingNamespace?.ToDisplayString() == "Restate.Sdk")
                return true;
            current = current.BaseType;
        }

        return false;
    }

    private static string? UnwrapReturnType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var typeName = named.Name;

            if ((typeName == "Task" || typeName == "ValueTask") && named.TypeArguments.Length == 1)
                return named.TypeArguments[0].ToDisplayString(FullyQualifiedFormat);

            if (typeName == "Task" || typeName == "ValueTask" || typeName == "Void")
                return null;
        }

        return type.ToDisplayString(FullyQualifiedFormat);
    }

    private static string HintPrefix(ServiceInfo service)
    {
        return string.IsNullOrEmpty(service.Namespace)
            ? service.ClassName
            : $"{service.Namespace}.{service.ClassName}";
    }

    private static void Execute(ImmutableArray<ServiceInfo> services, SourceProductionContext spc)
    {
        if (services.IsDefaultOrEmpty)
            return;

        var distinct = services.Distinct().ToImmutableArray();

        foreach (var service in distinct)
        {
            var prefix = HintPrefix(service);

            var clientSource = ClientEmitter.Generate(service);
            spc.AddSource($"{prefix}Client.g.cs",
                SourceText.From(clientSource, Encoding.UTF8));

            var invokerSource = InvokerEmitter.Generate(service);
            spc.AddSource($"{prefix}Invokers.g.cs",
                SourceText.From(invokerSource, Encoding.UTF8));
        }

        var serializationSource = SerializationContextEmitter.Generate(distinct);
        spc.AddSource("RestateSerializationContext.g.cs",
            SourceText.From(serializationSource, Encoding.UTF8));

        var factorySource = ClientFactoryEmitter.Generate(distinct);
        spc.AddSource("RestateClientFactory.g.cs",
            SourceText.From(factorySource, Encoding.UTF8));

        var serviceDefSource = ServiceDefinitionEmitter.Generate(distinct);
        spc.AddSource("ServiceDefinitions.g.cs",
            SourceText.From(serviceDefSource, Encoding.UTF8));
    }
}
