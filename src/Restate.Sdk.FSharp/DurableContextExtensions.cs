namespace Restate.Sdk.FSharp;

/// <summary>
///     Context extension methods that present the durable primitives to F# without the friction F# hits
///     against the raw SDK surface: every method returns <see cref="Task" />/<see cref="Task{T}" /> rather
///     than <c>ValueTask</c> (F#'s <c>task { }</c> binds <c>Task</c> cleanly), and the overloads are named
///     distinctly so F# never has to disambiguate <c>Func&lt;Task&lt;T&gt;&gt;</c> from <c>Func&lt;T&gt;</c>.
///
///     They are plain instance-style calls from F# — <c>ctx.RunAsync("step", fun () -> ...)</c> — because
///     F# consumes C# extension methods. The generic context receiver (<c>SharedObjectContext</c> /
///     <c>ObjectContext</c>) means a <c>WorkflowContext</c> flows in by ordinary inheritance, side-stepping
///     F#'s lack of implicit argument upcasting.
/// </summary>
public static class DurableContextExtensions
{
    // Named RunStep (not RunAsync) because the SDK already defines Context.RunAsync, which returns a
    // detached IDurableFuture<T> — an instance method would shadow this extension and break `let!`.

    /// <summary>Durable side effect returning a value — journaled once, replayed verbatim afterwards.</summary>
    public static Task<T> RunStep<T>(this Context context, string name, Func<Task<T>> action)
        => context.Run(name, action).AsTask();

    /// <summary>Durable side effect with an explicit retry policy.</summary>
    public static Task<T> RunStep<T>(this Context context, string name, RetryPolicy retryPolicy, Func<Task<T>> action)
        => context.Run(name, action, retryPolicy).AsTask();

    /// <summary>Durable side effect with no return value.</summary>
    public static Task RunStepUnit(this Context context, string name, Func<Task> action)
        => context.Run(name, action).AsTask();

    /// <summary>Reads durable per-key state (default value when unset).</summary>
    public static Task<T?> GetAsync<T>(this SharedObjectContext context, StateKey<T> key)
        => context.Get(key).AsTask();

    /// <summary>Lists every state key currently set for this key.</summary>
    public static Task<string[]> StateKeysAsync(this SharedObjectContext context)
        => context.StateKeys().AsTask();

    /// <summary>Writes durable per-key state.</summary>
    public static void SetState<T>(this ObjectContext context, StateKey<T> key, T value)
        => context.Set(key, value);

    /// <summary>Clears every state key for this object/workflow key.</summary>
    public static void ClearAllState(this ObjectContext context)
        => context.ClearAll();

    /// <summary>Creates a durable promise (awakeable) an external caller can resolve or reject by id.</summary>
    public static Awakeable<T> NewAwakeable<T>(this SharedObjectContext context)
        => context.Awakeable<T>();
}
