using System.Security.Cryptography;
using System.Text;
using Restate.Sdk.Internal.Identity;

namespace Restate.Sdk.Tests.Identity;

public class Base58Tests
{
    [Fact]
    public void Decode_EmptyInput_ReturnsEmpty()
    {
        byte[]? decoded = Base58.Decode("");

        Assert.NotNull(decoded);
        Assert.Empty(decoded);
    }

    [Theory]
    [InlineData("2NEpo7TZRRrLZSi2U", "Hello World!")]
    [InlineData("USm3fpXnKG5EUBx2ndxBDMPVciP5hGey2Jh4NDv6gmeo1LkMeiKrLJUUBk6Z",
        "The quick brown fox jumps over the lazy dog.")]
    public void Decode_KnownAsciiVectors(string encoded, string expectedText)
    {
        byte[]? decoded = Base58.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(expectedText, Encoding.ASCII.GetString(decoded));
    }

    [Theory]
    [InlineData("111233QC4", "000000287fb4cd")]
    [InlineData("1", "00")]
    [InlineData("11", "0000")]
    [InlineData("2g", "61")]
    [InlineData("a3gV", "626262")]
    public void Decode_KnownBinaryVectors(string encoded, string expectedHex)
    {
        byte[]? decoded = Base58.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(Convert.FromHexString(expectedHex), decoded);
    }

    [Theory]
    [InlineData("0")] // zero is not in the alphabet
    [InlineData("O")] // capital o is not in the alphabet
    [InlineData("I")] // capital i is not in the alphabet
    [InlineData("l")] // lowercase L is not in the alphabet
    [InlineData("2NEpo7TZ+RrLZSi2U")]
    [InlineData("2NEpo7TZ RrLZSi2U")]
    public void Decode_InvalidCharacters_ReturnsNull(string encoded)
    {
        Assert.Null(Base58.Decode(encoded));
    }

    [Fact]
    public void Decode_RoundTripsRandomKeys()
    {
        for (int i = 0; i < 50; i++)
        {
            byte[] original = RandomNumberGenerator.GetBytes(32);
            string encoded = IdentityTestHelpers.Base58Encode(original);

            Assert.Equal(original, Base58.Decode(encoded));
        }
    }

    [Fact]
    public void Decode_RoundTripsLeadingZeros()
    {
        byte[] original = [0, 0, 0, 1, 2, 3];
        string encoded = IdentityTestHelpers.Base58Encode(original);

        Assert.StartsWith("111", encoded, StringComparison.Ordinal);
        Assert.Equal(original, Base58.Decode(encoded));
    }
}
