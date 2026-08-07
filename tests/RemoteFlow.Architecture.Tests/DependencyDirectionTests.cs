using System.Reflection;
using Xunit;

namespace RemoteFlow.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    private static readonly string[] _applicationReferences =
    [
        "RemoteFlow.Domain",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    [Fact]
    public void DomainReferencesOnlyTheBaseClassLibrary()
    {
        var invalidReferences = GetReferences(typeof(Domain.AssemblyMarker).Assembly)
            .Where(reference => !IsBaseClassLibrary(reference))
            .ToArray();

        Assert.Empty(invalidReferences);
    }

    [Fact]
    public void ApplicationReferencesOnlyApprovedDependencies()
    {
        var invalidReferences = GetReferences(typeof(Application.AssemblyMarker).Assembly)
            .Where(reference => !IsBaseClassLibrary(reference))
            .Except(_applicationReferences, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalidReferences);
    }

    [Fact]
    public void ApplicationDoesNotReferenceSshImplementations()
    {
        AssertDoesNotReference(
            typeof(Application.AssemblyMarker).Assembly,
            "Tmds.Ssh",
            "Renci.SshNet");
    }

    [Fact]
    public void UiDoesNotReferenceInfrastructureOrPersistence()
    {
        AssertDoesNotReference(
            typeof(UI.AssemblyMarker).Assembly,
            "RemoteFlow.Infrastructure",
            "RemoteFlow.Persistence");
    }

    [Fact]
    public void InfrastructureDoesNotReferenceUi()
    {
        AssertDoesNotReference(
            typeof(Infrastructure.AssemblyMarker).Assembly,
            "RemoteFlow.UI");
    }

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbiddenReferences)
    {
        var actualReferences = GetReferences(assembly);
        var violations = actualReferences.Intersect(forbiddenReferences, StringComparer.Ordinal).ToArray();

        Assert.Empty(violations);
    }

    private static IEnumerable<string> GetReferences(Assembly assembly)
    {
        return assembly.GetReferencedAssemblies().Select(reference => reference.Name!);
    }

    private static bool IsBaseClassLibrary(string assemblyName)
    {
        return assemblyName.StartsWith("System", StringComparison.Ordinal) ||
        assemblyName is "Microsoft.CSharp" or "Microsoft.VisualBasic" or "mscorlib" or "netstandard";
    }
}
