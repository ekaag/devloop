namespace DevLoop;

public record Plan(string Request, string[] Steps, string[] AcceptanceCriteria)
{
    // Criteria the reviewer may comment on but that do not block success.
    public string[] OptionalCriteria { get; init; } = [];
}
public record TaskDefinition(string Request, string[] AcceptanceCriteria)
{
    public string[]? Steps { get; init; }
    public string[]? OptionalCriteria { get; init; }
}
public record FileChange(string Path, string Content);
public record ModelUsage(string Provider, string Model, long InputTokens, long OutputTokens, long CachedInputTokens = 0)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
public record CriterionReport(string Criterion, bool Satisfied, string Evidence);
public record BuildResult(bool Succeeded, FileChange[] Changes, string Summary)
{
    // Process outcome is separate from task outcome: a zero exit code does not mean the task was done.
    public CommandResult? Process { get; init; }
    public bool TaskCompleted { get; init; }
    public string Diff { get; init; } = "";
    public string? DiffHash { get; init; }
    public string[] ChangedFiles { get; init; } = [];
    public string[] Violations { get; init; } = [];
    public CriterionReport[] Criteria { get; init; } = [];
    public bool Retryable { get; init; } = true;
    public ModelUsage? Usage { get; init; }
}
public record CommandResult(string Command, int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Passed => ExitCode == 0 && !TimedOut;
}
public record TestResult(CommandResult Build, CommandResult Test)
{
    public bool Passed => Build.Passed && Test.Passed;
}
public record Critique(bool Approved, string[] Issues)
{
    public string Decision { get; init; } = Approved ? "PASS" : "FAIL";
    public string[] Recommendations { get; init; } = [];
    public string BuilderFeedback { get; init; } = "";
    public CriterionReport[] Criteria { get; init; } = [];
    // True when no trustworthy review exists (API failure, malformed or incomplete response).
    public bool ReviewFailed { get; init; }
    public ModelUsage? Usage { get; init; }
}
public record GateResult(bool Passed, string[] Failures);
public record Iteration(int Number, Plan Plan, BuildResult? Build, TestResult? Tests, Critique? Critic)
{
    public string[] PriorFeedback { get; init; } = [];
    public string? ReviewSkippedReason { get; init; }
    public GateResult? Gate { get; init; }
}
public record IterationSummary(int Number, bool BuilderSucceeded, bool? ChecksPassed, string? Decision, bool GatePassed, string[] ChangedFiles, string[] Failures);
public record LoopOutcome(string Status, int Iterations, string[] RemainingIssues, string RunDirectory)
{
    public string Request { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }
    public string? StopReason { get; init; }
    public IterationSummary[] History { get; init; } = [];
    public ModelUsage[] Usage { get; init; } = [];
    public string? ReportPath { get; init; }
}
public record ModelRequest(string Role, string Payload);
public record StructuredModelRequest(string Instructions, string Input, string SchemaName, string JsonSchema, int MaxOutputTokens);
public record StructuredModelResponse(string? Text, bool Completed, string? FailureReason, ModelUsage? Usage);
public record GitChange(string Status, string Path);
public record ChangeSet(string[] Files, string Diff, string Hash, string[] Violations);

public interface IModelClient { Task<string> CompleteAsync(ModelRequest request, CancellationToken token); }
public interface IStructuredModelClient { Task<StructuredModelResponse> CompleteJsonAsync(StructuredModelRequest request, CancellationToken token); }
public interface IPlannerAgent { Task<Plan> PlanAsync(string request, CancellationToken token); }
public interface IBuilderAgent { Task<BuildResult> BuildAsync(Plan plan, int iteration, string[] feedback, CancellationToken token); }
public interface ITesterAgent { Task<TestResult> TestAsync(CancellationToken token); }
public interface ICriticAgent { Task<Critique> ReviewAsync(Iteration iteration, CancellationToken token); }
public interface ICommandRunner { Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token); }
public interface IProcessRunner { Task<CommandResult> RunAsync(ProcessSpec spec, CancellationToken token); }
public interface IGitClient
{
    Task<string> GetTopLevelAsync(CancellationToken token);
    Task<string> GetHeadAsync(CancellationToken token);
    Task<GitChange[]> GetChangesAsync(CancellationToken token);
    Task<string> GetDiffAsync(string baseline, CancellationToken token);
}

public sealed class WorkspacePolicyException(string message) : InvalidOperationException(message);
public sealed class GitException(string message) : InvalidOperationException(message);
