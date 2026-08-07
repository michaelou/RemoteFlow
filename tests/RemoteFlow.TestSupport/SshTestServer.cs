using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using System.Security.Cryptography;
using System.Text;

namespace RemoteFlow.TestSupport;

public enum SshTestHostKey
{
    Primary = 1,
    Alternate = 2,
}

public sealed class SshTestServer : IAsyncDisposable
{
    public const string PasswordUsername = "password-user";
    public const string Password = "password-secret";
    public const string PublicKeyUsername = "key-user";
    public const string KeyboardInteractiveUsername = "interactive-user";
    public const string KeyboardInteractivePassword = "interactive-secret";

    private const int _sshPort = 22;
    private IFutureDockerImage? _image;
    private IContainer? _container;

    public string Hostname => _container?.Hostname ?? throw NotStarted();

    public ushort Port => _container?.GetMappedPublicPort(_sshPort) ?? throw NotStarted();

    public static string FixtureRoot => "/srv/remoteflow-fixtures";

    private static string BuildContextDirectory => Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "RemoteFlow.Ssh.IntegrationTests",
        "Sshd");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_container is not null)
        {
            return;
        }

        _image = new ImageFromDockerfileBuilder()
            .WithName(GetImageName())
            .WithContextDirectory(BuildContextDirectory)
            .WithDockerfileDirectory(BuildContextDirectory)
            .WithDockerfile("Dockerfile")
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync(cancellationToken).ConfigureAwait(false);

        _container = new ContainerBuilder(_image)
            .WithPortBinding(_sshPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(_sshPort))
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync(cancellationToken).ConfigureAwait(false);
        await WaitForSshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ExecResult> ExecAsync(
        IList<string> command,
        CancellationToken cancellationToken = default)
    {
        return (_container ?? throw NotStarted()).ExecAsync(command, cancellationToken);
    }

    public async Task UseHostKeyAsync(
        SshTestHostKey hostKey,
        CancellationToken cancellationToken = default)
    {
        var name = hostKey switch
        {
            SshTestHostKey.Primary => "primary",
            SshTestHostKey.Alternate => "alternate",
            _ => throw new ArgumentOutOfRangeException(nameof(hostKey)),
        };
        var expected = await FingerprintFileAsync(
            $"/opt/remoteflow/host_keys/{name}.pub",
            cancellationToken).ConfigureAwait(false);
        var result = await ExecAsync(
        [
            "/usr/local/bin/use-host-key",
            name,
        ], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "swap the SSH host key");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!string.Equals(
            await ReadPresentedHostKeyFingerprintAsync(timeout.Token).ConfigureAwait(false),
            expected,
            StringComparison.Ordinal))
        {
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    public Task<string> GetHostKeyFingerprintAsync(CancellationToken cancellationToken = default)
    {
        return ReadPresentedHostKeyFingerprintAsync(cancellationToken);
    }

    public async Task<string> GetPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecAsync(
        [
            "cat",
            "/opt/remoteflow/client_keys/id_ed25519",
        ], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "read the fixture SSH private key");
        return result.Stdout;
    }

    public async Task<HostKeyInfo> GetPresentedHostKeyAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecAsync(
        [
            "ssh-keyscan",
            "-T",
            "2",
            "-t",
            "ed25519",
            "localhost",
        ], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "read the presented SSH host key");
        var fields = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .First(parts => parts.Length >= 3 && parts[1] == "ssh-ed25519");
        var publicKey = Convert.FromBase64String(fields[2]);
        return new HostKeyInfo(fields[1], publicKey, HostKeyFingerprint.FormatSha256(publicKey));
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            _container = null;
        }

        if (_image is not null)
        {
            await _image.DisposeAsync().ConfigureAwait(false);
            _image = null;
        }
    }

    private async Task WaitForSshAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var result = await ExecAsync(
            [
                "ssh-keyscan",
                "-T",
                "1",
                "-t",
                "ed25519",
                "localhost",
            ], timeout.Token).ConfigureAwait(false);
            if (result.ExitCode == 0 && result.Stdout.Contains("ssh-ed25519", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadPresentedHostKeyFingerprintAsync(CancellationToken cancellationToken)
    {
        var result = await ExecAsync(
        [
            "/usr/local/bin/presented-host-key-fingerprint",
        ], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "read the presented SSH host key");
        return result.Stdout.Trim();
    }

    private async Task<string> FingerprintFileAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ExecAsync(
        [
            "ssh-keygen",
            "-lf",
            path,
            "-E",
            "sha256",
        ], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "read an SSH host-key fingerprint");
        return result.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not find the RemoteFlow repository root from the test output directory.");
    }

    private static string GetImageName()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(BuildContextDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(BuildContextDirectory, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(path));
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant();
        return $"remoteflow-sshd-tests:{digest}";
    }

    private static void EnsureSuccess(ExecResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not {operation}. Exit code: {result.ExitCode}. Error: {result.Stderr}");
        }
    }

    private static InvalidOperationException NotStarted()
    {
        return new InvalidOperationException("The SSH test server has not been started.");
    }
}
