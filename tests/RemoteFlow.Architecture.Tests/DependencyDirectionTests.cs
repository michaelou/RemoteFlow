using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace RemoteFlow.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    private static readonly string[] _sharedProjects =
    [
        "RemoteFlow.Domain",
        "RemoteFlow.Application",
        "RemoteFlow.UI",
        "RemoteFlow.Infrastructure",
        "RemoteFlow.Persistence",
    ];

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

    [Fact]
    public void SharedProjectsDoNotReferenceWindowsRdpOrTargetWindows()
    {
        var repositoryRoot = FindRepositoryRoot();

        foreach (var projectName in _sharedProjects)
        {
            var projectPath = Path.Combine(repositoryRoot, "src", projectName, $"{projectName}.csproj");
            var project = XDocument.Load(projectPath);
            var targetFrameworks = project.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(element => element.Value)
                .ToArray();
            var projectReferences = project.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .ToArray();

            Assert.DoesNotContain(targetFrameworks, framework => framework.Contains("-windows", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(projectReferences, reference => reference!.Contains("RemoteFlow.Rdp.Windows", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DesktopManifestDeclaresSupportedWindowsWithoutOverridingDpiAwareness()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "src", "RemoteFlow.Desktop", "app.manifest");
        var manifest = XDocument.Load(manifestPath);
        var elementNames = manifest.Descendants().Select(element => element.Name.LocalName).ToArray();

        Assert.Contains("supportedOS", elementNames);
        Assert.DoesNotContain("dpiAware", elementNames);
        Assert.DoesNotContain("dpiAwareness", elementNames);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RemoteFlow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
