using System.Numerics;
using System.Security.Cryptography;
using Restate.Sdk.Internal.Identity.Ed25519;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Proves the vendored verify-only Ed25519 math is correct: RFC 8032 §7.1 test
///     vectors plus a cross-check against BouncyCastle's independent implementation.
/// </summary>
public class Ed25519Tests
{
    // RFC 8032 §7.1 vectors: public key, message, signature (hex).
    public static TheoryData<string, string, string> Rfc8032Vectors => new()
    {
        // TEST 1 (empty message)
        {
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        // TEST 2 (one byte)
        {
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        // TEST 3 (two bytes)
        {
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac" +
            "18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        // TEST SHA(abc) (64-byte message)
        {
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a" +
            "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b589" +
            "09351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verify_Rfc8032Vectors_Succeeds(string publicKeyHex, string messageHex, string signatureHex)
    {
        byte[] publicKey = Convert.FromHexString(publicKeyHex);
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromHexString(signatureHex);

        Assert.True(Ed25519.Verify(signature, message, publicKey));
    }

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verify_TamperedSignature_Fails(string publicKeyHex, string messageHex, string signatureHex)
    {
        byte[] publicKey = Convert.FromHexString(publicKeyHex);
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromHexString(signatureHex);
        signature[0] ^= 0x01;

        Assert.False(Ed25519.Verify(signature, message, publicKey));
    }

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verify_TamperedMessage_Fails(string publicKeyHex, string messageHex, string signatureHex)
    {
        byte[] publicKey = Convert.FromHexString(publicKeyHex);
        byte[] message = Convert.FromHexString(messageHex);
        byte[] signature = Convert.FromHexString(signatureHex);
        byte[] tampered = new byte[message.Length + 1];
        message.CopyTo(tampered, 0);
        tampered[^1] = 0x42;

        Assert.False(Ed25519.Verify(signature, tampered, publicKey));
    }

    [Fact]
    public void Verify_WrongPublicKey_Fails()
    {
        // TEST 1 signature against TEST 2's public key.
        byte[] publicKey = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        byte[] signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        Assert.False(Ed25519.Verify(signature, [], publicKey));
    }

    [Fact]
    public void Verify_NonCanonicalScalar_Fails()
    {
        // s' = s + L verifies under the naive equation but violates RFC 8032's s < L
        // requirement (signature malleability). It must be rejected.
        byte[] publicKey = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        BigInteger order = BigInteger.Pow(2, 252) +
            BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
        var s = new BigInteger(signature.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
        byte[] malleable = (byte[])signature.Clone();
        Assert.True((s + order).TryWriteBytes(malleable.AsSpan(32, 32), out _, isUnsigned: true, isBigEndian: false));

        Assert.False(Ed25519.Verify(malleable, [], publicKey));
    }

    [Theory]
    [InlineData(63, 32)]
    [InlineData(65, 32)]
    [InlineData(0, 32)]
    [InlineData(64, 31)]
    [InlineData(64, 33)]
    [InlineData(64, 0)]
    public void Verify_WrongLengths_Fail(int signatureLength, int publicKeyLength)
    {
        Assert.False(Ed25519.Verify(new byte[signatureLength], [1, 2, 3], new byte[publicKeyLength]));
    }

    [Fact]
    public void Verify_InvalidPointEncoding_Fails()
    {
        // All-0xFF is not a valid curve point encoding.
        byte[] publicKey = new byte[32];
        Array.Fill(publicKey, (byte)0xff);

        Assert.False(Ed25519.Verify(new byte[64], [1, 2, 3], publicKey));
    }

    [Fact]
    public void Verify_CrossCheckAgainstBouncyCastle()
    {
        // Independent implementation cross-check: BouncyCastle signs, vendored code verifies.
        for (int i = 0; i < 25; i++)
        {
            (_, var privateKey) = IdentityTestHelpers.CreateKeyPair();
            byte[] publicKey = privateKey.GeneratePublicKey().GetEncoded();
            byte[] message = RandomNumberGenerator.GetBytes(i * 13);
            byte[] signature = IdentityTestHelpers.Sign(privateKey, message);

            Assert.True(Ed25519.Verify(signature, message, publicKey));

            signature[i % 64] ^= 0x80;
            Assert.False(Ed25519.Verify(signature, message, publicKey));
        }
    }
}
