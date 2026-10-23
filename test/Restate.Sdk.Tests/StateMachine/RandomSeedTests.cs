using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     <c>StartMessage.random_seed</c> only exists from protocol V6. These tests pin where the
///     seed behind <see cref="DurableRandom" /> comes from on each version: derived from the
///     invocation id below V6, taken from the StartMessage unchanged from V6 on.
/// </summary>
public class RandomSeedTests : IDisposable
{
    /// <summary>The first values of <c>Next()</c> for seed 42, pinning V6+ behaviour.</summary>
    private static readonly int[] SeedFortyTwoSequence = [1434747710, 302596119, 269548474, 1122627734, 361709742];

    /// <summary>The first values of <c>Next()</c> for seed 0, pinning the unrewritten V6 zero.</summary>
    private static readonly int[] SeedZeroSequence = [1559595546, 1755192844, 1649316166, 1198642031, 442452829];

    /// <summary>The first <c>NextGuid()</c> for seed 42, pinning V6+ behaviour.</summary>
    private const string SeedFortyTwoGuid = "3e17ba96-ae04-4d3b-9986-9e56f0adbf3a";

    private readonly List<Pipe> _pipes = [];

    public void Dispose()
    {
        foreach (var pipe in _pipes)
        {
            pipe.Writer.Complete();
            pipe.Reader.Complete();
        }
    }

    // ------- Below V6: derived from the invocation id -------

    [Fact]
    public void V5_DifferentInvocationIds_ProduceDifferentSequences()
    {
        using var first = CreateSm(ServiceProtocolVersion.V5);
        using var second = CreateSm(ServiceProtocolVersion.V5);

        first.Initialize("inv-a", Encoding.UTF8.GetBytes("inv-a"), "", 0, 0);
        second.Initialize("inv-b", Encoding.UTF8.GetBytes("inv-b"), "", 0, 0);

        Assert.NotEqual(first.RandomSeed, second.RandomSeed);
        Assert.NotEqual(NextValues(first.RandomSeed), NextValues(second.RandomSeed));
        Assert.NotEqual(new DurableRandom(first.RandomSeed).NextGuid(),
            new DurableRandom(second.RandomSeed).NextGuid());
    }

    [Fact]
    public void V5_SameInvocationId_ProducesTheSameSequenceOnEveryConstruction()
    {
        using var first = CreateSm(ServiceProtocolVersion.V5);
        using var second = CreateSm(ServiceProtocolVersion.V5);

        first.Initialize("inv-replay", Encoding.UTF8.GetBytes("inv-replay"), "", 0, 0);
        second.Initialize("inv-replay", Encoding.UTF8.GetBytes("inv-replay"), "", 0, 0);

        Assert.Equal(first.RandomSeed, second.RandomSeed);
        Assert.Equal(NextValues(first.RandomSeed), NextValues(second.RandomSeed));
        Assert.Equal(new DurableRandom(first.RandomSeed).NextGuid(),
            new DurableRandom(second.RandomSeed).NextGuid());
    }

    [Fact]
    public void V5_SeedIsSha256OfTheRawInvocationIdAsBigEndian()
    {
        var rawId = new byte[] { 0x01, 0x7a, 0xbc, 0xde, 0xf0, 0x11, 0x22, 0x33, 0x44, 0x55 };

        using var sm = CreateSm(ServiceProtocolVersion.V5);
        sm.Initialize("inv-raw", rawId, "", 0, 0);

        Assert.Equal(ExpectedSeed(rawId), sm.RandomSeed);
    }

    [Fact]
    public void V5_WithoutARawInvocationId_DerivesFromTheIdText()
    {
        using var sm = CreateSm(ServiceProtocolVersion.V5);
        sm.Initialize("inv-text-only", "", 0, 0);

        Assert.Equal(ExpectedSeed(Encoding.UTF8.GetBytes("inv-text-only")), sm.RandomSeed);
    }

    [Fact]
    public void V5_StartMessageSeed_IsIgnored()
    {
        using var sm = CreateSm(ServiceProtocolVersion.V5);
        sm.Initialize("inv-seeded", Encoding.UTF8.GetBytes("inv-seeded"), "", 42, 0);

        Assert.Equal(ExpectedSeed(Encoding.UTF8.GetBytes("inv-seeded")), sm.RandomSeed);
        Assert.NotEqual(42UL, sm.RandomSeed);
    }

    // ------- V6 and above: the StartMessage seed, untouched -------

    [Fact]
    public void V6_ExplicitSeed_IsUsedUnchanged()
    {
        using var sm = CreateSm(ServiceProtocolVersion.V6);
        sm.Initialize("inv-v6", Encoding.UTF8.GetBytes("inv-v6"), "", 42, 0);

        Assert.Equal(42UL, sm.RandomSeed);
        Assert.Equal(SeedFortyTwoSequence, NextValues(sm.RandomSeed));
        Assert.Equal(SeedFortyTwoGuid, new DurableRandom(sm.RandomSeed).NextGuid().ToString());
    }

    [Fact]
    public void V6_SeedOfZero_IsNotRewritten()
    {
        using var sm = CreateSm(ServiceProtocolVersion.V6);
        sm.Initialize("inv-v6-zero", Encoding.UTF8.GetBytes("inv-v6-zero"), "", 0, 0);

        Assert.Equal(0UL, sm.RandomSeed);
        Assert.Equal(SeedZeroSequence, NextValues(sm.RandomSeed));
    }

    [Fact]
    public void V7_ExplicitSeed_IsUsedUnchanged()
    {
        using var sm = CreateSm(ServiceProtocolVersion.V7);
        sm.Initialize("inv-v7", Encoding.UTF8.GetBytes("inv-v7"), "", 42, 0);

        Assert.Equal(42UL, sm.RandomSeed);
        Assert.Equal(SeedFortyTwoSequence, NextValues(sm.RandomSeed));
        Assert.Equal(SeedFortyTwoGuid, new DurableRandom(sm.RandomSeed).NextGuid().ToString());
    }

    // ------- Through the wire -------

    [Fact]
    public async Task StartAsync_OnAV5Stream_DerivesTheSeedFromTheInvocationId()
    {
        var rawId = new byte[] { 0x5a, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        var (sm, inbound) = CreateSmWithInbound(ServiceProtocolVersion.V5);
        using (sm)
        {
            await WriteStartAsync(inbound, rawId, "inv-v5-stream", 0);

            var info = await sm.StartAsync(CancellationToken.None);

            Assert.Equal(ExpectedSeed(rawId), sm.RandomSeed);
            Assert.Equal(sm.RandomSeed, info.RandomSeed);
        }
    }

    [Fact]
    public async Task StartAsync_OnAV6Stream_KeepsTheSeedFromTheStartMessage()
    {
        var rawId = new byte[] { 0x60, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        var (sm, inbound) = CreateSmWithInbound(ServiceProtocolVersion.V6);
        using (sm)
        {
            await WriteStartAsync(inbound, rawId, "inv-v6-stream", 99);

            var info = await sm.StartAsync(CancellationToken.None);

            Assert.Equal(99UL, sm.RandomSeed);
            Assert.Equal(99UL, info.RandomSeed);
        }
    }

    // ------- Helpers -------

    private static ulong ExpectedSeed(byte[] idBytes)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(idBytes));
    }

    private static int[] NextValues(ulong seed)
    {
        var random = new DurableRandom(seed);
        var values = new int[5];
        for (var i = 0; i < values.Length; i++)
            values[i] = random.Next();

        return values;
    }

    private InvocationStateMachine CreateSm(ServiceProtocolVersion version)
    {
        return CreateSmWithInbound(version).Sm;
    }

    private (InvocationStateMachine Sm, Pipe Inbound) CreateSmWithInbound(ServiceProtocolVersion version)
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        _pipes.Add(inbound);
        _pipes.Add(outbound);

        var sm = new InvocationStateMachine(
            new ProtocolReader(inbound.Reader),
            new ProtocolWriter(outbound.Writer),
            negotiatedVersion: version);

        return (sm, inbound);
    }

    private static async Task WriteStartAsync(Pipe inbound, byte[] rawId, string debugId, ulong randomSeed)
    {
        var start = new Gen.StartMessage
        {
            Id = ByteString.CopyFrom(rawId),
            DebugId = debugId,
            KnownEntries = 1,
            Key = "",
            RandomSeed = randomSeed
        };
        await WriteFramedAsync(inbound, MessageType.Start, start.ToByteArray());

        var input = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes("World")) }
        };
        await WriteFramedAsync(inbound, MessageType.InputCommand, input.ToByteArray());
    }

    private static async Task WriteFramedAsync(Pipe inbound, MessageType type, byte[] payload)
    {
        var header = new byte[MessageHeader.Size];
        MessageHeader.Create(type, MessageFlags.None, (uint)payload.Length).Write(header);
        await inbound.Writer.WriteAsync(header);
        await inbound.Writer.WriteAsync(payload);
    }
}
