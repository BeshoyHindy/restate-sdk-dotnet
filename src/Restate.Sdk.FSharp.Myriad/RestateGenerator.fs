namespace Restate.Sdk.FSharp.Myriad

open System.Text
open Fantomas.FCS.Syntax
open Myriad.Core

/// <summary>
///   A Myriad generator that produces Restate service registration for F#.
///
///   It scans an input <c>.fs</c> for types carrying the SDK's <c>[&lt;Service&gt;]</c> /
///   <c>[&lt;VirtualObject&gt;]</c> / <c>[&lt;Workflow&gt;]</c> attributes and their
///   <c>[&lt;Handler&gt;]</c> / <c>[&lt;SharedHandler&gt;]</c> members, then emits a <c>Registrations</c>
///   module that calls the strongly-typed builders in the Restate.Sdk.FSharp C# helper — the same job the
///   Roslyn source generator does for C#, which cannot run for F#.
///
///   The AST is matched with named-field patterns so the generator is resilient to Fantomas.FCS adding
///   fields to its syntax nodes between versions.
/// </summary>
module private Emit =

    type Handler =
        { Name: string
          Shared: bool
          HasInput: bool
          HasOutput: bool }

    type Service =
        { TypeName: string
          Kind: string
          Handlers: Handler list }

    let private lastIdent (ids: Ident list) =
        match List.tryLast ids with
        | Some ident -> ident.idText
        | None -> ""

    let private stripAttribute (name: string) =
        if name.EndsWith("Attribute") then name.Substring(0, name.Length - "Attribute".Length) else name

    let private attributeNames (attributes: SynAttributes) =
        attributes
        |> List.collect (fun list -> list.Attributes)
        |> List.map (fun attribute ->
            match attribute.TypeName with
            | SynLongIdent(id = ids) -> stripAttribute (lastIdent ids))

    let private serviceKind (names: string list) =
        if List.contains "Service" names then Some "Service"
        elif List.contains "VirtualObject" names then Some "VirtualObject"
        elif List.contains "Workflow" names then Some "Workflow"
        else None

    // A handler returns an output payload unless it is annotated as returning Task<unit> (or bare Task).
    let private returnsOutput (returnInfo: SynBindingReturnInfo option) =
        match returnInfo with
        | Some(SynBindingReturnInfo(typeName = synType)) ->
            match synType with
            | SynType.App(typeName = SynType.LongIdent(SynLongIdent(id = taskId)); typeArgs = [ arg ]) when
                lastIdent taskId = "Task"
                ->
                match arg with
                | SynType.LongIdent(SynLongIdent(id = argId)) when lastIdent argId = "unit" -> false
                | _ -> true
            | _ -> false
        | None -> true

    let private memberHandler (definition: SynMemberDefn) =
        match definition with
        | SynMemberDefn.Member(memberDefn = SynBinding(attributes = attributes; headPat = headPat; returnInfo = returnInfo)) ->
            let names = attributeNames attributes
            let isExclusive = List.contains "Handler" names
            let isShared = List.contains "SharedHandler" names

            if isExclusive || isShared then
                let name, argCount =
                    match headPat with
                    | SynPat.LongIdent(longDotId = SynLongIdent(id = ids); argPats = SynArgPats.Pats pats) ->
                        lastIdent ids, List.length pats
                    | _ -> "", 0

                if name = "" then
                    None
                else
                    Some
                        { Name = name
                          Shared = isShared
                          HasInput = argCount >= 2
                          HasOutput = returnsOutput returnInfo }
            else
                None
        | _ -> None

    let private serviceMembers (repr: SynTypeDefnRepr) =
        match repr with
        | SynTypeDefnRepr.ObjectModel(members = members) -> members
        | _ -> []

    let private collectServices (ast: ParsedInput) =
        let fromTypeDefn (typeDefn: SynTypeDefn) =
            match typeDefn with
            | SynTypeDefn(typeInfo = SynComponentInfo(attributes = attributes; longId = longId); typeRepr = repr) ->
                match serviceKind (attributeNames attributes) with
                | Some kind ->
                    Some
                        { TypeName = lastIdent longId
                          Kind = kind
                          Handlers = serviceMembers repr |> List.choose memberHandler }
                | None -> None

        let rec fromDecls decls =
            decls
            |> List.collect (fun decl ->
                match decl with
                | SynModuleDecl.Types(typeDefns, _) -> typeDefns |> List.choose fromTypeDefn
                | SynModuleDecl.NestedModule(decls = nested) -> fromDecls nested
                | _ -> [])

        match ast with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            let ns =
                match modules with
                | SynModuleOrNamespace(longId = longId) :: _ -> longId |> List.map (fun id -> id.idText) |> String.concat "."
                | [] -> "Generated"

            let services = modules |> List.collect (fun (SynModuleOrNamespace(decls = decls)) -> fromDecls decls)
            ns, services
        | _ -> "Generated", []

    let private handlerCall (typeName: string) (handler: Handler) =
        let shared = if handler.Shared then "true" else "false"
        let task = "System.Threading.Tasks.Task"

        match handler.HasInput, handler.HasOutput with
        | true, true ->
            $"            FsHandler.InOut(\"{handler.Name}\", {shared}, fun (s: {typeName}) ctx i -> s.{handler.Name} ctx i)"
        | true, false ->
            $"            FsHandler.InUnit(\"{handler.Name}\", {shared}, fun (s: {typeName}) ctx i -> s.{handler.Name} ctx i :> {task})"
        | false, true ->
            $"            FsHandler.Out(\"{handler.Name}\", {shared}, fun (s: {typeName}) ctx -> s.{handler.Name} ctx)"
        | false, false ->
            $"            FsHandler.Unit(\"{handler.Name}\", {shared}, fun (s: {typeName}) ctx -> s.{handler.Name} ctx :> {task})"

    let private serviceRegistration (service: Service) =
        let calls = service.Handlers |> List.map (handlerCall service.TypeName) |> String.concat ",\n"
        $"        FsService.{service.Kind}<{service.TypeName}>(\n            \"{service.TypeName}\",\n{calls})"

    let private bindCall (service: Service) =
        let verb =
            match service.Kind with
            | "Service" -> "AddService"
            | "VirtualObject" -> "AddVirtualObject"
            | _ -> "AddWorkflow"

        $"            .{verb}<{service.TypeName}>()"

    let source (ast: ParsedInput) =
        let ns, services = collectServices ast
        let registrations = services |> List.map serviceRegistration |> String.concat "\n\n"
        let binds = services |> List.map bindCall |> String.concat "\n"

        let builder = StringBuilder()
        builder.AppendLine($"namespace {ns}") |> ignore
        builder.AppendLine() |> ignore
        builder.AppendLine("open System.Threading.Tasks") |> ignore
        builder.AppendLine("open Restate.Sdk.Hosting") |> ignore
        builder.AppendLine("open Restate.Sdk.FSharp") |> ignore
        builder.AppendLine() |> ignore
        builder.AppendLine("/// Restate registration generated from the [<Service>]/[<VirtualObject>]/[<Workflow>] types.") |> ignore
        builder.AppendLine("[<RequireQualifiedAccess>]") |> ignore
        builder.AppendLine("module Registrations =") |> ignore
        builder.AppendLine() |> ignore
        builder.AppendLine("    /// Registers every discovered service definition (mirrors the C# generator's module initializer).") |> ignore
        builder.AppendLine("    let registerAll () : unit =") |> ignore
        builder.AppendLine(registrations) |> ignore
        builder.AppendLine() |> ignore
        builder.AppendLine("    /// Binds every discovered service onto a host builder.") |> ignore
        builder.AppendLine("    let bind (builder: RestateHostBuilder) : RestateHostBuilder =") |> ignore
        builder.AppendLine("        builder") |> ignore
        builder.Append(binds) |> ignore
        builder.ToString()

/// <summary>The Myriad generator entry point. Reference this assembly via <c>MyriadSdkGenerator</c>.</summary>
[<MyriadGenerator("restate")>]
type RestateGenerator() =
    interface IMyriadGenerator with
        member _.ValidInputExtensions = seq { ".fs" }

        member _.Generate(context: GeneratorContext) =
            // Parse with this plugin's own Fantomas.Core rather than Myriad.Core's Ast.fromFilename: the
            // Myriad runner shares its Myriad.Core assembly with the plugin, and the tool's build of that
            // helper has a different signature than the NuGet package. The plugin boundary stays string-only
            // (GeneratorContext.InputFilename in, Output.Source out), which avoids any cross-assembly type
            // identity mismatch.
            let content = System.IO.File.ReadAllText context.InputFilename
            let ast =
                Fantomas.Core.CodeFormatter.ParseAsync(false, content)
                |> Async.RunSynchronously
                |> Array.head
                |> fst
            Output.Source(Emit.source ast)
