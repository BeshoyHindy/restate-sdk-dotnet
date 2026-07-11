using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.Serde;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal;

internal sealed class InvocationHandler
{
    internal static readonly ActivitySource ActivitySource = new("Restate.Sdk");
    private readonly ILoggerFactory _loggerFactory;
    private readonly RestateTelemetryOptions _telemetryOptions;

    public InvocationHandler(ILoggerFactory? loggerFactory = null, RestateTelemetryOptions? telemetryOptions = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _telemetryOptions = telemetryOptions ?? new RestateTelemetryOptions();
    }

    public async Task HandleAsync(
        PipeReader requestBodyReader,
        PipeWriter responseBodyWriter,
        ServiceDefinition service,
        HandlerDefinition handler,
        IServiceProvider serviceProvider,
        ServiceProtocolVersion protocolVersion,
        CancellationToken ct)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var outcome = InvocationOutcome.Error;
        var logger = _loggerFactory.CreateLogger("Restate.Invocation");
        var jsonOptions = JsonSerde.SerializerOptions;
        using var reader = new ProtocolReader(requestBodyReader);
        using var writer = new ProtocolWriter(responseBodyWriter);

        using var sm = new InvocationStateMachine(reader, writer, jsonOptions, logger, protocolVersion,
            _telemetryOptions.EnableOperationActivities);
        using var incomingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? incomingTask = null;
        Activity? activity = null;
        IDisposable? invocationLogScope = null;

        try
        {
            var startInfo = await sm.StartAsync(ct).ConfigureAwait(false);

            Log.InvocationStarted(logger, service.Name, handler.Name, startInfo.InvocationId);

            activity = StartActivity(service, handler, sm.Headers, startInfo.InvocationId);

            // ctx.Logger: replay-aware wrapper over a handler-facing logger, with an
            // invocation-id scope spanning the handler execution. Skipped entirely when
            // no logger factory is configured (e.g. Lambda without UseLoggerFactory).
            ILogger contextLogger = NullLogger.Instance;
            if (!ReferenceEquals(_loggerFactory, NullLoggerFactory.Instance))
            {
                var handlerLogger = _loggerFactory.CreateLogger(service.Name);
                contextLogger = new ReplayAwareLogger(handlerLogger, () => sm.IsReplaying);
                invocationLogScope = handlerLogger.BeginScope(new InvocationLogScope(startInfo.InvocationId));
            }

            incomingTask = sm.ProcessIncomingMessagesAsync(incomingCts.Token);

            // Use the linked token so the handler cancels when either:
            // (a) the external caller cancels, or (b) the incoming reader detects connection close.
            var handlerToken = incomingCts.Token;
            var context = CreateContext(sm, service.Type, handler.IsShared, contextLogger, handlerToken);

            object? input = null;
            if (handler.InputDeserializer is not null)
                input = handler.InputDeserializer(new ReadOnlySequence<byte>(startInfo.Input));

            var serviceInstance = service.Factory(serviceProvider);
            var result = await handler.Invoker(serviceInstance, context, input, handlerToken).ConfigureAwait(false);

            // Defensive: never write Output/End after a SuspensionMessage. Catching Exception
            // around durable awaits in user code is unsupported (same caveat as other SDKs) —
            // if the SuspensionException was swallowed there, the invocation state still wins.
            if (sm.State != InvocationState.Suspended)
            {
                var output = result is not null
                    ? sm.SerializeObject(result)
                    : ReadOnlyMemory<byte>.Empty;
                await sm.CompleteAsync(output, ct).ConfigureAwait(false);
                outcome = InvocationOutcome.Success;

                Log.InvocationCompleted(logger, sm.InvocationId);
            }
            else
            {
                outcome = InvocationOutcome.Suspended;
            }
        }
        catch (TerminalException ex)
        {
            outcome = InvocationOutcome.TerminalError;
            Log.TerminalException(logger, sm.InvocationId, ex.Code);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailTerminalAsync(ex.Code, ex.Message, CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken — nothing more we can do */ }
        }
        catch (SuspensionException)
        {
            outcome = InvocationOutcome.Suspended;
            // The input stream closed while the handler awaited a durable result — suspend.
            Log.InvocationSuspending(logger, sm.InvocationId);
            try { await sm.SuspendAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken — nothing more we can do */ }
        }
        catch (OperationCanceledException) when (sm.InputClosed && !ct.IsCancellationRequested)
        {
            outcome = InvocationOutcome.Suspended;
            // The poisoned durable wait surfaced wrapped as a cancellation (e.g. user code
            // converted the SuspensionException). Input EOF means no completion can ever
            // arrive, so this is still a suspension, not a failure.
            Log.InvocationSuspending(logger, sm.InvocationId);
            try { await sm.SuspendAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken — nothing more we can do */ }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = InvocationOutcome.Cancelled;
            Log.InvocationCancelled(logger, sm.InvocationId);
        }
        catch (ProtocolException ex)
        {
            Log.ProtocolError(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailAsync(500, ex.Message, CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        catch (Exception ex)
        {
            Log.InvocationFailed(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailAsync(500, ex.Message, CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        finally
        {
            invocationLogScope?.Dispose();

            if (activity is not null)
            {
                activity.SetTag("restate.journal.commands", sm.JournalCommandCount);
                activity.SetTag("restate.replayed", sm.ReplayedCommandCount > 1);
                activity.Dispose();
            }

            RestateMetrics.RecordInvocation(
                service.Name, handler.Name, outcome,
                Stopwatch.GetElapsedTime(startTimestamp), sm.ReplayedCommandCount);

            // Each cleanup operation is wrapped in try-catch because the stream may already
            // be in a broken state. Without this, a failure in any cleanup step would prevent
            // the remaining cleanup from executing AND propagate an exception to Kestrel.
            try { writer.Complete(); } catch { /* already completed or broken */ }

            // Cancel and await the incoming reader task BEFORE completing the reader.
            // This avoids a "Concurrent reads or writes are not supported" race between
            // PipeReader.Complete() and ProcessIncomingMessagesAsync's pending ReadAsync().
            try { await incomingCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            if (incomingTask is not null)
                try
                {
                    await incomingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    Log.IncomingReaderStopped(logger, ex, sm.InvocationId);
                }
                catch (Exception ex)
                {
                    // The reader faulted (e.g. a trailing malformed frame) after the invocation
                    // outcome was already decided and written. Log it — rethrowing here would
                    // discard the completed response (on Lambda: turn success into a retry).
                    Log.IncomingReaderFailed(logger, ex, sm.InvocationId);
                }

            try { reader.Complete(); } catch { /* already completed or broken */ }
        }
    }

    private static Activity? StartActivity(
        ServiceDefinition service, HandlerDefinition handler,
        IReadOnlyDictionary<string, string> headers, string invocationId)
    {
        ActivityContext parentContext = default;

        if (headers.TryGetValue("traceparent", out var traceparent))
        {
            headers.TryGetValue("tracestate", out var tracestate);
            ActivityContext.TryParse(traceparent, tracestate, out parentContext);
        }

        var activity = ActivitySource.StartActivity(
            $"{service.Name}/{handler.Name}",
            ActivityKind.Server,
            parentContext);

        if (activity is not null)
        {
            activity.SetTag("restate.invocation.id", invocationId);
            activity.SetTag("restate.service", service.Name);
            activity.SetTag("restate.handler", handler.Name);
            activity.SetTag("rpc.service", service.Name);
            activity.SetTag("rpc.method", handler.Name);
            activity.SetTag("rpc.system", "restate");
        }

        return activity;
    }

    private static Restate.Sdk.Context CreateContext(
        InvocationStateMachine sm, ServiceType serviceType, bool isShared, ILogger contextLogger,
        CancellationToken ct)
    {
        return serviceType switch
        {
            ServiceType.Service => new DefaultContext(sm, contextLogger, ct),
            ServiceType.VirtualObject => isShared
                ? new DefaultSharedObjectContext(sm, contextLogger, ct)
                : new DefaultObjectContext(sm, contextLogger, ct),
            ServiceType.Workflow => isShared
                ? new DefaultSharedWorkflowContext(sm, contextLogger, ct)
                : new DefaultWorkflowContext(sm, contextLogger, ct),
            _ => new DefaultContext(sm, contextLogger, ct)
        };
    }
}