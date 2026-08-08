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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{request.FileName}' did not start a process.");
        return Task.CompletedTask;
    }
}
