using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.Protocol;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Protocol;

/// <summary>
///     Plan 07 §1.2 4b-iii (ProtobufCodecEdgeTests). Covers the <see cref="ProtobufCodec" /> surface
///     the §4 suites and the existing ProtobufParserTests do NOT hit:
///       * <see cref="ProtobufCodec.ParseReplayCommand" /> over EVERY <see cref="MessageType" />
///         command case (table-driven, asserting EntryType / Name / ids / eager fields) plus the
///         H12 default arm (unknown command type → <see cref="ProtocolException" />);
///       * the size/write helpers, the eager-keys and lazy-keys factories, the suspension factory
///         (empty + both lists), and <see cref="ProtobufCodec.CreateCancelInvocationCommand" />
///         which must use the shared <see cref="ProtobufCodec.CancelSignalId" /> constant.
/// </summary>
public class ProtobufCodecEdgeTests
{
    // ---- ParseReplayCommand: one row per command MessageType (blueprint 2.3(b) parity) ---------

    public static IEnumerable<object[]> ReplayCommandCases()
    {
        // OutputCommand carries no fields the preflight reads — just the EntryType marker.
        yield return Row(MessageType.OutputCommand, new Gen.OutputCommandMessage(), JournalEntryType.Output);

        yield return Row(MessageType.GetLazyStateCommand,
            ProtobufCodec.CreateGetStateCommand("k1", 5), JournalEntryType.GetState,
            name: "k1", resultId: 5);

        yield return Row(MessageType.GetEagerStateCommand,
            ProtobufCodec.CreateGetEagerStateCommand("k2", JsonSerializer.SerializeToUtf8Bytes("v")),
            JournalEntryType.GetState, name: "k2", hasEager: true);

        yield return Row(MessageType.GetEagerStateCommand,
            ProtobufCodec.CreateGetEagerStateCommand("k3", null),
            JournalEntryType.GetState, name: "k3", hasEager: true, eagerVoid: true);

        yield return Row(MessageType.GetLazyStateKeysCommand,
            ProtobufCodec.CreateGetStateKeysCommand(9), JournalEntryType.GetStateKeys, resultId: 9);

        yield return Row(MessageType.GetEagerStateKeysCommand,
            ProtobufCodec.CreateGetEagerStateKeysCommand(["a", "b"]),
            JournalEntryType.GetStateKeys, hasEager: true);

        yield return Row(MessageType.SetStateCommand,
            ProtobufCodec.CreateSetStateCommand("sk", JsonSerializer.SerializeToUtf8Bytes(1)),
            JournalEntryType.SetState, name: "sk");

        yield return Row(MessageType.ClearStateCommand,
            ProtobufCodec.CreateClearStateCommand("ck"), JournalEntryType.ClearState, name: "ck");

        yield return Row(MessageType.ClearAllStateCommand,
            ProtobufCodec.CreateClearAllStateCommand(), JournalEntryType.ClearAllState);

        yield return Row(MessageType.SleepCommand,
            ProtobufCodec.CreateSleepCommand(123, 7), JournalEntryType.Sleep, resultId: 7);

        yield return Row(MessageType.CallCommand,
            ProtobufCodec.CreateCallCommand("Svc", "H", "key", JsonSerializer.SerializeToUtf8Bytes("p"), 11, 10),
            JournalEntryType.Call, resultId: 11, invocationIdx: 10,
            service: "Svc", handler: "H", key: "key");

        yield return Row(MessageType.OneWayCallCommand,
            ProtobufCodec.CreateSendCommand("Svc2", "H2", "k2", JsonSerializer.SerializeToUtf8Bytes("p"), 0, null, 12),
            JournalEntryType.OneWayCall, invocationIdx: 12,
            service: "Svc2", handler: "H2", key: "k2");

        yield return Row(MessageType.SendSignalCommand,
            ProtobufCodec.CreateCancelInvocationCommand("inv_target"), JournalEntryType.SendSignal);

        yield return Row(MessageType.RunCommand,
            ProtobufCodec.CreateRunCommand("run-name", 3), JournalEntryType.Run, name: "run-name", resultId: 3);

        yield return Row(MessageType.GetPromiseCommand,
            ProtobufCodec.CreateGetPromiseCommand("p", 4), JournalEntryType.GetPromise, name: "p", resultId: 4);

        yield return Row(MessageType.PeekPromiseCommand,
            ProtobufCodec.CreatePeekPromiseCommand("p", 6), JournalEntryType.PeekPromise, name: "p", resultId: 6);

        yield return Row(MessageType.CompletePromiseCommand,
            ProtobufCodec.CreateCompletePromiseSuccess("p", JsonSerializer.SerializeToUtf8Bytes("v"), 8),
            JournalEntryType.CompletePromise, name: "p", resultId: 8);

        yield return Row(MessageType.AttachInvocationCommand,
            ProtobufCodec.CreateAttachInvocationCommand("inv", 13), JournalEntryType.AttachInvocation, resultId: 13);

        yield return Row(MessageType.GetInvocationOutputCommand,
            ProtobufCodec.CreateGetInvocationOutputCommand("inv", 14),
            JournalEntryType.GetInvocationOutput, resultId: 14);

        yield return Row(MessageType.CompleteAwakeableCommand,
            ProtobufCodec.CreateCompleteAwakeableSuccess("sign_1", JsonSerializer.SerializeToUtf8Bytes("v")),
            JournalEntryType.CompleteAwakeable);
    }

    // MemberData rows carry MessageType/JournalEntryType as their underlying integer values: the
    // [Theory] method is public (xUnit requirement) and cannot expose the INTERNAL enum types as
    // parameters (CS0051), so the enums are reconstituted inside the test body via the cast.
    private static object[] Row(MessageType type, IMessage message, JournalEntryType entryType,
        string? name = null, uint resultId = 0, uint invocationIdx = 0,
        bool hasEager = false, bool eagerVoid = false,
        string? service = null, string? handler = null, string? key = null) =>
        [(ushort)type, message.ToByteArray(), (int)entryType, name!, resultId, invocationIdx,
            hasEager, eagerVoid, service!, handler!, key!];

    [Theory]
    [MemberData(nameof(ReplayCommandCases))]
    public void ParseReplayCommand_DecodesEveryCommandCase(ushort typeValue, byte[] payload,
        int entryTypeValue, string? name, uint resultId, uint invocationIdx,
        bool hasEager, bool eagerVoid, string? service, string? handler, string? key)
    {
        var type = (MessageType)typeValue;
        var entryType = (JournalEntryType)entryTypeValue;
        var command = ProtobufCodec.ParseReplayCommand(type, payload);

        Assert.Equal(type, command.MessageType);
        Assert.Equal(entryType, command.EntryType);
        if (name is not null) Assert.Equal(name, command.Name);
        Assert.Equal(resultId, command.ResultCompletionId);
        Assert.Equal(invocationIdx, command.InvocationIdNotificationIdx);
        Assert.Equal(hasEager, command.HasEagerResult);
        if (hasEager) Assert.Equal(eagerVoid, command.EagerIsVoid);
        if (service is not null) Assert.Equal(service, command.TargetService);
        if (handler is not null) Assert.Equal(handler, command.TargetHandler);
        if (key is not null) Assert.Equal(key, command.TargetKey);
    }

    [Fact]
    public void ParseReplayCommand_UnknownCommandType_Throws()
    {
        // H12: a notification-typed frame is not a command — the default arm rejects it. Using a type
        // outside the command range proves the "Unknown replayed command type" guard fires.
        var ex = Assert.Throws<ProtocolException>(() =>
            ProtobufCodec.ParseReplayCommand(MessageType.RunCompletion, []));
        Assert.Contains("Unknown replayed command type", ex.Message);
    }

    [Fact]
    public void ParseReplayCommand_GetEagerStateKeys_RoundTripsKeysAsJson()
    {
        var msg = ProtobufCodec.CreateGetEagerStateKeysCommand(["x", "y", "z"]);
        var command = ProtobufCodec.ParseReplayCommand(MessageType.GetEagerStateKeysCommand, msg.ToByteArray());

        Assert.True(command.HasEagerResult);
        var keys = JsonSerializer.Deserialize<string[]>(command.EagerValue.Span);
        Assert.NotNull(keys);
        Assert.Equal(["x", "y", "z"], keys);
    }

    [Fact]
    public void ParseReplayCommand_GetEagerStateKeys_WithUnsetValue_DecodesToEmptyKeyArray()
    {
        // ProtobufCodec.cs:118 — the `m.Value?.Keys.Count ?? 0` null-conditional. A
        // GetEagerStateKeysCommand whose StateKeys `value` oneof is UNSET (an object with no eager
        // keys) parses with m.Value == null, so the key count must coalesce to 0 and the eager value
        // round-trips as an empty JSON array — the null arm the populated-keys tests never reach.
        var msg = new Gen.GetEagerStateKeysCommandMessage();   // Value left unset
        Assert.Null(msg.Value);

        var command = ProtobufCodec.ParseReplayCommand(MessageType.GetEagerStateKeysCommand, msg.ToByteArray());

        Assert.True(command.HasEagerResult);
        var keys = JsonSerializer.Deserialize<string[]>(command.EagerValue.Span);
        Assert.NotNull(keys);
        Assert.Empty(keys);
    }

    // ---- Size/write helpers -------------------------------------------------------------------

    [Fact]
    public void CalculateSize_And_WriteTo_RoundTripThroughTheCodec()
    {
        var message = ProtobufCodec.CreateRunCommand("hello", 1);
        var size = ProtobufCodec.CalculateSize(message);
        Assert.True(size > 0);

        var buffer = new byte[size];
        ProtobufCodec.WriteTo(message, buffer);

        var parsed = Gen.RunCommandMessage.Parser.ParseFrom(buffer);
        Assert.Equal("hello", parsed.Name);
        Assert.Equal(1u, parsed.ResultCompletionId);
    }

    // ---- Suspension factory: empty and both-lists --------------------------------------------

    [Fact]
    public void CreateSuspensionMessage_EmptyLists_ProducesEmptyMessage()
    {
        var msg = ProtobufCodec.CreateSuspensionMessage([], []);
        Assert.Empty(msg.WaitingCompletions);
        Assert.Empty(msg.WaitingSignals);
    }

    [Fact]
    public void CreateSuspensionMessage_BothLists_ArePopulated()
    {
        var msg = ProtobufCodec.CreateSuspensionMessage([3u, 5u], [17u, 18u]);
        Assert.Equal([3u, 5u], msg.WaitingCompletions);
        Assert.Equal([17u, 18u], msg.WaitingSignals);
    }

    // ---- Cancel uses the shared CancelSignalId constant ---------------------------------------

    [Fact]
    public void CreateCancelInvocationCommand_UsesCancelSignalId()
    {
        var msg = ProtobufCodec.CreateCancelInvocationCommand("inv_x");
        Assert.Equal("inv_x", msg.TargetInvocationId);
        Assert.Equal(ProtobufCodec.CancelSignalId, msg.Idx);
        Assert.Equal(1u, msg.Idx);   // BuiltInSignal.CANCEL = 1
        Assert.NotNull(msg.Void);
    }

    // ---- Lazy state-keys command factory ------------------------------------------------------

    [Fact]
    public void CreateGetStateKeysCommand_SetsCompletionId()
    {
        var msg = ProtobufCodec.CreateGetStateKeysCommand(42);
        Assert.Equal(42u, msg.ResultCompletionId);
    }

    // ---- Call-with-options factory (idempotency key path) -------------------------------------

    [Fact]
    public void CreateCallCommandWithOptions_SetsIdempotencyKey()
    {
        var msg = ProtobufCodec.CreateCallCommandWithOptions(
            "Svc", "H", "key", JsonSerializer.SerializeToUtf8Bytes("p"), 2, 1, "idem-123");
        Assert.Equal("Svc", msg.ServiceName);
        Assert.Equal("idem-123", msg.IdempotencyKey);
        Assert.Equal(2u, msg.ResultCompletionId);
        Assert.Equal(1u, msg.InvocationIdNotificationIdx);
    }

    // ---- ParseSignalNotification: signal-id and result oneof arms -----------------------------
    // The §4 suites only reach the SignalNotificationMessage through the SM's CANCEL emission
    // (an OUTGOING command), never through the INBOUND parser. These tests drive
    // ProtobufCodec.ParseSignalNotification directly so the Name signal-id arm (lines 341-342),
    // the Failure result arm (lines 353-354), and the SignalNotification record's Name/
    // FailureCode/FailureMessage getters + IsSuccess/IsFailure projections (lines 35,38,39,41-42)
    // are all exercised — these are the genuine inbound-signal decode paths a real server hits
    // when delivering a named/failed awakeable signal.

    [Fact]
    public void ParseSignalNotification_IdxWithValue_DecodesIndexAndSuccess()
    {
        // Indexed signal carrying a value: SignalNotification.Value is set, so IsSuccess is true.
        var content = JsonSerializer.SerializeToUtf8Bytes("payload");
        var msg = new Gen.SignalNotificationMessage
        {
            Idx = 17,
            Value = new Gen.Value { Content = ByteString.CopyFrom(content) }
        };

        var signal = ProtobufCodec.ParseSignalNotification(msg.ToByteArray());

        Assert.Equal(17u, signal.Idx);
        Assert.Null(signal.Name);
        Assert.True(signal.Value.HasValue);
        Assert.Equal(content, signal.Value!.Value.ToArray());
        Assert.True(signal.IsSuccess);
        Assert.False(signal.IsFailure);
    }

    [Fact]
    public void ParseSignalNotification_NamedSignal_DecodesNameArm()
    {
        // Named signal-id oneof: exercises the `else if (... Name)` arm (codec lines 341-342) and
        // the SignalNotification.Name getter (line 35). Void result → IsSuccess via the IsVoid path.
        var msg = new Gen.SignalNotificationMessage
        {
            Name = "my-signal",
            Void = new Gen.Void()
        };

        var signal = ProtobufCodec.ParseSignalNotification(msg.ToByteArray());

        Assert.Null(signal.Idx);
        Assert.Equal("my-signal", signal.Name);
        Assert.False(signal.Value.HasValue);
        Assert.True(signal.IsVoid);
        Assert.True(signal.IsSuccess);   // IsVoid alone makes a void signal a success
        Assert.False(signal.IsFailure);
    }

    [Fact]
    public void ParseSignalNotification_Failure_DecodesFailureArm()
    {
        // Failure result oneof: exercises codec lines 353-354 (Code/Message copy) and the
        // SignalNotification.FailureCode/FailureMessage getters + IsFailure projection (38,39,41-42).
        var msg = new Gen.SignalNotificationMessage
        {
            Idx = 18,
            Failure = new Gen.Failure { Code = 409, Message = "signal rejected" }
        };

        var signal = ProtobufCodec.ParseSignalNotification(msg.ToByteArray());

        Assert.Equal(18u, signal.Idx);
        Assert.Equal((ushort)409, signal.FailureCode);
        Assert.Equal("signal rejected", signal.FailureMessage);
        Assert.True(signal.IsFailure);
        Assert.False(signal.IsSuccess);   // no Value and not Void → not a success
    }
}
