using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Diagnostics;
using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.Infrastructure.Security;

namespace RemoteFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteFlowInfrastructure(
        this IServiceCollection services,
        IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(appPaths);

        services.TryAddSingleton<IAppPaths>(appPaths);
        services.TryAddSingleton<ISecureRandom, SecureRandom>();
        services.TryAddSingleton<ISecretRegistry, SecretRegistry>();
        services.TryAddSingleton<IGlobalExceptionHandler, GlobalExceptionHandler>();
        services.TryAddSingleton<WindowsCredentialProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICredentialProvider, WindowsCredentialProvider>());
        services.TryAddSingleton<CredentialProviderSelector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RedactingLoggerProvider>());
        return services;
    }
}
