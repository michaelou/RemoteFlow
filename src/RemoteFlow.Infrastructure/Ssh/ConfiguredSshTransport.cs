using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

public sealed class ConfiguredSshTransport : ISshTransport
{
    private readonly ISettingsStore _settings;
    private readonly ISshTransport _tmds;
    private readonly ISshTransport _sshNet;

    public ConfiguredSshTransport(
        ISettingsStore settings,
        TmdsSshTransport tmds,
        SshNetTransport sshNet)
        : this(settings, (ISshTransport)tmds, sshNet)
    {
    }

    internal ConfiguredSshTransport(
        ISettingsStore settings,
        ISshTransport tmds,
        ISshTransport sshNet)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tmds = tmds ?? throw new ArgumentNullException(nameof(tmds));
        _sshNet = sshNet ?? throw new ArgumentNullException(nameof(sshNet));
    }

    public async Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        var selected = await _settings.Get(SettingKeys.SshTransport, cancellationToken).ConfigureAwait(false);
        var transport = selected switch
        {
            SshTransport.Tmds => _tmds,
            SshTransport.SshNet => _sshNet,
            _ => throw new InvalidOperationException($"Unknown SSH transport setting value: {(int)selected}."),
        };

        return await transport.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
