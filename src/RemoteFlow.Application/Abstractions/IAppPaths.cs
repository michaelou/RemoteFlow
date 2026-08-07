namespace RemoteFlow.Application.Abstractions;

public interface IAppPaths
{
    string ConfigDirectory { get; }

    string DataDirectory { get; }

    string CacheDirectory { get; }

    string LogDirectory { get; }

    void EnsureDirectories();
}
