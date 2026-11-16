using System.IO.Pipelines;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plays the runtime's part in a run for tests that drive a state machine over bare pipes:
///     reads every frame the state machine writes and completes each proposed run with the
///     outcome it proposed, as the runtime does once that outcome is stored. A live run waits for
///     that completion, so the test starts this before running one.
/// </summary>
internal sealed class RunAcknowledger
{
    private readonly Task<List<(MessageType Type, byte[] Payload)>> _frames;

    private RunAcknowledger(Task<List<(MessageType Type, byte[] Payload)>> frames)
    {
        _frames = frames;
    }

    /// <summary>
    ///     Every frame the state machine wrote, available once <paramref name="outbound" /> was
    ///     completed by the test.
    /// </summary>
    public Task<List<(MessageType Type, byte[] Payload)>> Frames => _frames;

    /// <summary>
    ///     Starts the state machine's incoming-message loop and the acknowledging reader. Call it
    ///     once the state machine is processing: after <c>Initialize</c>, or after <c>StartAsync</c>
    ///     consumed the replayed prefix.
    /// </summary>
    public static RunAcknowledger Start(InvocationStateMachine sm, PipeReader outbound, PipeWriter inbound)
    {
        _ = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        return new RunAcknowledger(Task.Run(() => AcknowledgeAsync(outbound, inbound)));
    }

    /// <summary>The notification the runtime sends once it stored <paramref name="proposal" />.</summary>
    public static Gen.RunCompletionNotificationMessage CompletionFor(Gen.ProposeRunCompletionMessage proposal)
    {
        var completion = new Gen.RunCompletionNotificationMessage { CompletionId = proposal.ResultCompletionId };
        if (proposal.ResultCase == Gen.ProposeRunCompletionMessage.ResultOneofCase.Failure)
            completion.Failure = proposal.Failure;
        else
            completion.Value = new Gen.Value { Content = proposal.Value };
        return completion;
    }

    private static async Task<List<(MessageType Type, byte[] Payload)>> AcknowledgeAsync(PipeReader outbound,
        PipeWriter inbound)
    {
        var frames = new List<(MessageType Type, byte[] Payload)>();
        using var reader = new ProtocolReader(outbound);
        while (await reader.ReadMessageAsync() is { } message)
        {
            using (message)
                frames.Add((message.Header.Type, message.Payload.ToArray()));

            if (frames[^1].Type != MessageType.ProposeRunCompletion)
                continue;

            var proposal = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(frames[^1].Payload);
            var payload = CompletionFor(proposal).ToByteArray();
            var header = new byte[MessageHeader.Size];
            MessageHeader.Create(MessageType.RunCompletion, MessageFlags.None, (uint)payload.Length).Write(header);
            await inbound.WriteAsync(header);
            await inbound.WriteAsync(payload);
        }

        return frames;
    }
}
