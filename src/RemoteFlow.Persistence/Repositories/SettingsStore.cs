using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Repositories;

public sealed class SettingsStore(
    IDbContextFactory<RemoteFlowDbContext> contextFactory,
    IClock clock) : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IDbContextFactory<RemoteFlowDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SettingsStore(IDbContextFactory<RemoteFlowDbContext> contextFactory)
        : this(contextFactory, SystemClock.Instance)
    {
    }

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public async Task<T> Get<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        await SeedDefaults(cancellationToken).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var json = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == key.Name)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (json is null)
        {
            return key.DefaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, _serializerOptions)!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Setting '{key.Name}' does not contain a valid {typeof(T).Name} value.", exception);
        }
    }

    public async Task Set<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        await SeedDefaults(cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(value, _serializerOptions);
        var changed = false;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var setting = await context.Settings.FindAsync([key.Name], cancellationToken).ConfigureAwait(false);
            if (setting is null)
            {
                _ = context.Settings.Add(Setting.Create(key.Name, json, _clock.UtcNow).Value);
                changed = true;
            }
            else if (!string.Equals(setting.Value, json, StringComparison.Ordinal))
            {
                _ = setting.SetValue(json, _clock.UtcNow);
                changed = true;
            }

            if (changed)
            {
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _writeLock.Release();
        }

        if (changed)
        {
            OnSettingChanged(key.Name);
        }
    }

    public async Task SeedDefaults(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existingKeyValues = await context.Settings.AsNoTracking()
                .Select(setting => setting.Key)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var existingKeys = existingKeyValues.ToHashSet(StringComparer.Ordinal);
            foreach (var key in SettingKeys.All.Where(key => !existingKeys.Contains(key.Name)))
            {
                var json = JsonSerializer.Serialize(key.UntypedDefaultValue, key.ValueType, _serializerOptions);
                _ = context.Settings.Add(Setting.Create(key.Name, json, _clock.UtcNow).Value);
            }

            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writeLock.Dispose();
        _disposed = true;
    }

    private void OnSettingChanged(string key)
    {
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key));
    }
}
