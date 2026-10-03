using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Docker;

namespace RemoteFlow.Application.Services.Docker;

/// <summary>The compose projects the Docker page has seen, kept in the settings store. One key for every
/// connection, so the whole list is read and written at once and two writers cannot interleave halves of
/// it. A project with no files to its name is not kept: it could not be brought up from here anyway.</summary>
public sealed class SettingsDockerComposeProjectMemory(ISettingsStore settings) : IDockerComposeProjectMemory, IDisposable
{
    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RememberedComposeProject>> RecallAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var stored = await _settings.Get(SettingKeys.DockerComposeProjects, cancellationToken).ConfigureAwait(false);
        return [.. (stored ?? []).Where(project => project.ConnectionId == connectionId)];
    }

    public Task RememberAsync(
        Guid connectionId,
        IReadOnlyList<DockerComposeProject> projects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var seen = projects
            .Where(project => project.ConfigFiles.Count > 0)
            .Select(project => new RememberedComposeProject(connectionId, project.Name, [.. project.ConfigFiles]))
            .ToList();
        return seen.Count == 0
            ? Task.CompletedTask
            : UpdateAsync(
                stored => [.. stored.Where(project => !(project.ConnectionId == connectionId
                    && seen.Any(next => next.Name == project.Name))), .. seen],
                cancellationToken);
    }

    public Task ForgetAsync(Guid connectionId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return UpdateAsync(
            stored => [.. stored.Where(project => !(project.ConnectionId == connectionId && project.Name == name))],
            cancellationToken);
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task UpdateAsync(
        Func<RememberedComposeProject[], RememberedComposeProject[]> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _settings.Get(SettingKeys.DockerComposeProjects, cancellationToken).ConfigureAwait(false)
                ?? [];
            var next = change(current);
            if (!next.SequenceEqual(current, RememberedProjectComparer.Instance))
            {
                await _settings.Set(SettingKeys.DockerComposeProjects, next, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>Records compare their arrays by reference; this compares the files, so an unchanged list is
    /// not written back on every refresh.</summary>
    private sealed class RememberedProjectComparer : IEqualityComparer<RememberedComposeProject>
    {
        public static RememberedProjectComparer Instance { get; } = new();

        public bool Equals(RememberedComposeProject? x, RememberedComposeProject? y)
        {
            return x is not null && y is not null
                && x.ConnectionId == y.ConnectionId
                && x.Name == y.Name
                && x.ConfigFiles.SequenceEqual(y.ConfigFiles);
        }

        public int GetHashCode(RememberedComposeProject obj)
        {
            return HashCode.Combine(obj.ConnectionId, obj.Name);
        }
    }
}
