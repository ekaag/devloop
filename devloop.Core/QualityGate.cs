namespace DevLoop;

// Success requires every deterministic check and an independent review. A model's PASS never overrides a failure here.
public static class QualityGate
{
    public static GateResult Evaluate(Plan plan, BuildResult? build, TestResult? tests, Critique? critique)
    {
        var failures = new List<string>();
        if (build is null) failures.Add("Builder did not run.");
        else
        {
            failures.AddRange(build.Violations.Select(violation => $"Policy violation: {violation}"));
            if (!build.Succeeded) failures.Add($"Builder did not complete the task: {build.Summary}");
        }
        if (tests is null) failures.Add("Mandatory build/test checks did not run.");
        else if (!tests.Passed)
            failures.Add($"Fix mandatory build/test failures. Build exit={tests.Build.ExitCode}, timeout={tests.Build.TimedOut}; " +
                         $"test exit={tests.Test.ExitCode}, timeout={tests.Test.TimedOut}.");

        if (critique is null) failures.Add("Independent review did not run.");
        else if (critique.ReviewFailed) failures.AddRange(critique.Issues);
        else
        {
            if (!critique.Approved) failures.Add("Reviewer did not approve the change.");
            failures.AddRange(critique.Issues.Select(issue => $"Blocking finding: {issue}"));
            for (var index = 0; index < plan.AcceptanceCriteria.Length; index++)
            {
                var assessment = critique.Criteria.ElementAtOrDefault(index);
                if (assessment is not { Satisfied: true } || string.IsNullOrWhiteSpace(assessment.Evidence))
                    failures.Add($"Acceptance criterion lacks evidence of completion: {plan.AcceptanceCriteria[index]}");
            }
        }
        return new(failures.Count == 0, failures.Distinct().ToArray());
    }
}
