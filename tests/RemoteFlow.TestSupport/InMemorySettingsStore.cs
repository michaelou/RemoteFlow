using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.TestSupport;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public Task<T> Get<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(_values.TryGetValue(key.Name, out var value) ? (T)value! : key.DefaultValue);
    }

    public Task Set<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(key);
        _values[key.Name] = value;
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key.Name));
        return Task.CompletedTask;
    }

    public Task SeedDefaults(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
