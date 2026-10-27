using System.Security.Cryptography;

using WhalePod.Contracts.Devices;

namespace WhalePod.Tests.Unit;

public sealed class DeviceProtocolTests
{
    private const string Rfc8032Seed =
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string Rfc8032PublicKey =
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const string Rfc8032Signature =
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
        "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";

    [Fact]
    public void Rfc8032_vector_verifies()
    {
        var publicKey = Convert.FromHexString(Rfc8032PublicKey);
        var signature = Convert.FromHexString(Rfc8032Signature);

        Assert.True(DeviceCrypto.VerifyRaw(publicKey, string.Empty, signature));
        Assert.False(DeviceCrypto.VerifyRaw(publicKey, "tampered", signature));
    }

    [Fact]
    public void Rfc8032_seed_derives_public_key_and_signs()
    {
        var seed = Convert.FromHexString(Rfc8032Seed);
        var publicKey = DeviceCrypto.DerivePublicKey(seed);
        Assert.Equal(Rfc8032PublicKey, Convert.ToHexString(publicKey).ToLowerInvariant());

        var signature = DeviceCrypto.Sign(seed, "message");
        Assert.True(DeviceCrypto.VerifyRaw(publicKey, "message", Convert.FromBase64String(signature)));
        Assert.False(DeviceCrypto.VerifyRaw(publicKey, "tampered", Convert.FromBase64String(signature)));
    }

    [Fact]
    public void Payload_format_is_stable()
    {
        var message = DeviceProtocol.BuildMessage(
            "abc",
            "alice",
            1760000000,
            "6.8.0-45-generic",
            "linux-x64");

        Assert.Equal(
            """
            community-device-v1
            challenge:abc
            user:alice
            timestamp:1760000000
            kernel:6.8.0-45-generic
            platform:linux-x64
            """,
            message);
    }

    [Fact]
    public void Key_material_round_trips_through_spki()
    {
        var material = DeviceCrypto.GenerateKeyMaterial();
        Assert.Equal(DeviceProtocol.SpkiBytes, material.PublicKeySpki.Length);
        Assert.Equal(64, material.Fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", material.Fingerprint);
        Assert.True(DeviceProtocol.TryParseSpki(material.PublicKeySpki, out var raw));
        Assert.Equal(32, raw.Length);

        var message = DeviceProtocol.BuildMessage(
            DeviceProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            "alice",
            1760000000,
            "10.0.26300",
            "win32-x64");
        var signature = DeviceCrypto.Sign(material.PrivateKeySeed, message);
        Assert.True(DeviceCrypto.Verify(material.PublicKeyBase64, message, signature));
        Assert.False(DeviceCrypto.Verify(material.PublicKeyBase64, message + "x", signature));
        Assert.Equal(
            material.PublicKeyBase64,
            DeviceProtocol.TryDecodeBase64(material.PublicKeyBase64, out var spki)
                ? Convert.ToBase64String(spki)
                : string.Empty);
    }

    [Fact]
    public void Tampered_fields_do_not_verify()
    {
        var material = DeviceCrypto.GenerateKeyMaterial();
        const string challenge = "Y2hhbGxlbmdlLXNhbXBsZQ";
        var message = DeviceProtocol.BuildMessage(
            challenge,
            "alice",
            1760000000,
            "6.8.0-45-generic",
            "linux-x64");
        var signature = DeviceCrypto.Sign(material.PrivateKeySeed, message);

        var tamperedMessages = new[]
        {
            DeviceProtocol.BuildMessage("other", "alice", 1760000000, "6.8.0-45-generic", "linux-x64"),
            DeviceProtocol.BuildMessage(challenge, "bob", 1760000000, "6.8.0-45-generic", "linux-x64"),
            DeviceProtocol.BuildMessage(challenge, "alice", 1760000001, "6.8.0-45-generic", "linux-x64"),
            DeviceProtocol.BuildMessage(challenge, "alice", 1760000000, "6.8.0-46-generic", "linux-x64"),
            DeviceProtocol.BuildMessage(challenge, "alice", 1760000000, "6.8.0-45-generic", "win32-x64")
        };

        foreach (var tampered in tamperedMessages)
        {
            Assert.False(DeviceCrypto.Verify(material.PublicKeyBase64, tampered, signature));
        }
    }

    [Fact]
    public void Challenge_base64url_round_trips_and_rejects_padding()
    {
        var value = RandomNumberGenerator.GetBytes(32);
        var encoded = DeviceProtocol.Base64UrlEncode(value);
        Assert.Equal(43, encoded.Length);
        Assert.True(DeviceProtocol.IsValidChallenge(encoded));
        Assert.True(DeviceProtocol.TryDecodeBase64Url(encoded, out var decoded));
        Assert.Equal(value, decoded);
        Assert.False(DeviceProtocol.IsValidChallenge(encoded + "="));
        Assert.False(DeviceProtocol.TryDecodeBase64Url(encoded + "=", out _));
    }

    [Fact]
    public void Validation_matches_protocol_rules()
    {
        Assert.True(DeviceProtocol.IsValidHandle("alice-01"));
        Assert.False(DeviceProtocol.IsValidHandle("Alice"));
        Assert.False(DeviceProtocol.IsValidHandle("ab"));
        Assert.True(DeviceProtocol.IsValidKernelVersion("6.8.0-45-generic"));
        Assert.False(DeviceProtocol.IsValidKernelVersion("6.8"));
        Assert.True(DeviceProtocol.IsSupportedPlatform("linux-arm64"));
        Assert.False(DeviceProtocol.IsSupportedPlatform("freebsd-x64"));
    }
}