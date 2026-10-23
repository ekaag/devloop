using Microsoft.Extensions.Logging;
namespace DevLoop;

// Plan → Build → deterministic checks → independent review → quality gate → PASS or retry.
public sealed class LoopOrchestrator(IPlannerAgent planner, IBuilderAgent builder, ITesterAgent tester,
    ICriticAgent critic, ArtifactStore artifacts, LoopOptions options, ILogger<LoopOrchestrator> logger)
{
    public async Task<LoopOutcome> RunAsync(string request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Task must not be empty.");
        if (options.MaxIterations is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(options.MaxIterations), "Use one to three iterations.");
        if (options.RunTimeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(options.RunTimeoutSeconds));
        var started = DateTimeOffset.UtcNow;
        var run = artifacts.BeginRun();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RunTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var runToken = linked.Token;
        var history = new List<Iteration>();
        Plan? plan = null;
        var count = 0;
        var feedback = Array.Empty<string>();
        var status = "Failed";
        string? stopReason = null;
        string? previousDiffHash = null;
        try
        {
            runToken.ThrowIfCancellationRequested();
            plan = await planner.PlanAsync(request, runToken);
            await artifacts.WriteJsonAsync("plan.json", plan, runToken);
            for (var number = 1; number <= options.MaxIterations; number++)
            {
                runToken.ThrowIfCancellationRequested();
                count = number;
                logger.LogInformation("Starting iteration {Iteration} of {Maximum}", number, options.MaxIterations);
                var path = $"iteration-{number}.json";
                var iteration = new Iteration(number, plan, null, null, null) { PriorFeedback = feedback };
                await artifacts.WriteJsonAsync(path, iteration, runToken);

                var built = await builder.BuildAsync(plan, number, feedback, runToken);
                iteration = iteration with { Build = built };
                await artifacts.WriteJsonAsync(path, iteration, runToken);

                // Code that violated policy is not executed.
                TestResult? tests = null;
                if (built.Violations.Length == 0)
                {
                    tests = await tester.TestAsync(runToken);
                    iteration = iteration with { Tests = tests };
                    await artifacts.WriteJsonAsync(path, iteration, runToken);
                }

                // The paid reviewer only sees iterations that already passed every deterministic check.
                Critique? review = null;
                if (built.Succeeded && tests?.Passed == true) review = await critic.ReviewAsync(iteration, runToken);
                else iteration = iteration with { ReviewSkippedReason = "Deterministic builder or build/test checks failed." };
                runToken.ThrowIfCancellationRequested();

                var gate = QualityGate.Evaluate(plan, built, tests, review);
                iteration = iteration with { Critic = review, Gate = gate };
                await artifacts.WriteJsonAsync(path, iteration, runToken);
                history.Add(iteration);
                if (gate.Passed) { status = "Succeeded"; feedback = []; break; }

                feedback = BuildFeedback(gate, tests, review);
                stopReason = built.Violations.Length > 0 ? "Stopped: workspace or security policy violation."
                    : !built.Retryable ? "Stopped: the builder failure cannot be fixed by retrying."
                    : review?.ReviewFailed == true ? "Stopped: the cloud critic did not produce a valid review."
                    : built.DiffHash is not null && built.DiffHash == previousDiffHash ? "Stopped: no progress; the workspace diff did not change since the previous iteration."
                    : number == options.MaxIterations ? "Stopped: maximum iterations reached."
                    : null;
                if (stopReason is not null) { logger.LogWarning("{Reason}", stopReason); break; }
                previousDiffHash = built.DiffHash;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { status = "Cancelled"; feedback = ["Run cancelled."]; stopReason = "Cancelled by the operator."; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { status = "TimedOut"; feedback = [$"Run exceeded {options.RunTimeoutSeconds} seconds."]; stopReason = "Stopped: run timeout."; }
        catch (WorkspacePolicyException exception)
        {
            logger.LogError("Workspace policy violation: {Message}", exception.Message);
            feedback = [exception.Message];
            stopReason = "Stopped: workspace policy violation.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Run failed");
            feedback = [$"{exception.GetType().Name}: {exception.Message}"];
            stopReason = "Stopped: unexpected error.";
        }

        var outcome = new LoopOutcome(status, count, feedback, run)
        {
            Request = request,
            StartedUtc = started,
            FinishedUtc = DateTimeOffset.UtcNow,
            StopReason = status == "Succeeded" ? null : stopReason,
            History = history.Select(Summarize).ToArray(),
            Usage = AggregateUsage(history),
            ReportPath = Path.Combine(run, "report.md")
        };
        await artifacts.WriteJsonAsync("outcome.json", outcome, CancellationToken.None);
        await artifacts.WriteTextAsync("report.md", ReportWriter.ToMarkdown(outcome, plan, history), CancellationToken.None);
        foreach (var usage in outcome.Usage)
            logger.LogInformation("Total model usage {Provider}/{Model}: input={Input} output={Output}", usage.Provider, usage.Model, usage.InputTokens, usage.OutputTokens);
        logger.LogInformation("Run finished with {Status} after {Iterations} iterations", status, count);
        return outcome;
    }

    private static string[] BuildFeedback(GateResult gate, TestResult? tests, Critique? review)
    {
        var feedback = new List<string>(gate.Failures);
        if (tests is { Passed: false })
            feedback.Add($"Build output (exit {tests.Build.ExitCode}):\n{Text.Tail($"{tests.Build.StandardOutput}\n{tests.Build.StandardError}".Trim(), 4_000)}\n\n" +
                         $"Test output (exit {tests.Test.ExitCode}):\n{Text.Tail($"{tests.Test.StandardOutput}\n{tests.Test.StandardError}".Trim(), 4_000)}");
        if (review is { ReviewFailed: false } && !string.IsNullOrWhiteSpace(review.BuilderFeedback))
            feedback.Add($"Reviewer guidance: {review.BuilderFeedback}");
        return feedback.ToArray();
    }

    private static IterationSummary Summarize(Iteration iteration) => new(iteration.Number, iteration.Build?.Succeeded == true,
        iteration.Tests?.Passed, iteration.Critic?.Decision, iteration.Gate?.Passed == true, iteration.Build?.ChangedFiles ?? [], iteration.Gate?.Failures ?? []);

    private static ModelUsage[] AggregateUsage(IEnumerable<Iteration> history) =>
        history.SelectMany(i => new[] { i.Build?.Usage, i.Critic?.Usage }).OfType<ModelUsage>()
            .GroupBy(u => (u.Provider, u.Model))
            .Select(g => new ModelUsage(g.Key.Provider, g.Key.Model, g.Sum(u => u.InputTokens), g.Sum(u => u.OutputTokens), g.Sum(u => u.CachedInputTokens)))
            .ToArray();
}
