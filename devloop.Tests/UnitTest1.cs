using DevLoop;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace devloop.Tests;

public sealed class LoopTests
{
    private static readonly CommandResult Passed = new("check", 0, "full output", "");
    private static readonly CommandResult Failed = new("check", 1, "", "failure details");
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.devloop/test-workspaces"));

    private static (LoopOrchestrator Loop, LoopOptions Options) Create(int failures = 0,
        ITesterAgent? tester = null, ICriticAgent? critic = null, IBuilderAgent? builder = null)
    {
        var root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new LoopOptions { Workspace = root, MockFailures = failures };
        var workspace = new Workspace(options);
        var model = new MockModelClient();
        return (new(new PlannerAgent(model), builder ?? new MockBuilderAgent(workspace, options),
            tester ?? new StubTester(), critic ?? new CriticAgent(model),
            new ArtifactStore(options, workspace), options, NullLogger<LoopOrchestrator>.Instance), options);
    }

    [Fact]
    public async Task SuccessPersistsPlanChangesOutputsAndOutcome()
    {
        var (loop, _) = Create();
        var outcome = await loop.RunAsync("implement a feature");
        Assert.Equal("Succeeded", outcome.Status);
        Assert.Equal(1, outcome.Iterations);
        var iteration = JsonSerializer.Deserialize<Iteration>(await File.ReadAllTextAsync(Path.Combine(outcome.RunDirectory, "iteration-1.json")))!;
        Assert.Contains("implement a feature", iteration.Build!.Changes[0].Content);
        Assert.Equal("full output", iteration.Tests!.Test.StandardOutput);
        Assert.True(iteration.Critic!.Approved);
        Assert.True(File.Exists(Path.Combine(outcome.RunDirectory, "plan.json")));
        Assert.True(File.Exists(Path.Combine(outcome.RunDirectory, "outcome.json")));
    }

    [Fact]
    public async Task RetryReceivesActionableCriticFeedback()
    {
        var (loop, _) = Create(failures: 1);
        var outcome = await loop.RunAsync("feature");
        Assert.Equal("Succeeded", outcome.Status);
        Assert.Equal(2, outcome.Iterations);
        var text = await File.ReadAllTextAsync(Path.Combine(outcome.RunDirectory, "iteration-2.json"));
        Assert.Contains("Complete the artifact", text);
    }

    [Fact]
    public async Task MaximumIterationsStopsAndReportsRemainingIssues()
    {
        var (loop, _) = Create(failures: 10);
        var outcome = await loop.RunAsync("feature");
        Assert.Equal("Failed", outcome.Status);
        Assert.Equal(3, outcome.Iterations);
        Assert.NotEmpty(outcome.RemainingIssues);
        Assert.False(File.Exists(Path.Combine(outcome.RunDirectory, "iteration-4.json")));
    }

    [Fact]
    public async Task FailedMandatoryChecksCannotBeOverriddenByCritic()
    {
        var (loop, options) = Create(tester: new StubTester(false), critic: new ApprovingCritic());
        options.MaxIterations = 1;
        var outcome = await loop.RunAsync("feature");
        Assert.Equal("Failed", outcome.Status);
        Assert.Contains(outcome.RemainingIssues, x => x.Contains("mandatory"));
    }

    [Fact]
    public async Task BuilderFailureCannotBeOverridden()
    {
        var (loop, options) = Create(builder: new FailingBuilder(), critic: new ApprovingCritic());
        options.MaxIterations = 1;
        Assert.Equal("Failed", (await loop.RunAsync("feature")).Status);
    }

    [Fact]
    public async Task CancellationPersistsTerminalOutcome()
    {
        var (loop, _) = Create();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var outcome = await loop.RunAsync("feature", source.Token);
        Assert.Equal("Cancelled", outcome.Status);
        Assert.Equal(0, outcome.Iterations);
        Assert.True(File.Exists(Path.Combine(outcome.RunDirectory, "outcome.json")));
    }

    [Fact]
    public async Task CancellationDuringAgentStopsLoop()
    {
        using var source = new CancellationTokenSource();
        var (loop, _) = Create(tester: new CancellingTester(source));
        var outcome = await loop.RunAsync("feature", source.Token);
        Assert.Equal("Cancelled", outcome.Status);
        Assert.Equal(1, outcome.Iterations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task InvalidIterationLimitRejected(int maximum)
    {
        var (loop, options) = Create();
        options.MaxIterations = maximum;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => loop.RunAsync("feature"));
    }

    [Fact]
    public async Task TesterRunsBothCommandsAndRetainsFailureOutput()
    {
        var runner = new RecordingRunner();
        var result = await new TesterAgent(runner, new()).TestAsync(default);
        Assert.False(result.Passed);
        Assert.Equal(new[] { "build", "test" }, runner.Calls);
        Assert.Equal("failure details", result.Build.StandardError);
    }

    [Theory]
    [InlineData("git", "reset", "--hard")]
    [InlineData("dotnet", "restore", "devloop.sln")]
    [InlineData("dotnet", "publish", "devloop.sln")]
    [InlineData("powershell", "-Command", "Remove-Item")]
    public async Task UnsafeCommandsAreRejected(string executable, string verb, string argument)
    {
        var (_, options) = Create();
        var runner = new ControlledCommandRunner(new(options), options, NullLogger<ControlledCommandRunner>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(executable, [verb, argument], default));
    }

    [Fact]
    public void WorkspaceRejectsTraversalAndSiblingPrefix()
    {
        var (_, options) = Create();
        var workspace = new Workspace(options);
        Assert.Throws<InvalidOperationException>(() => workspace.Resolve("../outside"));
        Assert.Throws<InvalidOperationException>(() => workspace.Resolve(options.Workspace + "-sibling/file"));
    }

    [Fact]
    public async Task TimedOutCheckCannotPassEvenWithZeroExitCode()
    {
        var (loop, options) = Create(tester: new TimeoutTester(), critic: new ApprovingCritic());
        options.MaxIterations = 1;
        Assert.Equal("Failed", (await loop.RunAsync("feature")).Status);
    }

    [Fact]
    public async Task AgentExceptionProducesFailureReport()
    {
        var (loop, _) = Create(builder: new ThrowingBuilder());
        var outcome = await loop.RunAsync("feature");
        Assert.Equal("Failed", outcome.Status);
        Assert.Contains(outcome.RemainingIssues, x => x.Contains("adapter unavailable"));
        Assert.True(File.Exists(Path.Combine(outcome.RunDirectory, "outcome.json")));
    }

    [Fact]
    public async Task ConfiguredMaximumIsRespected()
    {
        var (loop, options) = Create(failures: 10);
        options.MaxIterations = 2;
        Assert.Equal(2, (await loop.RunAsync("feature")).Iterations);
    }

    private sealed class TimeoutTester : ITesterAgent
    {
        public Task<TestResult> TestAsync(CancellationToken token) =>
            Task.FromResult(new TestResult(Passed with { TimedOut = true }, Passed));
    }
    private sealed class ThrowingBuilder : IBuilderAgent
    {
        public Task<BuildResult> BuildAsync(Plan plan, int iteration, string[] feedback, CancellationToken token) =>
            throw new InvalidOperationException("adapter unavailable");
    }
    private sealed class StubTester(bool passed = true) : ITesterAgent
    {
        public Task<TestResult> TestAsync(CancellationToken token) => Task.FromResult(new TestResult(passed ? Passed : Failed, Passed));
    }
    private sealed class ApprovingCritic : ICriticAgent
    {
        public Task<Critique> ReviewAsync(Iteration iteration, CancellationToken token) => Task.FromResult(new Critique(true, []));
    }
    private sealed class FailingBuilder : IBuilderAgent
    {
        public Task<BuildResult> BuildAsync(Plan plan, int iteration, string[] feedback, CancellationToken token) =>
            Task.FromResult(new BuildResult(false, [], "Builder failed."));
    }
    private sealed class CancellingTester(CancellationTokenSource source) : ITesterAgent
    {
        public Task<TestResult> TestAsync(CancellationToken token)
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }
    private sealed class RecordingRunner : ICommandRunner
    {
        public List<string> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments[0]);
            return Task.FromResult(Failed);
        }
    }
}
