using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace WhalePod.Contracts.Devices;

public sealed record DeviceCompleteRequest(
    int Version,
    string? Challenge,
    string? Handle,
    long Timestamp,
    string? KernelVersion,
    string? Platform,
    string? PublicKey,
    string? Signature,
    string? Label);

public sealed record DeviceKeyMaterial(
    byte[] PrivateKeySeed,
    byte[] PublicKeySpki,
    string PublicKeyBase64,
    string Fingerprint);

public static partial class DeviceProtocol
{
    public const string Name = "community-device-v1";
    public const string CliVersion = "0.1.0";
    public const int Version = 1;
    public const int ChallengeBytes = 32;
    public const int SignatureBytes = 64;
    public const int SpkiBytes = 44;
    public const int MaxKernelLength = 64;
    public const int MaxLabelLength = 40;

    public static readonly string[] Platforms =
    [
        "win32-x64",
        "win32-arm64",
        "darwin-x64",
        "darwin-arm64",
        "linux-x64",
        "linux-arm64"
    ];

    public static bool IsSupportedPlatform(string? platform)
    {
        return platform is not null && Platforms.Contains(platform, StringComparer.Ordinal);
    }

    public static bool IsValidKernelVersion(string? kernelVersion)
    {
        return !string.IsNullOrWhiteSpace(kernelVersion)
            && kernelVersion.Length <= MaxKernelLength
            && KernelVersionPattern().IsMatch(kernelVersion);
    }

    public static bool IsValidHandle(string? handle)
    {
        return !string.IsNullOrWhiteSpace(handle)
            && handle.Length is >= 3 and <= 32
            && HandlePattern().IsMatch(handle);
    }

    public static bool IsValidChallenge(string? challenge)
    {
        return !string.IsNullOrWhiteSpace(challenge)
            && challenge.Length is >= 16 and <= 128
            && ChallengePattern().IsMatch(challenge);
    }

    public static string BuildMessage(
        string challenge,
        string handle,
        long timestamp,
        string kernelVersion,
        string platform)
    {
        return string.Join(
            '\n',
            Name,
            $"challenge:{challenge}",
            $"user:{handle}",
            $"timestamp:{timestamp}",
            $"kernel:{kernelVersion}",
            $"platform:{platform}");
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecodeBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)
            || value.Length is < 16 or > 128
            || !ChallengePattern().IsMatch(value))
        {
            return false;
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ComputeFingerprint(ReadOnlySpan<byte> spkiDer)
    {
        return Convert.ToHexString(SHA256.HashData(spkiDer)).ToLowerInvariant();
    }

    public static bool TryParseSpki(ReadOnlySpan<byte> spkiDer, out byte[] rawPublicKey)
    {
        rawPublicKey = [];
        if (spkiDer.Length != SpkiBytes)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(spkiDer.ToArray(), AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var algorithm = sequence.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.3.101.112")
            {
                return false;
            }

            algorithm.ThrowIfNotEmpty();
            var bitString = sequence.ReadBitString(out var unusedBitCount);
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            if (unusedBitCount != 0 || bitString.Length != 32)
            {
                return false;
            }

            rawPublicKey = bitString;
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    public static byte[] EncodeSpki(ReadOnlySpan<byte> rawPublicKey)
    {
        if (rawPublicKey.Length != 32)
        {
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(rawPublicKey));
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.101.112");
            }

            writer.WriteBitString(rawPublicKey);
        }

        return writer.Encode();
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex KernelVersionPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengePattern();
}

public static class DeviceCrypto
{
    public static DeviceKeyMaterial GenerateKeyMaterial()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKey = DerivePublicKey(seed);
        var spki = DeviceProtocol.EncodeSpki(publicKey);
        return new DeviceKeyMaterial(
            seed,
            spki,
            Convert.ToBase64String(spki),
            DeviceProtocol.ComputeFingerprint(spki));
    }

    public static byte[] DerivePublicKey(ReadOnlySpan<byte> privateSeed)
    {
        if (privateSeed.Length != 32)
        {
            throw new ArgumentException("Ed25519 private seed must be 32 bytes.", nameof(privateSeed));
        }

        var privateKey = new Ed25519PrivateKeyParameters(privateSeed.ToArray(), 0);
        return privateKey.GeneratePublicKey().GetEncoded();
    }

    public static string Sign(ReadOnlySpan<byte> privateSeed, string message)
    {
        if (privateSeed.Length != 32)
        {
            throw new ArgumentException("Ed25519 private seed must be 32 bytes.", nameof(privateSeed));
        }

        var privateKey = new Ed25519PrivateKeyParameters(privateSeed.ToArray(), 0);
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify(string? publicKeyBase64, string message, string? signatureBase64)
    {
        if (!DeviceProtocol.TryDecodeBase64(publicKeyBase64, out var spki)
            || !DeviceProtocol.TryParseSpki(spki, out var rawPublicKey)
            || !DeviceProtocol.TryDecodeBase64(signatureBase64, out var signature)
            || signature.Length != DeviceProtocol.SignatureBytes)
        {
            return false;
        }

        return VerifyRaw(rawPublicKey, message, signature);
    }

    public static bool VerifyRaw(ReadOnlySpan<byte> rawPublicKey, string message, ReadOnlySpan<byte> signature)
    {
        if (rawPublicKey.Length != 32 || signature.Length != DeviceProtocol.SignatureBytes)
        {
            return false;
        }

        try
        {
            var publicKey = new Ed25519PublicKeyParameters(rawPublicKey.ToArray(), 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            var bytes = Encoding.UTF8.GetBytes(message);
            verifier.BlockUpdate(bytes, 0, bytes.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    public static bool TryImportPublicKey(string? spkiBase64, out byte[] rawPublicKey)
    {
        rawPublicKey = [];
        return DeviceProtocol.TryDecodeBase64(spkiBase64, out var spki)
            && DeviceProtocol.TryParseSpki(spki, out rawPublicKey);
    }

    public static string ExportSpkiBase64(ReadOnlySpan<byte> rawPublicKey)
    {
        return Convert.ToBase64String(DeviceProtocol.EncodeSpki(rawPublicKey));
    }
}