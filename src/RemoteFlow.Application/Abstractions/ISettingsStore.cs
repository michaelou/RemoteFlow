namespace RemoteFlow.Application.Abstractions;

#pragma warning disable CA1716 // The issue deliberately specifies the cross-platform settings API as Get<T>/Set<T>.
public interface ISettingsStore
{
    event EventHandler<SettingChangedEventArgs>? SettingChanged;

    Task<T> Get<T>(SettingKey<T> key, CancellationToken cancellationToken = default);

    Task Set<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default);

    Task SeedDefaults(CancellationToken cancellationToken = default);
}
#pragma warning restore CA1716

public sealed class SettingChangedEventArgs(string key) : EventArgs
{
    public string Key { get; } = key;
}
