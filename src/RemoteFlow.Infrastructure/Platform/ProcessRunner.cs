using System.Diagnostics;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class ProcessRunner : IProcessRunner
{
    public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = request.UseShellExecute,
            Verb = request.Verb ?? string.Empty,
            CreateNoWindow = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!request.UseShellExecute && request.EnvironmentVariables is not null)
        {
            foreach (var variable in request.EnvironmentVariables)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        using var process = Process.Start(startInfo);
        if (process is null && !request.UseShellExecute)
        {
            throw new InvalidOperationException($"'{request.FileName}' did not start a process.");
        }

        // A shell launch reports no process whenever the shell itself served the request: it showed
        // its "Open with" picker for a file it has no association for, or handed the file to an
        // already running instance of the associated application. Neither one is a failure.
        return Task.CompletedTask;
    }
}
