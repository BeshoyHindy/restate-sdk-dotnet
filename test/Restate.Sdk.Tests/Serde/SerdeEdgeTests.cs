using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restate.Sdk.Internal.Serde;

namespace Restate.Sdk.Tests.Serde;

/// <summary>
///     Plan 07 §1.2 protocol-serde lane. The existing JsonSerdeTests only build
///     <see cref="JsonSerde{T}" /> through <see cref="JsonSerde{T}.Default" />, which uses the
///     reflection (<see cref="JsonSerializerOptions" />) constructor. The AOT-facing
///     <c>JsonSerde(JsonTypeInfo&lt;T&gt;)</c> constructor (line 63), its <c>_typeInfo</c> field
///     assignment, and the <c>_typeInfo is not null</c> branches in Serialize (line 74) and
///     Deserialize (line 85) are never reached. These tests drive a serde built from a genuine
///     source-generated <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}" /> —
///     the exact path an AOT consumer takes — so both typed branches round-trip.
/// </summary>
public class SerdeEdgeTests
{
    private static ReadOnlyMemory<byte> SerializeToMemory<T>(ISerde<T> serde, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serde.Serialize(buffer, value);
        return buffer.WrittenMemory;
    }

    [Fact]
    public void TypeInfoConstructor_RoundTripsThroughTheTypedBranch_Object()
    {
        // The JsonTypeInfo<T> ctor must select the typed Serialize/Deserialize branches (not the
        // reflection fallback). A round-trip through a source-generated context proves both.
        var serde = new JsonSerde<SerdePayload>(SerdeEdgeJsonContext.Default.SerdePayload);
        var original = new SerdePayload { Name = "typed", Value = 7 };

        var bytes = SerializeToMemory(serde, original);
        var result = serde.Deserialize(new ReadOnlySequence<byte>(bytes));

        Assert.Equal("typed", result.Name);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public void TypeInfoConstructor_RoundTripsThroughTheTypedBranch_Primitive()
    {
        // A primitive type info exercises the same typed branches with the simplest payload shape.
        var serde = new JsonSerde<int>(SerdeEdgeJsonContext.Default.Int32);

        var bytes = SerializeToMemory(serde, 99);
        var result = serde.Deserialize(new ReadOnlySequence<byte>(bytes));

        Assert.Equal(99, result);
    }

    [Fact]
    public void TypeInfoConstructor_ContentType_IsJson()
    {
        // ContentType is constant regardless of which constructor produced the instance.
        var serde = new JsonSerde<int>(SerdeEdgeJsonContext.Default.Int32);
        Assert.Equal("application/json", serde.ContentType);
    }

    [Fact]
    public void TypeInfoConstructor_DeserializeEmpty_ReturnsDefault()
    {
        // The IsEmpty fast-path returns default BEFORE the _typeInfo branch — so the typed serde
        // must agree with the reflection serde on empty input (both yield default(T)).
        var serde = new JsonSerde<int>(SerdeEdgeJsonContext.Default.Int32);
        Assert.Equal(0, serde.Deserialize(ReadOnlySequence<byte>.Empty));
    }

    public class SerdePayload
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }
}

// Source-generated metadata gives real JsonTypeInfo<T> instances for the AOT constructor path,
// matching how generated handler code wires payload type infos into JsonSerde<T>.
[JsonSerializable(typeof(SerdeEdgeTests.SerdePayload))]
[JsonSerializable(typeof(int))]
internal partial class SerdeEdgeJsonContext : JsonSerializerContext;
