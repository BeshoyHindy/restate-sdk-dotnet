using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using Restate.Sdk.Benchmarks.Helpers;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Benchmarks;

/// <summary>
///     Layer 2 benchmarks: full invocation lifecycle through InvocationStateMachine.
///     These are the cross-SDK comparable scenarios defined in CROSS_SDK_BENCHMARK_SPEC.md.
///     Each benchmark measures complete SDK overhead including protocol parsing,
///     journal management, serialization, and command writing.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class InvocationLifecycleBenchmarks
{
    [Benchmark(Description = "Noop")]
    public async Task<bool> Noop()
    {
        using var protocol = new MockProtocol();

        WriteStartMessage(protocol, knownEntries: 1);
        WriteInputCommand(protocol, "\"hello\""u8);
        await protocol.FlushInbound();
        protocol.CompleteInbound();

        using var sm = new InvocationStateMachine(protocol.Reader, protocol.Writer);
        var start = await sm.StartAsync(CancellationToken.None);

        await sm.CompleteAsync(start.Input, CancellationToken.None);

        return true;
    }

    [Benchmark(Description = "SingleRun")]
    public async Task<string> SingleRun()
    {
        using var protocol = new MockProtocol();

        WriteStartMessage(protocol, knownEntries: 1);
        WriteInputCommand(protocol, ReadOnlySpan<byte>.Empty);
        await protocol.FlushInbound();
        protocol.CompleteInbound();

        using var sm = new InvocationStateMachine(protocol.Reader, protocol.Writer);
        await sm.StartAsync(CancellationToken.None);

        return await sm.RunSync("step1", () => "result", CancellationToken.None);
    }

    [Benchmark(Description = "ThreeRuns")]
    public async Task<int> ThreeRuns()
    {
        using var protocol = new MockProtocol();

        WriteStartMessage(protocol, knownEntries: 1);
        WriteInputCommand(protocol, ReadOnlySpan<byte>.Empty);
        await protocol.FlushInbound();
        protocol.CompleteInbound();

        using var sm = new InvocationStateMachine(protocol.Reader, protocol.Writer);
        await sm.StartAsync(CancellationToken.None);

        await sm.RunSync("step1", () => "hello", CancellationToken.None);
        await sm.RunSync("step2", () => 42, CancellationToken.None);
        return await sm.RunSync("step3", () => 99, CancellationToken.None);
    }

    [Benchmark(Description = "StateGetSet")]
    public async Task<int> StateGetSet()
    {
        using var protocol = new MockProtocol();

        WriteStartMessageWithState(protocol, knownEntries: 1,
            ("count", JsonSerializer.SerializeToUtf8Bytes(42)));
        WriteInputCommand(protocol, ReadOnlySpan<byte>.Empty);
        await protocol.FlushInbound();
        protocol.CompleteInbound();

        using var sm = new InvocationStateMachine(protocol.Reader, protocol.Writer);
        await sm.StartAsync(CancellationToken.None);

        var current = await sm.GetStateAsync<int>("count", CancellationToken.None);
        sm.SetState("count", current + 1);
        return current;
    }

    [Benchmark(Description = "ReplayRun_5Entries")]
    public async Task<int> ReplayRun_5Entries()
    {
        using var protocol = new MockProtocol();

        // 6 known entries = InputCommand + 5 RunCommands.
        // StartAsync reads ALL known entries — this is where journal replay happens.
        WriteStartMessage(protocol, knownEntries: 6);
        WriteInputCommand(protocol, ReadOnlySpan<byte>.Empty);

        for (var i = 0; i < 5; i++)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes($"result-{i}");
            WriteRunCommandReplayEntry(protocol, payload);
        }

        await protocol.FlushInbound();
        protocol.CompleteInbound();

        using var sm = new InvocationStateMachine(protocol.Reader, protocol.Writer);
        var start = await sm.StartAsync(CancellationToken.None);

        // After StartAsync, all 6 entries are in the journal and state is Processing.
        return start.KnownEntries;
    }

    // ---- Protocol message construction helpers using generated protobuf ----

    private static void WriteStartMessage(MockProtocol protocol, uint knownEntries)
    {
        var msg = new Gen.StartMessage
        {
            DebugId = "bench-inv-001",
            KnownEntries = knownEntries,
            RandomSeed = 12345UL
        };
        protocol.WriteInboundMessage(MessageType.Start, msg.ToByteArray());
    }

    private static void WriteStartMessageWithState(MockProtocol protocol, uint knownEntries,
        params (string Key, byte[] Value)[] stateEntries)
    {
        var msg = new Gen.StartMessage
        {
            DebugId = "bench-inv-001",
            KnownEntries = knownEntries,
            RandomSeed = 12345UL
        };

        foreach (var (key, value) in stateEntries)
        {
            msg.StateMap.Add(new Gen.StartMessage.Types.StateEntry
            {
                Key = ByteString.CopyFromUtf8(key),
                Value = ByteString.CopyFrom(value)
            });
        }

        protocol.WriteInboundMessage(MessageType.Start, msg.ToByteArray());
    }

    private static void WriteInputCommand(MockProtocol protocol, ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            protocol.WriteInboundMessage(MessageType.InputCommand, ReadOnlySpan<byte>.Empty);
            return;
        }

        var msg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(input) }
        };
        protocol.WriteInboundMessage(MessageType.InputCommand, msg.ToByteArray());
    }

    private static void WriteRunCommandReplayEntry(MockProtocol protocol, byte[] resultPayload)
    {
        protocol.WriteInboundMessage(MessageType.RunCommand, resultPayload);
    }
}
