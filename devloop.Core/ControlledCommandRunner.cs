using Microsoft.Extensions.Logging;
namespace DevLoop;

public sealed class ControlledCommandRunner(Workspace workspace, LoopOptions options, ILogger<ControlledCommandRunner> logger,
    IProcessRunner? processes = null) : ICommandRunner
{
    private readonly IProcessRunner runner = processes ?? new ProcessRunner();

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (executable != options.DotnetExecutable || arguments.Count != 3 ||
            arguments[0] is not ("build" or "test") || arguments[1] != options.Solution || arguments[2] != "--no-restore")
            throw new InvalidOperationException("Command denied. Network, deployment, and arbitrary commands require a separately approved adapter.");
        workspace.Resolve(options.Solution);
        if (options.CommandTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(options.CommandTimeoutSeconds));
        logger.LogInformation("Executing {Executable} {Arguments}", executable, string.Join(" ", arguments));
        // Build and test code comes from the builder, so it never sees credentials or DevLoop settings.
        var environment = ProcessEnvironment.Sanitized(new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1"
        });
        return await runner.RunAsync(new ProcessSpec(executable, arguments, workspace.Root, TimeSpan.FromSeconds(options.CommandTimeoutSeconds))
            { Environment = environment }, token);
    }
}
