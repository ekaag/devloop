using System.Text.Json;

namespace DevLoop;

// Owns the per-run artifact directory. One run at a time per store.
public sealed class ArtifactStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Root { get; }
    public string? RunDirectory { get; private set; }

    public ArtifactStore(LoopOptions options, Workspace workspace) =>
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.ArtifactDirectory)
            ? Path.Combine(workspace.Root, ".devloop", "runs")
            : Path.Combine(workspace.Root, options.ArtifactDirectory));

    public string BeginRun()
    {
        RunDirectory = Path.Combine(Root, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RunDirectory);
        return RunDirectory;
    }

    public string Resolve(string relative)
    {
        var run = RunDirectory ?? throw new InvalidOperationException("No run has been started.");
        var path = Path.GetFullPath(Path.Combine(run, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(run + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Artifact path escapes the run directory.");
        return path;
    }

    public async Task<string> WriteTextAsync(string relative, string content, CancellationToken token)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, token);
        return path;
    }

    public Task<string> WriteJsonAsync(string relative, object value, CancellationToken token) =>
        WriteTextAsync(relative, JsonSerializer.Serialize(value, value.GetType(), JsonOptions), token);
}
