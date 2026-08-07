namespace RemoteFlow.Infrastructure.Security;

public sealed class CredentialSecurityState
{
    public const string KeyringUnavailableBanner = "OS keyring unavailable - using passphrase vault";

    private volatile bool _isKeyringUnavailable;

    public event EventHandler? Changed;

    public bool IsKeyringUnavailable => _isKeyringUnavailable;

    public string? BannerMessage => IsKeyringUnavailable ? KeyringUnavailableBanner : null;

    internal void SetKeyringUnavailable(bool unavailable)
    {
        if (_isKeyringUnavailable == unavailable)
        {
            return;
        }

        _isKeyringUnavailable = unavailable;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
