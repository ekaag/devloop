using System.Text;

namespace DevLoop;

public static class ReportWriter
{
    public static string ToMarkdown(LoopOutcome outcome, Plan? plan, IReadOnlyList<Iteration> history)
    {
        var report = new StringBuilder();
        report.AppendLine("# DevLoop run report").AppendLine();
        report.AppendLine($"- **Status:** {outcome.Status}");
        report.AppendLine($"- **Iterations:** {outcome.Iterations}");
        report.AppendLine($"- **Started:** {outcome.StartedUtc:u}");
        report.AppendLine($"- **Duration:** {(outcome.FinishedUtc - outcome.StartedUtc).TotalSeconds:F1} s");
        if (outcome.StopReason is not null) report.AppendLine($"- **Stop reason:** {outcome.StopReason}");
        report.AppendLine($"- **Artifacts:** `{outcome.RunDirectory}`").AppendLine();
        report.AppendLine("## Request").AppendLine().AppendLine(plan?.Request ?? outcome.Request).AppendLine();

        if (plan is not null)
        {
            var assessment = history.LastOrDefault(i => i.Critic is { ReviewFailed: false })?.Critic?.Criteria ?? [];
            report.AppendLine("## Acceptance criteria").AppendLine();
            report.AppendLine("| # | Criterion | Reviewer result | Evidence |").AppendLine("| --- | --- | --- | --- |");
            for (var index = 0; index < plan.AcceptanceCriteria.Length; index++)
            {
                var item = assessment.ElementAtOrDefault(index);
                var result = item is null ? "not assessed" : item.Satisfied ? "met" : "not met";
                report.AppendLine($"| {index + 1} | {Cell(plan.AcceptanceCriteria[index])} | {result} | {Cell(item?.Evidence ?? "")} |");
            }
            report.AppendLine();
        }

        report.AppendLine("## Iterations").AppendLine();
        foreach (var iteration in history)
        {
            report.AppendLine($"### Iteration {iteration.Number}").AppendLine();
            var build = iteration.Build;
            if (build is not null)
            {
                var process = build.Process is null ? "" : $" (exit {build.Process.ExitCode}{(build.Process.TimedOut ? ", timed out" : "")})";
                report.AppendLine($"- **Builder:** {(build.Succeeded ? "succeeded" : "failed")}{process}; task completed: {build.TaskCompleted}");
                report.AppendLine($"- **Changed files:** {(build.ChangedFiles.Length == 0 ? "none" : string.Join(", ", build.ChangedFiles.Select(f => $"`{f}`")))}");
            }
            report.AppendLine(iteration.Tests is null ? "- **Checks:** not run"
                : $"- **Checks:** build exit {iteration.Tests.Build.ExitCode}, test exit {iteration.Tests.Test.ExitCode}");
            report.AppendLine(iteration.Critic is null ? $"- **Review:** skipped. {iteration.ReviewSkippedReason}"
                : $"- **Review:** {iteration.Critic.Decision}");
            report.AppendLine($"- **Gate:** {(iteration.Gate?.Passed == true ? "passed" : "failed")}");
            foreach (var failure in iteration.Gate?.Failures ?? []) report.AppendLine($"  - {Line(failure)}");
            foreach (var recommendation in iteration.Critic?.Recommendations ?? []) report.AppendLine($"  - Recommendation: {Line(recommendation)}");
            report.AppendLine();
        }

        if (outcome.Status != "Succeeded" && outcome.RemainingIssues.Length > 0)
        {
            report.AppendLine("## Remaining issues").AppendLine();
            foreach (var issue in outcome.RemainingIssues) report.AppendLine($"- {Line(issue)}");
            report.AppendLine();
        }

        report.AppendLine("## Model usage").AppendLine();
        if (outcome.Usage.Length == 0) report.AppendLine("No usage reported by providers.");
        else
        {
            report.AppendLine("| Provider | Model | Input tokens | Cached input | Output tokens |").AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var usage in outcome.Usage)
                report.AppendLine($"| {usage.Provider} | {Cell(usage.Model)} | {usage.InputTokens} | {usage.CachedInputTokens} | {usage.OutputTokens} |");
        }
        return report.ToString();
    }

    private static string Line(string text) => Text.Truncate(text.ReplaceLineEndings(" "), 600);

    private static string Cell(string text) => Line(text).Replace("|", "\\|");
}
