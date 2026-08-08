using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

public sealed class KnownHostsImportService(
    IHostKeyStore store,
    IGuidProvider guidProvider,
    IClock clock) : IKnownHostsImportService
{
    private readonly IHostKeyStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IGuidProvider _guidProvider = guidProvider ?? throw new ArgumentNullException(nameof(guidProvider));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<KnownHostsImportPreview> PreviewAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var entries = new List<KnownHostImportEntry>();
        var warnings = new List<string>();

        // Read-only sharing also makes the no-write guarantee explicit at the OS boundary.
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            ParseLine(line, lineNumber, entries, warnings);
        }

        return new(fullPath, entries, warnings);
    }

    public async Task<KnownHostsImportResult> ApplyAsync(
        KnownHostsImportPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var added = 0;
        var skipped = 0;
        foreach (var entry in preview.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _store.GetAsync(entry.Host, entry.Port, entry.KeyAlgorithm, cancellationToken).ConfigureAwait(false) is not null)
            {
                skipped++;
                continue;
            }

            var created = HostKey.Create(
                _guidProvider,
                entry.Host,
                entry.Port,
                entry.KeyAlgorithm,
                entry.PublicKeyBase64,
                entry.Sha256Fingerprint,
                entry.IsRevoked ? HostKeyTrust.Revoked : HostKeyTrust.Trusted,
                HostKeySource.ImportedKnownHosts,
                entry.IsHashed ? "Imported hashed OpenSSH hostname; plaintext is intentionally unavailable." : entry.Comment,
                _clock.UtcNow);
            if (created.IsFailure)
            {
                throw new InvalidDataException($"known_hosts line {entry.LineNumber}: {created.Error.Message}");
            }

            await _store.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
            added++;
        }

        return new(added, skipped);
    }

    private static void ParseLine(
        string line,
        int lineNumber,
        List<KnownHostImportEntry> entries,
        List<string> warnings)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var offset = fields[0].StartsWith('@') ? 1 : 0;
        var isRevoked = offset == 1 && string.Equals(fields[0], "@revoked", StringComparison.Ordinal);
        if (fields.Length < offset + 3)
        {
            warnings.Add($"Line {lineNumber} was ignored because it is not a complete known_hosts entry.");
            return;
        }

        var hosts = fields[offset].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var algorithm = fields[offset + 1];
        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(fields[offset + 2]);
        }
        catch (FormatException)
        {
            warnings.Add($"Line {lineNumber} was ignored because its public key is not valid Base64.");
            return;
        }

        var fingerprint = HostKeyFingerprint.FormatSha256(publicKey);
        var comment = fields.Length > offset + 3
            ? string.Join(' ', fields.Skip(offset + 3))
            : null;
        foreach (var hostField in hosts)
        {
            var isHashed = hostField.StartsWith("|1|", StringComparison.Ordinal);
            var (host, port) = isHashed ? (hostField, 22) : ParseHostAndPort(hostField);
            entries.Add(new(
                lineNumber,
                host,
                port,
                algorithm,
                Convert.ToBase64String(publicKey),
                fingerprint,
                comment,
                isHashed,
                isRevoked));
        }
    }

    private static (string Host, int Port) ParseHostAndPort(string value)
    {
        if (value.StartsWith('[') && value.IndexOf("]:", StringComparison.Ordinal) is var separator && separator > 1 &&
            int.TryParse(value[(separator + 2)..], out var port) && port is >= 1 and <= 65_535)
        {
            return (value[1..separator], port);
        }
        return (value, 22);
    }
}

public static class KnownHostsHash
{
    public static bool Matches(string hashedHost, string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hashedHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var parts = hashedHost.Split('|');
        if (parts.Length != 4 || parts[1] != "1")
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var candidate = port == 22 ? host : $"[{host}]:{port}";
#pragma warning disable CA5350 // OpenSSH hashed known_hosts version 1 is defined as HMAC-SHA1.
            using var hmac = new HMACSHA1(salt);
#pragma warning restore CA5350
            var actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(candidate));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
