using System.Collections.Concurrent;
using System.Security.Cryptography;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Services;

public static class HostKeyFingerprint
{
    public static string FormatSha256(ReadOnlySpan<byte> publicKeyBlob)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(publicKeyBlob, digest);
        return $"SHA256:{Convert.ToBase64String(digest).TrimEnd('=')}";
    }
}

public sealed class HostKeyVerifier(
    IHostKeyStore store,
    IHostKeyPrompt prompt,
    IClock clock,
    IGuidProvider guidProvider) : IHostKeyVerifier
{
    private const string _acceptAnyWarning = "Accepted without identity verification for this connection.";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _verificationLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IHostKeyStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IHostKeyPrompt _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IGuidProvider _guidProvider = guidProvider ?? throw new ArgumentNullException(nameof(guidProvider));

    public async Task<SshResult<HostKeyVerificationResult>> VerifyAsync(
        HostKeyVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var lockKey = $"{request.Host}\n{request.Port}\n{request.HostKey.Algorithm}";
        var verificationLock = _verificationLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await verificationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await VerifyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = verificationLock.Release();
        }
    }

    private async Task<SshResult<HostKeyVerificationResult>> VerifyCoreAsync(
        HostKeyVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var fingerprint = HostKeyFingerprint.FormatSha256(request.HostKey.PublicKey);
        var publicKeyBase64 = Convert.ToBase64String(request.HostKey.PublicKey);
        var known = await _store.GetAsync(
            request.Host,
            request.Port,
            request.HostKey.Algorithm,
            cancellationToken).ConfigureAwait(false);
        known ??= (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item =>
            string.Equals(item.KeyAlgorithm, request.HostKey.Algorithm, StringComparison.Ordinal) &&
            item.Host.StartsWith("|1|", StringComparison.Ordinal) &&
            KnownHostsHash.Matches(item.Host, request.Host, request.Port));

        if (known is not null)
        {
            if (known.TrustState == HostKeyTrust.Revoked)
            {
                return SshResult<HostKeyVerificationResult>.Fail(
                    SshError.HostKeyRevoked,
                    $"The {known.KeyAlgorithm} key for {request.Host}:{request.Port} is revoked.");
            }

            if (KeysMatch(known.PublicKeyBase64, request.HostKey.PublicKey))
            {
                _ = known.Observe(_clock.UtcNow);
                await _store.UpdateAsync(known, cancellationToken).ConfigureAwait(false);
                return Accepted(fingerprint, known.Source == HostKeySource.AcceptAny);
            }

            if (request.Policy != HostKeyPolicy.AcceptAny)
            {
                var mismatchDecision = await _prompt.PromptAsync(new HostKeyTrustPrompt(
                    request.Host,
                    request.Port,
                    request.HostKey.Algorithm,
                    fingerprint,
                    known.Sha256Fingerprint,
                    HostKeyRandomArt.Create(request.HostKey.PublicKey)), cancellationToken).ConfigureAwait(false);
                if (mismatchDecision == HostKeyPromptDecision.Reject)
                {
                    return SshResult<HostKeyVerificationResult>.Fail(
                        SshError.HostKeyMismatch,
                        $"The {known.KeyAlgorithm} key for {request.Host}:{request.Port} changed.");
                }

                if (mismatchDecision == HostKeyPromptDecision.AcceptOnce)
                {
                    return Accepted(fingerprint, isFlagged: true);
                }

                _ = known.UpdatePresentedKey(
                    publicKeyBase64,
                    fingerprint,
                    HostKeySource.UserAccepted,
                    _clock.UtcNow,
                    "A changed host key was explicitly accepted after a security warning.");
                await _store.UpdateAsync(known, cancellationToken).ConfigureAwait(false);
                return Accepted(fingerprint, isFlagged: true);
            }

            _ = known.UpdatePresentedKey(
                publicKeyBase64,
                fingerprint,
                HostKeySource.AcceptAny,
                _clock.UtcNow,
                _acceptAnyWarning);
            await _store.UpdateAsync(known, cancellationToken).ConfigureAwait(false);
            return Accepted(fingerprint, isFlagged: true);
        }

        var keysForHost = await _store.ListForHostAsync(
            request.Host,
            request.Port,
            cancellationToken).ConfigureAwait(false);
        var source = HostKeySource.AlgorithmRotation;
        var isFlagged = false;
        string? comment = null;
        if (keysForHost.Count == 0)
        {
            if (request.Policy == HostKeyPolicy.Strict)
            {
                return SshResult<HostKeyVerificationResult>.Fail(
                    SshError.HostKeyUnknown,
                    $"No trusted host key is stored for {request.Host}:{request.Port}.");
            }

            if (request.Policy == HostKeyPolicy.TrustOnFirstUse)
            {
                var decision = await _prompt.PromptAsync(new HostKeyTrustPrompt(
                    request.Host,
                    request.Port,
                    request.HostKey.Algorithm,
                    fingerprint,
                    RandomArt: HostKeyRandomArt.Create(request.HostKey.PublicKey)), cancellationToken).ConfigureAwait(false);
                if (decision == HostKeyPromptDecision.Reject)
                {
                    return SshResult<HostKeyVerificationResult>.Fail(
                        SshError.HostKeyUnknown,
                        $"The host key for {request.Host}:{request.Port} was not trusted.");
                }

                if (decision == HostKeyPromptDecision.AcceptOnce)
                {
                    return Accepted(fingerprint, isFlagged: false);
                }

                source = HostKeySource.UserAccepted;
            }
        }

        if (request.Policy == HostKeyPolicy.AcceptAny)
        {
            source = HostKeySource.AcceptAny;
            isFlagged = true;
            comment = _acceptAnyWarning;
        }

        var created = HostKey.Create(
            _guidProvider,
            request.Host,
            request.Port,
            request.HostKey.Algorithm,
            publicKeyBase64,
            fingerprint,
            HostKeyTrust.Trusted,
            source,
            comment,
            _clock.UtcNow);
        if (created.IsFailure)
        {
            return SshResult<HostKeyVerificationResult>.Fail(
                SshError.HostKeyUnknown,
                created.Error.Message);
        }

        await _store.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Accepted(fingerprint, isFlagged);
    }

    private static bool KeysMatch(string knownBase64, byte[] presentedKey)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(knownBase64),
                presentedKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static SshResult<HostKeyVerificationResult> Accepted(string fingerprint, bool isFlagged)
    {
        return SshResult<HostKeyVerificationResult>.Success(new(fingerprint, isFlagged));
    }

    private static void Validate(HostKeyVerificationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Port, 65_535);
        ArgumentNullException.ThrowIfNull(request.HostKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostKey.Algorithm);
        ArgumentOutOfRangeException.ThrowIfZero(request.HostKey.PublicKey.Length);
        if (!Enum.IsDefined(request.Policy))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The host-key policy is invalid.");
        }
    }
}

public static class HostKeyRandomArt
{
    private const int _width = 17;
    private const int _height = 9;
    private const string _symbols = " .o+=*BOX@%&#/^";

    public static string Create(ReadOnlySpan<byte> publicKeyBlob)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(publicKeyBlob, digest);
        var board = new int[_width * _height];
        var x = _width / 2;
        var y = _height / 2;
        var start = (y * _width) + x;
        foreach (var value in digest)
        {
            var remaining = value;
            for (var step = 0; step < 4; step++)
            {
                x = Math.Clamp(x + ((remaining & 1) == 0 ? -1 : 1), 0, _width - 1);
                y = Math.Clamp(y + ((remaining & 2) == 0 ? -1 : 1), 0, _height - 1);
                board[(y * _width) + x]++;
                remaining >>= 2;
            }
        }

        var end = (y * _width) + x;
        var result = new System.Text.StringBuilder();
        _ = result.Append('+').Append('-', _width).AppendLine("+");
        for (var row = 0; row < _height; row++)
        {
            _ = result.Append('|');
            for (var column = 0; column < _width; column++)
            {
                var index = (row * _width) + column;
                var symbol = index == start
                    ? 'S'
                    : (index == end ? 'E' : _symbols[Math.Min(board[index], _symbols.Length - 1)]);
                _ = result.Append(symbol);
            }
            _ = result.AppendLine("|");
        }
        return result.Append('+').Append('-', _width).Append('+').ToString();
    }
}

internal sealed class RejectingHostKeyPrompt : IHostKeyPrompt
{
    public ValueTask<bool> ConfirmTrustAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}
