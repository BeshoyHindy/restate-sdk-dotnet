using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

public sealed record ResolveAwakeableRequest(
    [property: JsonPropertyName("awakeableKey")] string AwakeableKey,
    [property: JsonPropertyName("value")] string Value);

public sealed record RejectAwakeableRequest(
    [property: JsonPropertyName("awakeableKey")] string AwakeableKey,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
///     Runs a list of commands that create durable futures and combine them, recording each
///     command's result. Commands are polymorphic on their "type" field, so they are read as
///     raw JSON.
/// </summary>
[VirtualObject(Name = "VirtualObjectCommandInterpreter")]
public sealed class VirtualObjectCommandInterpreter
{
    private static readonly StateKey<List<string>> Results = new("results");

    [SharedHandler(Name = "getResults")]
    public async Task<List<string>> GetResults(SharedObjectContext ctx)
    {
        return await ctx.Get(Results) ?? [];
    }

    [SharedHandler(Name = "hasAwakeable")]
    public async Task<bool> HasAwakeable(SharedObjectContext ctx, string awakeableKey)
    {
        return !string.IsNullOrEmpty(await ctx.Get(AwakeableKey(awakeableKey)));
    }

    [SharedHandler(Name = "resolveAwakeable")]
    public async Task ResolveAwakeable(SharedObjectContext ctx, ResolveAwakeableRequest request)
    {
        ctx.ResolveAwakeable(await GetAwakeableId(ctx, request.AwakeableKey), request.Value);
    }

    [SharedHandler(Name = "rejectAwakeable")]
    public async Task RejectAwakeable(SharedObjectContext ctx, RejectAwakeableRequest request)
    {
        ctx.RejectAwakeable(await GetAwakeableId(ctx, request.AwakeableKey), request.Reason);
    }

    [Handler(Name = "interpretCommands")]
    public async Task<string> InterpretCommands(ObjectContext ctx, JsonElement request)
    {
        var result = "";
        foreach (var command in request.GetProperty("commands").EnumerateArray())
        {
            result = await Interpret(ctx, command);

            var results = await ctx.Get(Results) ?? [];
            results.Add(result);
            ctx.Set(Results, results);
        }

        return result;
    }

    private static async Task<string> Interpret(ObjectContext ctx, JsonElement command)
    {
        switch (Type(command))
        {
            case "awaitAwakeableOrTimeout":
            {
                var awakeable = ctx.Awakeable<string>();
                ctx.Set(AwakeableKey(command.GetProperty("awakeableKey").GetString()!), awakeable.Id);
                var timeout = ctx.Timer(TimeSpan.FromMilliseconds(command.GetProperty("timeoutMillis").GetInt64()));
                await foreach (var (future, _) in ctx.WaitAll(awakeable.Future, timeout))
                {
                    if (ReferenceEquals(future, timeout))
                        throw new TerminalException("await-timeout");
                    break;
                }

                return await awakeable.Future.GetResult();
            }
            case "resolveAwakeable":
                ctx.ResolveAwakeable(await GetAwakeableId(ctx, command.GetProperty("awakeableKey").GetString()!),
                    command.GetProperty("value").GetString()!);
                return "";
            case "rejectAwakeable":
                ctx.RejectAwakeable(await GetAwakeableId(ctx, command.GetProperty("awakeableKey").GetString()!),
                    command.GetProperty("reason").GetString()!);
                return "";
            case "getEnvVariable":
            {
                var name = command.GetProperty("envName").GetString()!;
                return await ctx.Run("get_env", () => Environment.GetEnvironmentVariable(name) ?? "");
            }
            case "awaitOne":
                return await Resolve(ToFuture(ctx, command.GetProperty("command")));
            case "awaitAny" or "awaitFirstCompleted":
            {
                var futures = ToFutures(ctx, command);
                if (AllTyped(futures, out var typed))
                    return await ctx.Race(typed);
                await foreach (var (future, _) in ctx.WaitAll(futures.Select(f => f.Future).ToArray()))
                    return await Resolve(futures.First(f => ReferenceEquals(f.Future, future)));
                throw new InvalidOperationException("No future completed");
            }
            case "awaitAnySuccessful" or "awaitFirstSucceededOrAllFailed":
            {
                var futures = ToFutures(ctx, command);
                Exception? lastError = null;
                await foreach (var (future, error) in ctx.WaitAll(futures.Select(f => f.Future).ToArray()))
                {
                    if (error is null)
                        return await Resolve(futures.First(f => ReferenceEquals(f.Future, future)));
                    lastError = error;
                }

                throw lastError ?? new InvalidOperationException("No future completed");
            }
            case "awaitAllSucceededOrFirstFailed":
            {
                var futures = ToFutures(ctx, command);
                if (AllTyped(futures, out var typed))
                    return string.Join('|', await ctx.All(typed));
                await foreach (var (_, error) in ctx.WaitAll(futures.Select(f => f.Future).ToArray()))
                {
                    if (error is not null)
                        throw error;
                }

                var values = new List<string>();
                foreach (var future in futures)
                    values.Add(await Resolve(future));
                return string.Join('|', values);
            }
            case "awaitAllCompleted":
            {
                var futures = ToFutures(ctx, command);
                await foreach (var _ in ctx.WaitAll(futures.Select(f => f.Future).ToArray()))
                {
                }

                var parts = new List<string>();
                foreach (var future in futures)
                {
                    try
                    {
                        parts.Add("ok:" + await Resolve(future));
                    }
                    catch (TerminalException e)
                    {
                        parts.Add("err:" + e.Message);
                    }
                }

                return string.Join('|', parts);
            }
            default:
                throw new TerminalException($"Unknown command type '{Type(command)}'");
        }
    }

    private static List<Awaitable> ToFutures(ObjectContext ctx, JsonElement command)
    {
        return command.GetProperty("commands").EnumerateArray().Select(c => ToFuture(ctx, c)).ToList();
    }

    private static Awaitable ToFuture(ObjectContext ctx, JsonElement command)
    {
        switch (Type(command))
        {
            case "createAwakeable":
            {
                var awakeable = ctx.Awakeable<string>();
                ctx.Set(AwakeableKey(command.GetProperty("awakeableKey").GetString()!), awakeable.Id);
                return new Awaitable(awakeable.Future, awakeable.Future);
            }
            case "sleep":
                return new Awaitable(
                    ctx.Timer(TimeSpan.FromMilliseconds(command.GetProperty("timeoutMillis").GetInt64())), null);
            case "runThrowTerminalException":
            {
                var reason = command.GetProperty("reason").GetString()!;
                var run = ctx.RunAsync<string>("run should fail command", () => throw new TerminalException(reason));
                return new Awaitable(run, run);
            }
            case "createSignal":
            {
                var signal = ctx.Signal<string>(command.GetProperty("signalName").GetString()!);
                return new Awaitable(signal, signal);
            }
            case "runReturns":
            {
                var value = command.GetProperty("value").GetString()!;
                var run = ctx.RunAsync("runReturns", () => Task.FromResult(value));
                return new Awaitable(run, run);
            }
            default:
                throw new TerminalException($"Unknown awaitable command type '{Type(command)}'");
        }
    }

    private static async Task<string> Resolve(Awaitable awaitable)
    {
        if (awaitable.Typed is null)
        {
            await awaitable.Future.GetResult();
            return "sleep";
        }

        return await awaitable.Typed.GetResult();
    }

    private static bool AllTyped(List<Awaitable> futures, out IDurableFuture<string>[] typed)
    {
        typed = futures.Where(f => f.Typed is not null).Select(f => f.Typed!).ToArray();
        return typed.Length == futures.Count;
    }

    private static async Task<string> GetAwakeableId(SharedObjectContext ctx, string awakeableKey)
    {
        var id = await ctx.Get(AwakeableKey(awakeableKey));
        if (string.IsNullOrEmpty(id))
            throw new TerminalException("No awakeable is registered");
        return id;
    }

    private static StateKey<string> AwakeableKey(string awakeableKey)
    {
        return new StateKey<string>("awk-" + awakeableKey);
    }

    private static string? Type(JsonElement command)
    {
        return command.GetProperty("type").GetString();
    }

    /// <summary>A durable future plus its string-typed view; <c>Typed</c> is null for a sleep.</summary>
    private sealed record Awaitable(IDurableFuture Future, IDurableFuture<string>? Typed);
}
