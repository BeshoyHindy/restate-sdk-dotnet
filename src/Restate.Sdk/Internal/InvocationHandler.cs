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
    private static readonly ActivitySource ActivitySource = new("Restate.Sdk");
    private readonly ILoggerFactory _loggerFactory;

    public InvocationHandler(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task HandleAsync(
        PipeReader requestBodyReader,
        PipeWriter responseBodyWriter,
        ServiceDefinition service,
        HandlerDefinition handler,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        var logger = _loggerFactory.CreateLogger("Restate.Invocation");
        var jsonOptions = JsonSerde.SerializerOptions;
        using var reader = new ProtocolReader(requestBodyReader);
        using var writer = new ProtocolWriter(responseBodyWriter);

        using var sm = new InvocationStateMachine(reader, writer, jsonOptions, logger);
        using var incomingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? incomingTask = null;
        Activity? activity = null;

        try
        {
            var startInfo = await sm.StartAsync(ct).ConfigureAwait(false);

            Log.InvocationStarted(logger, service.Name, handler.Name, startInfo.InvocationId);

            activity = StartActivity(service, handler, sm.Headers, startInfo.InvocationId);

            incomingTask = sm.ProcessIncomingMessagesAsync(incomingCts.Token);

            // Use the linked token so the handler cancels when either:
            // (a) the external caller cancels, or (b) the incoming reader detects connection close.
            var handlerToken = incomingCts.Token;
            var context = CreateContext(sm, service.Type, handler.IsShared, logger, handlerToken);

            object? input = null;
            if (handler.InputDeserializer is not null)
                input = handler.InputDeserializer(new ReadOnlySequence<byte>(startInfo.Input));

            var serviceInstance = service.Factory(serviceProvider);
            var result = await handler.Invoker(serviceInstance, context, input, handlerToken).ConfigureAwait(false);

            var output = result is not null
                ? sm.SerializeObject(result)
                : ReadOnlyMemory<byte>.Empty;
            await sm.CompleteAsync(output, ct).ConfigureAwait(false);

            Log.InvocationCompleted(logger, sm.InvocationId);
        }
        catch (TerminalException ex)
        {
            Log.TerminalException(logger, sm.InvocationId, ex.Code);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailTerminalAsync(ex.Code, ex.Message, CancellationToken.None).ConfigureAwait(false); }
            catch { /* Stream already broken — nothing more we can do */ }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            activity?.Dispose();

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
            activity.SetTag("rpc.service", service.Name);
            activity.SetTag("rpc.method", handler.Name);
            activity.SetTag("rpc.system", "restate");
        }

        return activity;
    }

    private static Restate.Sdk.Context CreateContext(
        InvocationStateMachine sm, ServiceType serviceType, bool isShared, ILogger logger, CancellationToken ct)
    {
        return serviceType switch
        {
            ServiceType.Service => new DefaultContext(sm, logger, ct),
            ServiceType.VirtualObject => isShared
                ? new DefaultSharedObjectContext(sm, logger, ct)
                : new DefaultObjectContext(sm, logger, ct),
            ServiceType.Workflow => isShared
                ? new DefaultSharedWorkflowContext(sm, logger, ct)
                : new DefaultWorkflowContext(sm, logger, ct),
            _ => new DefaultContext(sm, logger, ct)
        };
    }
}