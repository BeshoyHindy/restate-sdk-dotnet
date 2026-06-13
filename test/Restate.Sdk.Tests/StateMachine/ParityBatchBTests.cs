using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Parity Batch B — closes shared-core v0.10.0 gaps G9/G11/G12 at the state-machine / protocol
///     level (audit doc docs/research/shared-core/09-parity-audit.md):
///
///       * G9  — a journal/command mismatch carries JOURNAL_MISMATCH (570); any other protocol
///         violation carries PROTOCOL_VIOLATION (571). Both surface via the terminal error path
///         instead of collapsing to 500 (vm/errors.rs:69-70,390-396).
///       * G11 — terminal-error Failure.metadata (V6) round-trips: emitted on the outgoing
///         OutputCommand failure ONLY when the negotiated protocol version is V6+, dropped on V5;
///         parsed off an incoming failure notification onto the surfaced TerminalException
///         (proto:633-646; verify_error_metadata_feature_support vm/mod.rs:118-124).
///       * G12 — the negotiated protocol version is threaded into the SM and feature-gates V6
///         metadata emission (Context.negotiated_protocol_version analogue).
///
///     Every wait is bounded by <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" />.
/// </summary>
public class ParityBatchBTests
{
    private const int WatchdogMs = 10_000;
    private const string Service = "Greeter";
    private const string Handler = "Greet";

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // ---- G9: protocol-violation error codes (570 / 571) --------------------------------------

    /// <summary>
    ///     G9 — a journaled command-TYPE mismatch (RunCommand journaled, handler runs Sleep) throws a
    ///     ProtocolException carrying JOURNAL_MISMATCH (570), NOT the old generic 500.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task JournalTypeMismatch_CarriesJournalMismatchCode570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.RunCommand, CreateRunCommand("x", 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None).AsTask()));
        Assert.Equal(ProtocolException.JournalMismatchCode, ex.Code);
        Assert.Equal(570, ex.Code);
    }

    /// <summary>
    ///     G9 — a journaled command-NAME mismatch (RunCommand name "a", handler name "b") also carries
    ///     JOURNAL_MISMATCH (570): CommandMismatchError → 570 (errors.rs:396).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task JournalNameMismatch_CarriesJournalMismatchCode570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.RunCommand, CreateRunCommand("a", 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.RunAsync<int>("b",
                () => Task.FromResult(0), CancellationToken.None).AsTask()));
        Assert.Equal(570, ex.Code);
    }

    /// <summary>
    ///     G9 — a non-deterministic completion-id divergence on a replayed Call (journaled target on a
    ///     different service) is a command-header mismatch → JOURNAL_MISMATCH (570).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task CallTargetMismatch_CarriesJournalMismatchCode570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2,
            (MessageType.CallCommand, ProtobufCodec.CreateCallCommand(
                "ServiceA", "Handler", "k", Array.Empty<byte>(), 2, 1).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            AwaitBounded(rig.StateMachine.CallAsync<string>(
                "ServiceB", "k", "Handler", null, CancellationToken.None).AsTask()));
        Assert.Equal(570, ex.Code);
    }

    /// <summary>
    ///     G9 — a generic protocol violation (an unavailable replay entry: empty queue) defaults to
    ///     PROTOCOL_VIOLATION (571), distinct from the 570 journal-mismatch family
    ///     (UnavailableEntryError → PROTOCOL_VIOLATION, errors.rs:387).
    /// </summary>
    [Fact]
    public void UnavailableEntry_CarriesProtocolViolationCode571()
    {
        var journal = new Restate.Sdk.Internal.Journal.InvocationJournal();
        var ex = Assert.Throws<ProtocolException>(() =>
            journal.DequeueReplay(JournalEntryType.Run, "x"));
        Assert.Equal(ProtocolException.ProtocolViolationCode, ex.Code);
        Assert.Equal(571, ex.Code);
    }

    /// <summary>
    ///     G9 — the default ProtocolException (no explicit code) is a PROTOCOL_VIOLATION (571): every
    ///     unclassified protocol fault surfaces as 571 rather than collapsing to 500.
    /// </summary>
    [Fact]
    public void DefaultProtocolException_Is571()
    {
        Assert.Equal(571, new ProtocolException("boom").Code);
        Assert.Equal(571, new ProtocolException("boom", new InvalidOperationException()).Code);
    }

    // ---- G11/G12: outgoing Failure.metadata gated on negotiated version -----------------------

    /// <summary>
    ///     G11/G12 — a terminal failure WITH metadata, on a V6 negotiation, emits the
    ///     OutputCommand failure carrying the V6 Failure.metadata (round-trip out).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalFailure_WithMetadata_OnV6_EmitsFailureMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 6;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var metadata = new Dictionary<string, string> { ["reason"] = "out-of-stock", ["sku"] = "A-42" };
        await AwaitBounded(rig.StateMachine.FailTerminalAsync(409, "rejected", CancellationToken.None, metadata));

        rig.CompleteInbound();
        var output = ReadOutputFailure(await ReadAllOutboundFromReaderAsync(rig));
        Assert.Equal(409u, output.Code);
        Assert.Equal("rejected", output.Message);
        Assert.Equal(2, output.Metadata.Count);
        var pairs = output.Metadata.ToDictionary(m => m.Key, m => m.Value);
        Assert.Equal("out-of-stock", pairs["reason"]);
        Assert.Equal("A-42", pairs["sku"]);
    }

    /// <summary>
    ///     G11/G12 — the SAME terminal failure WITH metadata, on a V5 negotiation, DROPS the metadata:
    ///     the OutputCommand failure carries the code/message but an EMPTY metadata list (sub-V6 gate).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalFailure_WithMetadata_OnV5_DropsFailureMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 5;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        var metadata = new Dictionary<string, string> { ["reason"] = "out-of-stock" };
        await AwaitBounded(rig.StateMachine.FailTerminalAsync(409, "rejected", CancellationToken.None, metadata));

        rig.CompleteInbound();
        var output = ReadOutputFailure(await ReadAllOutboundFromReaderAsync(rig));
        Assert.Equal(409u, output.Code);
        Assert.Equal("rejected", output.Message);
        Assert.Empty(output.Metadata);
    }

    /// <summary>
    ///     G11 — a terminal failure with NO metadata never emits a metadata list regardless of version
    ///     (V6 negotiation, empty metadata → empty repeated field, no spurious entries).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task TerminalFailure_WithoutMetadata_OnV6_EmitsEmptyMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 6;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);

        await AwaitBounded(rig.StateMachine.FailTerminalAsync(500, "boom", CancellationToken.None));

        rig.CompleteInbound();
        var output = ReadOutputFailure(await ReadAllOutboundFromReaderAsync(rig));
        Assert.Empty(output.Metadata);
    }

    // ---- G11: outgoing run-proposal Failure.metadata gated on negotiated version --------------

    /// <summary>
    ///     G11 — a Run whose closure throws a TerminalException with metadata, on V6, proposes the run
    ///     completion FAILURE carrying that V6 Failure.metadata (round-trip out on the run path).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RunProposalFailure_WithMetadata_OnV6_EmitsMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 6;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var runTask = rig.StateMachine.RunAsync<int>("r",
            () => throw new TerminalException("nope", 422, metadata), CancellationToken.None).AsTask();

        // The proposed failure re-raises after the runtime delivers its completion.
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletionFailure(1, 422, "nope").ToByteArray());
        await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundFromReaderAsync(rig);
        var proposal = Assert.Single(outbound, f => f.Header.Type == MessageType.ProposeRunCompletion);
        var msg = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload);
        Assert.Equal(Gen.ProposeRunCompletionMessage.ResultOneofCase.Failure, msg.ResultCase);
        var entry = Assert.Single(msg.Failure.Metadata);
        Assert.Equal("k", entry.Key);
        Assert.Equal("v", entry.Value);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G11 — the SAME Run failure with metadata, on V5, DROPS the metadata from the proposed run
    ///     completion (OutgoingFailureMetadata returns null on sub-V6 — the gate's false branch).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RunProposalFailure_WithMetadata_OnV5_DropsMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 5;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var runTask = rig.StateMachine.RunAsync<int>("r",
            () => throw new TerminalException("nope", 422, metadata), CancellationToken.None).AsTask();
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletionFailure(1, 422, "nope").ToByteArray());
        await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundFromReaderAsync(rig);
        var proposal = Assert.Single(outbound, f => f.Header.Type == MessageType.ProposeRunCompletion);
        var msg = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload);
        Assert.Empty(msg.Failure.Metadata);

        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G11 — a Run failure with NO metadata on V6 proposes an empty-metadata failure
    ///     (OutgoingFailureMetadata returns null when there is nothing to carry — the count==0 branch).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task RunProposalFailure_WithoutMetadata_OnV6_EmitsEmptyMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = 6;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var runTask = rig.StateMachine.RunAsync<int>("r",
            () => throw new TerminalException("nope", 422), CancellationToken.None).AsTask();
        await rig.DeliverAsync(MessageType.RunCompletion,
            CreateRunCompletionFailure(1, 422, "nope").ToByteArray());
        await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(runTask));

        rig.CompleteInbound();
        var outbound = await ReadAllOutboundFromReaderAsync(rig);
        var proposal = Assert.Single(outbound, f => f.Header.Type == MessageType.ProposeRunCompletion);
        var msg = Gen.ProposeRunCompletionMessage.Parser.ParseFrom(proposal.Payload);
        Assert.Empty(msg.Failure.Metadata);

        await AwaitBounded(pump);
    }

    // ---- G11: incoming Failure.metadata parsed onto the surfaced TerminalException ------------

    /// <summary>
    ///     G11 — an incoming Call failure notification carrying Failure.metadata surfaces a
    ///     TerminalException whose Metadata exposes those pairs (round-trip in). Parsing-in is
    ///     version-agnostic: a sub-V6 runtime simply never sends any.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task IncomingCallFailure_WithMetadata_SurfacesOnTerminalException()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletionFailureWithMetadata(2, 409, "conflict",
                ("retry-after", "30"), ("scope", "tenant")));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(callTask.AsTask()));
        Assert.Equal((ushort)409, ex.Code);
        Assert.Equal("30", ex.Metadata["retry-after"]);
        Assert.Equal("tenant", ex.Metadata["scope"]);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G11 — an incoming Call failure with NO metadata surfaces a TerminalException with an EMPTY
    ///     metadata map (the common <V6 / no-metadata case), never null.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task IncomingCallFailure_WithoutMetadata_HasEmptyMetadata()
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletionFailure(2, 500, "boom").ToByteArray());

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(callTask.AsTask()));
        Assert.Empty(ex.Metadata);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    /// <summary>
    ///     G11 — the round trip composes: a V6 SM that re-raises an incoming metadata-bearing failure as
    ///     its OWN terminal Output re-emits the SAME Failure.metadata (in → surfaced → out).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task IncomingFailureMetadata_RoundTripsBackOut_OnV6()
    {
        var caught = await CaptureIncomingTerminalAsync(version: 6);
        Assert.Equal("yes", caught.Metadata["propagate"]);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    ///     Drives a Call that fails with metadata and returns the surfaced TerminalException — shared by
    ///     the round-trip composition test so the in→surface step is asserted in one place.
    /// </summary>
    private static async Task<TerminalException> CaptureIncomingTerminalAsync(int version)
    {
        using var rig = new StateMachineRig();
        rig.StateMachine.NegotiatedProtocolVersion = version;
        rig.StateMachine.Initialize("inv-1", "", 0, 1);
        var pump = rig.StateMachine.ProcessIncomingMessagesAsync(CancellationToken.None);

        var callTask = rig.StateMachine.CallAsync<string>(
            Service, null, Handler, "hi", null, null, null, CancellationToken.None);
        await rig.DeliverAsync(MessageType.CallCompletion,
            CreateCallCompletionFailureWithMetadata(2, 409, "conflict", ("propagate", "yes")));

        var ex = await Assert.ThrowsAsync<TerminalException>(() => AwaitBounded(callTask.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
        return ex;
    }

    private static Gen.Failure ReadOutputFailure(
        IReadOnlyList<(MessageHeader Header, byte[] Payload)> outbound)
    {
        var frame = Assert.Single(outbound, f => f.Header.Type == MessageType.OutputCommand);
        var msg = Gen.OutputCommandMessage.Parser.ParseFrom(frame.Payload);
        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Failure, msg.ResultCase);
        return msg.Failure;
    }

    private static Gen.CallCompletionNotificationMessage CreateCallCompletionFailureWithMetadata(
        uint completionId, uint code, string message, params (string Key, string Value)[] metadata)
    {
        var failure = new Gen.Failure { Code = code, Message = message };
        foreach (var (key, value) in metadata)
            failure.Metadata.Add(new Gen.FailureMetadata { Key = key, Value = value });
        return new Gen.CallCompletionNotificationMessage { CompletionId = completionId, Failure = failure };
    }

    /// <summary>
    ///     Drains the rig's outbound frames via a bounded reader (used when the SM stays open — the
    ///     writer side is not completed, so ReadAllOutboundAsync would block forever).
    /// </summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> ReadAllOutboundFromReaderAsync(
        StateMachineRig rig)
    {
        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await reader.ReadMessageAsync(cts.Token).ConfigureAwait(false) is { } message)
            {
                frames.Add((message.Header, message.Payload.ToArray()));
                message.Dispose();
            }
        }
        catch (OperationCanceledException) { /* no more buffered frames */ }

        return frames;
    }

    /// <summary>
    ///     Starts the rig in replay mode: delivers Start{known_entries} + Input + the supplied journaled
    ///     frames, then runs StartAsync to buffer them. Mirrors the ReplayTests harness convention.
    /// </summary>
    private static async Task StartReplayAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, byte[] Payload)[] journaled)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in journaled)
            await rig.DeliverAsync(type, payload);
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }
}
