using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using WhalePod.Contracts.Devices;
using WhalePod.DeviceCli;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
}
catch (Exception)
{
}

const string usage = """
    whalepod-device - WhalePod 设备密钥签名工具

    用法:
      whalepod-device sign --challenge <base64url> --handle <handle> [--key-file <path>]
      whalepod-device version
      whalepod-device help

    说明:
      - sign 在本机生成/读取 Ed25519 密钥并输出可粘贴到站点的 JSON。
      - 私钥只保存在本机，不会上传、打印或写入日志。
      - 本工具不联网、不自动更新，也不读取站点令牌。

    退出码: 0 成功；2 参数错误；3 平台不支持；4 密钥文件不可用；5 签名失败；1 其他错误。
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(usage);
    return 2;
}

return args[0].ToLowerInvariant() switch
{
    "help" or "--help" or "-h" => PrintUsage(),
    "version" or "--version" => PrintVersion(),
    "sign" => Sign(args),
    _ => UnknownCommand(args[0])
};

static int PrintUsage()
{
    Console.WriteLine(usage);
    return 0;
}

static int PrintVersion()
{
    Console.WriteLine(
        $"whalepod-device {DeviceProtocol.CliVersion} ({DeviceProtocol.Name}, protocol version {DeviceProtocol.Version})");
    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"未知命令：{command}");
    Console.Error.WriteLine(usage);
    return 2;
}

static int Sign(string[] args)
{
    if (!HasOnlyKnownOptions(args))
    {
        Console.Error.WriteLine("存在未知参数。");
        Console.Error.WriteLine(usage);
        return 2;
    }

    if (!TryGetOption(args, "--challenge", out var challenge)
        || !TryGetOption(args, "--handle", out var handle))
    {
        Console.Error.WriteLine("缺少 --challenge 或 --handle 参数。");
        Console.Error.WriteLine(usage);
        return 2;
    }

    if (!DeviceProtocol.IsValidChallenge(challenge))
    {
        Console.Error.WriteLine("challenge 必须是无填充的 base64url 字符串（16-128 字符）。");
        return 2;
    }

    var normalizedHandle = (handle ?? string.Empty).Trim().ToLowerInvariant();
    if (!DeviceProtocol.IsValidHandle(normalizedHandle))
    {
        Console.Error.WriteLine("handle 必须是 3-32 位小写 ASCII 字母、数字或连字符。");
        return 2;
    }

    var platform = DetectPlatform();
    if (platform is null)
    {
        Console.Error.WriteLine("当前操作系统或架构不受支持。");
        return 3;
    }

    var kernelVersion = DetectKernelVersion();
    if (!DeviceProtocol.IsValidKernelVersion(kernelVersion))
    {
        Console.Error.WriteLine("无法获取有效的内核版本。");
        return 5;
    }

    var keyFile = ResolveKeyFile(
        TryGetOption(args, "--key-file", out var requestedKeyFile) ? requestedKeyFile : null);
    DeviceKeyMaterial keyMaterial;
    var existed = File.Exists(keyFile);
    try
    {
        keyMaterial = LoadKey(keyFile) ?? CreateKey(keyFile);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"密钥文件不可用：{exception.Message}");
        return 4;
    }

    var created = !existed;

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var message = DeviceProtocol.BuildMessage(
        challenge!,
        normalizedHandle,
        timestamp,
        kernelVersion,
        platform);
    string signature;
    try
    {
        signature = DeviceCrypto.Sign(keyMaterial.PrivateKeySeed, message);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"签名失败：{exception.Message}");
        return 5;
    }

    Console.Error.WriteLine(
        created
            ? $"已生成本地 Ed25519 密钥并保存到：{keyFile}。请备份该文件；私钥不会上传或打印。"
            : $"使用本地密钥：{keyFile}。请妥善备份；私钥不会上传或打印。");
    var output = new
    {
        version = DeviceProtocol.Version,
        challenge,
        handle = normalizedHandle,
        timestamp,
        kernelVersion,
        platform,
        publicKey = keyMaterial.PublicKeyBase64,
        signature
    };
    Console.WriteLine(JsonSerializer.Serialize(
        output,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

static DeviceKeyMaterial? LoadKey(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    var json = File.ReadAllText(path);
    var file = JsonSerializer.Deserialize<DeviceKeyFile>(
        json,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (file is null || file.Version != 1)
    {
        throw new InvalidDataException("密钥文件版本无效。");
    }

    var seed = Convert.FromBase64String(file.PrivateKey);
    if (seed.Length != 32)
    {
        throw new InvalidDataException("密钥文件中的私钥长度无效。");
    }

    var rawPublicKey = DeviceCrypto.DerivePublicKey(seed);
    var spki = DeviceProtocol.EncodeSpki(rawPublicKey);
    var publicKeyBase64 = Convert.ToBase64String(spki);
    if (!string.Equals(publicKeyBase64, file.PublicKey, StringComparison.Ordinal))
    {
        throw new InvalidDataException("密钥文件中的公钥与私钥不匹配。");
    }

    return new DeviceKeyMaterial(
        seed,
        spki,
        publicKeyBase64,
        DeviceProtocol.ComputeFingerprint(spki));
}

static DeviceKeyMaterial CreateKey(string path)
{
    var material = DeviceCrypto.GenerateKeyMaterial();
    var directory = Path.GetDirectoryName(path);
    if (string.IsNullOrWhiteSpace(directory))
    {
        throw new InvalidDataException("密钥文件路径无效。");
    }

    Directory.CreateDirectory(directory);
    RestrictDirectory(directory);
    var file = new DeviceKeyFile(
        1,
        Convert.ToBase64String(material.PrivateKeySeed),
        material.PublicKeyBase64,
        DateTimeOffset.UtcNow);
    var tempPath = path + ".tmp";
    File.WriteAllText(
        tempPath,
        JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
    RestrictFile(tempPath);
    File.Move(tempPath, path, overwrite: false);
    RestrictFile(path);
    return material;
}

static string ResolveKeyFile(string? requested)
{
    if (!string.IsNullOrWhiteSpace(requested))
    {
        return Path.GetFullPath(requested);
    }

    var environment = Environment.GetEnvironmentVariable("WHALEPOD_DEVICE_KEY_FILE");
    if (!string.IsNullOrWhiteSpace(environment))
    {
        return Path.GetFullPath(environment);
    }

    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (string.IsNullOrWhiteSpace(appData))
    {
        appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    return Path.Combine(appData, "WhalePod", "device-key.json");
}

static string? DetectPlatform()
{
    var architecture = RuntimeInformation.OSArchitecture;
    var suffix = architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => null
    };
    if (suffix is null)
    {
        return null;
    }

    if (OperatingSystem.IsWindows())
    {
        return $"win32-{suffix}";
    }

    if (OperatingSystem.IsMacOS())
    {
        return $"darwin-{suffix}";
    }

    return OperatingSystem.IsLinux() ? $"linux-{suffix}" : null;
}

static string DetectKernelVersion()
{
    if (OperatingSystem.IsWindows())
    {
        var version = Environment.OSVersion.Version;
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    var output = ProcessRunner.Run("uname", "-r", TimeSpan.FromSeconds(2));
    if (output is not null && DeviceProtocol.IsValidKernelVersion(output))
    {
        return output;
    }

    var fallback = Environment.OSVersion.Version;
    return string.Create(
        CultureInfo.InvariantCulture,
        $"{fallback.Major}.{fallback.Minor}.{Math.Max(0, fallback.Build)}");
}

static bool HasOnlyKnownOptions(string[] args)
{
    var known = new[] { "--challenge", "--handle", "--key-file" };
    for (var index = 1; index < args.Length; index++)
    {
        var argument = args[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = argument.Split('=', 2)[0];
        if (!known.Contains(name, StringComparer.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static bool TryGetOption(string[] args, string name, out string? value)
{
    value = null;
    for (var index = 1; index < args.Length; index++)
    {
        if (string.Equals(args[index], name, StringComparison.Ordinal))
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            value = args[index + 1];
            return true;
        }

        var prefix = name + "=";
        if (args[index].StartsWith(prefix, StringComparison.Ordinal))
        {
            value = args[index][prefix.Length..];
            return true;
        }
    }

    return false;
}

static void RestrictDirectory(string path)
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    catch (Exception)
    {
    }
}

static void RestrictFile(string path)
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    catch (Exception)
    {
    }
}

internal sealed record DeviceKeyFile(
    int Version,
    string PrivateKey,
    string PublicKey,
    DateTimeOffset CreatedAt);