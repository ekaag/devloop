using DevLoop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

string? taskPath = null;
var checkOnly = false;
var words = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--task" && index + 1 < args.Length) taskPath = args[++index];
    else if (args[index] == "--check") checkOnly = true;
    else words.Add(args[index]);
}

// Later sources win: appsettings.json < user secrets < DEVLOOP_ environment variables.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .AddEnvironmentVariables("DEVLOOP_")
    .Build();
var options = new LoopOptions();
configuration.Bind(options);
var codex = configuration.GetSection("Codex").Get<CodexOptions>() ?? new CodexOptions();
var critic = configuration.GetSection("Critic").Get<CriticOptions>() ?? new CriticOptions();

var errors = OptionsValidator.Validate(options, codex, critic).ToList();
if (options.IsCodexMode && taskPath is null) errors.Add("Codex mode requires --task <task.json> with explicit acceptance criteria.");
if (taskPath is not null && !File.Exists(taskPath)) errors.Add($"Task file not found: {taskPath}");
if (checkOnly)
{
    Console.WriteLine($"Mode:              {options.Mode}");
    Console.WriteLine($"Workspace:         {options.Workspace}");
    Console.WriteLine($"Solution:          {options.Solution}");
    Console.WriteLine($"Artifacts:         {(options.ArtifactDirectory == "" ? "<workspace>/.devloop/runs" : options.ArtifactDirectory)}");
    Console.WriteLine($"Max iterations:    {options.MaxIterations}; run timeout {options.RunTimeoutSeconds}s; check timeout {options.CommandTimeoutSeconds}s");
    Console.WriteLine($"Codex:             {codex.Executable} (model: {(codex.Model == "" ? "Codex default" : codex.Model)}, timeout {codex.TimeoutSeconds}s)");
    Console.WriteLine($"Critic model:      {(critic.Model == "" ? "<not set>" : critic.Model)}");
    Console.WriteLine($"Critic API key:    {(critic.ApiKey == "" ? "<missing>" : "configured")}");
    foreach (var error in errors) Console.WriteLine($"ERROR: {error}");
    Console.WriteLine(errors.Count == 0 ? "Configuration is valid. Nothing was executed." : "Configuration is invalid.");
    return errors.Count == 0 ? 0 : 1;
}
if (errors.Count > 0)
{
    foreach (var error in errors) Console.Error.WriteLine(error);
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var task = taskPath is null ? null : await StaticPlanner.LoadAsync(taskPath, cancellation.Token);
    var request = task?.Request ?? (words.Count == 0 ? "Create a demonstration artifact recording this software request." : string.Join(" ", words));

    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddJsonConsole());
    services.AddSingleton(options);
    services.AddSingleton(codex);
    services.AddSingleton(critic);
    services.AddSingleton<Workspace>();
    services.AddSingleton<ArtifactStore>();
    services.AddSingleton(new SecretScanner(critic.ApiKey));
    services.AddSingleton<IProcessRunner, ProcessRunner>();
    services.AddSingleton<ICommandRunner, ControlledCommandRunner>();
    services.AddSingleton<ITesterAgent, TesterAgent>();
    services.AddSingleton<LoopOrchestrator>();
    if (options.IsCodexMode)
    {
        services.AddSingleton<IPlannerAgent>(new StaticPlanner(task!));
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<WorkspaceChangeInspector>();
        services.AddSingleton<IBuilderAgent, CodexBuilderAgent>();
        services.AddSingleton<IStructuredModelClient, OpenAIStructuredModelClient>();
        services.AddSingleton<ICriticAgent, CloudCriticAgent>();
    }
    else
    {
        services.AddSingleton<IModelClient, MockModelClient>();
        services.AddSingleton<IPlannerAgent>(provider => task is null
            ? new PlannerAgent(provider.GetRequiredService<IModelClient>()) : new StaticPlanner(task));
        services.AddSingleton<IBuilderAgent, MockBuilderAgent>();
        services.AddSingleton<ICriticAgent, CriticAgent>();
    }
    using var provider = services.BuildServiceProvider();
    var outcome = await provider.GetRequiredService<LoopOrchestrator>().RunAsync(request, cancellation.Token);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { outcome.Status, outcome.Iterations, outcome.StopReason, outcome.RemainingIssues, outcome.ReportPath }));
    return outcome.Status switch { "Succeeded" => 0, "Cancelled" => 2, "TimedOut" => 3, _ => 1 };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
