using System.Text.Json;

namespace DevLoop;

public sealed class PlannerAgent(IModelClient model) : IPlannerAgent
{
    public async Task<Plan> PlanAsync(string request, CancellationToken token)
    {
        var plan = JsonSerializer.Deserialize<Plan>(await model.CompleteAsync(new("planner", request), token))
            ?? throw new InvalidOperationException("Model returned no plan.");
        if (plan.Steps.Length == 0 || plan.AcceptanceCriteria.Length == 0) throw new InvalidOperationException("Plan requires steps and criteria.");
        return plan;
    }
}
// Uses an operator-supplied task definition as the plan, so acceptance criteria are never invented by a model.
public sealed class StaticPlanner(TaskDefinition task) : IPlannerAgent
{
    public Task<Plan> PlanAsync(string request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(task.Request)) throw new InvalidOperationException("Task request must not be empty.");
        var criteria = (task.AcceptanceCriteria ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
        if (criteria.Length == 0) throw new InvalidOperationException("Task must define at least one acceptance criterion.");
        return Task.FromResult(new Plan(task.Request, task.Steps ?? [], criteria) { OptionalCriteria = task.OptionalCriteria ?? [] });
    }

    public static async Task<TaskDefinition> LoadAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TaskDefinition>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token)
            ?? throw new InvalidOperationException($"Task file is empty: {path}");
    }
}
public sealed class CriticAgent(IModelClient model) : ICriticAgent
{
    public async Task<Critique> ReviewAsync(Iteration iteration, CancellationToken token) =>
        JsonSerializer.Deserialize<Critique>(await model.CompleteAsync(new("critic", JsonSerializer.Serialize(iteration)), token))
        ?? throw new InvalidOperationException("Model returned no critique.");
}
public sealed class MockModelClient : IModelClient
{
    public Task<string> CompleteAsync(ModelRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object response;
        if (request.Role == "planner")
            response = new Plan(request.Payload,
                ["Write a local demonstration artifact", "Build and test the solution", "Review the implementation"],
                ["Artifact contains the original request and status complete", "Build and tests exit with code zero", "Critic approves maintainability and security"]);
        else if (request.Role == "critic")
        {
            var iteration = JsonSerializer.Deserialize<Iteration>(request.Payload)!;
            var issues = new List<string>();
            if (iteration.Build?.Succeeded != true) issues.Add("Builder must complete successfully.");
            if (iteration.Tests?.Passed != true) issues.Add("Fix failing build/test checks.");
            if (iteration.Build?.Changes.Length != 1 || iteration.Build.Changes[0].Path != ".devloop/demo/implementation.json")
                issues.Add("Expected demonstration artifact is missing or changes exceed mock scope.");
            else
            {
                using var document = JsonDocument.Parse(iteration.Build.Changes[0].Content);
                if (document.RootElement.GetProperty("status").GetString() != "complete" ||
                    document.RootElement.GetProperty("request").GetString() != iteration.Plan.Request)
                    issues.Add("Complete the artifact and preserve the original request.");
            }
            response = new Critique(issues.Count == 0, issues.ToArray())
            {
                Criteria = iteration.Plan.AcceptanceCriteria.Select(c => new CriterionReport(c, issues.Count == 0, issues.Count == 0 ? "Mock verification." : "")).ToArray()
            };
        }
        else throw new InvalidOperationException("Unknown mock role.");
        return Task.FromResult(JsonSerializer.Serialize(response));
    }
}
public sealed class MockBuilderAgent(Workspace workspace, LoopOptions options) : IBuilderAgent
{
    public async Task<BuildResult> BuildAsync(Plan plan, int iteration, string[] feedback, CancellationToken token)
    {
        var content = JsonSerializer.Serialize(new { request = plan.Request, status = iteration <= options.MockFailures ? "incomplete" : "complete", feedback });
        const string path = ".devloop/demo/implementation.json";
        await workspace.WriteAsync(path, content, token);
        return new(true, [new(path, content)], "Mock builder wrote the demonstration artifact.") { TaskCompleted = true, ChangedFiles = [path] };
    }
}
public sealed class TesterAgent(ICommandRunner runner, LoopOptions options) : ITesterAgent
{
    public async Task<TestResult> TestAsync(CancellationToken token)
    {
        var build = await runner.RunAsync(options.DotnetExecutable, ["build", options.Solution, "--no-restore"], token);
        // Run both commands and preserve their complete outputs, even when build fails.
        var test = await runner.RunAsync(options.DotnetExecutable, ["test", options.Solution, "--no-restore"], token);
        return new(build, test);
    }
}
