using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using RemoteFlow.Application.Abstractions;
using Renci.SshNet;

namespace RemoteFlow.Infrastructure.Ssh.Auth;

public sealed class SshKeyService(
    ISecretRegistry? secretRegistry = null,
    ISystemPlatform? platform = null) : ISshKeyService
{
    public const string PuttyConversionInstruction = "Convert it with: puttygen key.ppk -O private-openssh -o key";

    private static readonly string[] _wellKnownNonKeyFiles =
        ["config", "known_hosts", "known_hosts.old", "authorized_keys", "environment", "rc", "agent.env"];

    private readonly ISecretRegistry? _secretRegistry = secretRegistry;
    private readonly ISystemPlatform? _platform = platform;

    public string DefaultKeyDirectory => Path.Combine(
        _platform?.HomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh");

    public async Task<SshKeyInspection> InspectAsync(
        string path,
        ReadOnlyMemory<char> passphrase = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var format = DetectFormat(text, Path.GetExtension(fullPath));
        if (format == SshPrivateKeyFormat.PuttyPpk)
        {
            throw new SshKeyFormatException($"PuTTY .ppk keys are not supported. {PuttyConversionInstruction}");
        }

        var isEncrypted = IsEncrypted(text, format);
        if (isEncrypted && passphrase.IsEmpty)
        {
            return new(fullPath, format, true, null, null, null, null);
        }

        string? passphraseText = null;
        try
        {
            if (!passphrase.IsEmpty)
            {
                passphraseText = new string(passphrase.Span);
                if (passphraseText.Length >= 4)
                {
                    _secretRegistry?.Register(passphraseText);
                }
            }

            var publicKeyPath = fullPath + ".pub";
            if (File.Exists(publicKeyPath))
            {
                var publicText = (await File.ReadAllTextAsync(publicKeyPath, cancellationToken).ConfigureAwait(false)).Trim();
                return FromPublicKey(fullPath, format, isEncrypted, publicText);
            }

            using var privateKey = new PrivateKeyFile(fullPath, passphraseText ?? string.Empty);
            var algorithm = privateKey.HostKeyAlgorithms.First();
            var publicTextFromPrivate = $"{algorithm.Name} {Convert.ToBase64String(algorithm.Data)}{FormatComment(privateKey.Key.Comment)}";
            return FromPublicKey(fullPath, format, isEncrypted, publicTextFromPrivate);
        }
        catch (SshKeyFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SshKeyFormatException(
                isEncrypted
                    ? "The encrypted private key could not be opened. Check its passphrase and format."
                    : "The private key could not be opened. Check that it is a supported OpenSSH, PKCS#8, or PEM key.");
        }
    }

    public async Task<SshKeyInspection> GenerateEd25519Async(
        string path,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The key path must include a directory.", nameof(path));
        _ = Directory.CreateDirectory(directory);
        if (File.Exists(fullPath) || File.Exists(fullPath + ".pub"))
        {
            throw new IOException("The key path or its .pub file already exists.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", string.Empty, "-C", comment, "-f", fullPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ssh-keygen could not be started.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"ssh-keygen failed: {error.Trim()}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fullPath + ".pub", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        RestrictToOwner(fullPath);
        return await InspectAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a private key readable only by the user who owns it.
    /// </summary>
    /// <remarks>
    /// Both OpenSSH and SSH.NET refuse keys that other principals can read. On Unix that is mode
    /// 0600; on Windows it is the ACL equivalent of
    /// <c>icacls key /inheritance:r /grant:r "%USERNAME%:F"</c> — inheritance off and a single
    /// full-control entry for the current user.
    /// </remarks>
    public static void RestrictToOwner(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        RestrictToOwnerWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToOwnerWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } user)
        {
            return;
        }

        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        // Dropping the inherited entries rather than preserving them is the point: preserving would
        // copy SYSTEM and Administrators down as explicit entries and leave the key group-readable.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => !rule.IdentityReference.Equals(user))
            .ToArray())
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        security.SetAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    public async Task<IReadOnlyList<SshKeyInspection>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var directory = DefaultKeyDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var results = new List<SshKeyInspection>();
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (_wellKnownNonKeyFiles.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                // Checking the header first keeps unrelated files in ~/.ssh out of the list without
                // paying for a full parse of each one.
                var header = await ReadHeaderAsync(file, cancellationToken).ConfigureAwait(false);
                if (DetectFormat(header, Path.GetExtension(file)) == SshPrivateKeyFormat.Unknown)
                {
                    continue;
                }

                results.Add(await InspectAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One unreadable or malformed file must not hide the rest of the user's keys.
            }
        }

        return results;
    }

    public async Task<SshKeyInspection> ImportAsync(
        string path,
        string privateKeyText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyText);
        var text = privateKeyText.Trim().ReplaceLineEndings("\n") + "\n";
        var format = DetectFormat(text);
        if (format == SshPrivateKeyFormat.PuttyPpk)
        {
            throw new SshKeyFormatException($"PuTTY .ppk keys are not supported. {PuttyConversionInstruction}");
        }
        if (format == SshPrivateKeyFormat.Unknown)
        {
            throw new SshKeyFormatException(LooksLikePublicKey(text)
                ? "That is a public key. Paste the matching private key instead — the file without the .pub suffix, whose text starts with '-----BEGIN'."
                : "The pasted text is not an OpenSSH, PKCS#8, or PEM private key.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The key path must include a directory.", nameof(path));
        _ = Directory.CreateDirectory(directory);
        if (File.Exists(fullPath))
        {
            throw new IOException($"'{fullPath}' already exists. Choose a different key name.");
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows has no create-time ACL equivalent, so the file is created empty, locked down,
            // and only then written to.
            await File.WriteAllBytesAsync(fullPath, [], cancellationToken).ConfigureAwait(false);
            RestrictToOwner(fullPath);
            await File.WriteAllTextAsync(fullPath, text, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The file has to be owner-only before any key material reaches the disk, so the mode is
            // part of the create call rather than a follow-up chmod.
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            await using var stream = new FileStream(fullPath, options);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return await InspectAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool LooksLikePublicKey(string text)
    {
        return text.StartsWith("ssh-", StringComparison.Ordinal) ||
            text.StartsWith("ecdsa-", StringComparison.Ordinal) ||
            text.StartsWith("sk-ssh-", StringComparison.Ordinal) ||
            text.StartsWith("sk-ecdsa-", StringComparison.Ordinal);
    }

    private static async Task<string> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        using var reader = new StreamReader(path);
        var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }

#pragma warning disable IDE0045, IDE0046 // Sequential signature checks are clearer than nested conditional expressions.
    public static SshPrivateKeyFormat DetectFormat(string contents, string? extension = null)
    {
        ArgumentNullException.ThrowIfNull(contents);
        SshPrivateKeyFormat format;
        if (contents.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal) ||
            string.Equals(extension, ".ppk", StringComparison.OrdinalIgnoreCase))
        {
            format = SshPrivateKeyFormat.PuttyPpk;
        }
        else if (contents.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal))
        {
            format = SshPrivateKeyFormat.OpenSsh;
        }
        else if (contents.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
                 contents.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
        {
            format = SshPrivateKeyFormat.Pkcs8;
        }
        else
        {
            format = contents.Contains("PRIVATE KEY-----", StringComparison.Ordinal)
                ? SshPrivateKeyFormat.Pem
                : SshPrivateKeyFormat.Unknown;
        }
        return format;
    }
#pragma warning restore IDE0045, IDE0046

    private static SshKeyInspection FromPublicKey(
        string path,
        SshPrivateKeyFormat format,
        bool isEncrypted,
        string publicKeyText)
    {
        var fields = publicKeyText.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            throw new SshKeyFormatException("The public key is not in OpenSSH format.");
        }
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(fields[1]);
        }
        catch (FormatException exception)
        {
            throw new SshKeyFormatException($"The public key is not valid Base64: {exception.Message}");
        }
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(blob, digest);
        var fingerprint = $"SHA256:{Convert.ToBase64String(digest).TrimEnd('=')}";
        return new(
            path,
            format,
            isEncrypted,
            fields[0],
            fingerprint,
            fields.Length == 3 ? fields[2] : null,
            publicKeyText);
    }

    private static bool IsEncrypted(string contents, SshPrivateKeyFormat format)
    {
        return format switch
        {
            SshPrivateKeyFormat.OpenSsh => IsEncryptedOpenSsh(contents),
            SshPrivateKeyFormat.Pkcs8 => contents.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal),
            SshPrivateKeyFormat.Pem =>
                contents.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) ||
                contents.Contains("DEK-Info:", StringComparison.OrdinalIgnoreCase),
            SshPrivateKeyFormat.Unknown or SshPrivateKeyFormat.PuttyPpk => false,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static bool IsEncryptedOpenSsh(string contents)
    {
        try
        {
            var base64 = string.Concat(contents.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)));
            var data = Convert.FromBase64String(base64);
            var magic = "openssh-key-v1\0"u8;
            if (!data.AsSpan().StartsWith(magic))
            {
                return false;
            }
            var offset = magic.Length;
            var cipherName = ReadSshString(data, ref offset);
            return !cipherName.SequenceEqual("none"u8);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            return false;
        }
    }

    private static ReadOnlySpan<byte> ReadSshString(ReadOnlySpan<byte> data, ref int offset)
    {
        if (data.Length - offset < sizeof(uint))
        {
            throw new InvalidDataException("Incomplete SSH string.");
        }
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
        offset += sizeof(uint);
        if (length < 0 || data.Length - offset < length)
        {
            throw new InvalidDataException("Invalid SSH string length.");
        }
        var value = data.Slice(offset, length);
        offset += length;
        return value;
    }

    private static string FormatComment(string? comment)
    {
        return string.IsNullOrWhiteSpace(comment) ? string.Empty : $" {comment.Trim()}";
    }
}
