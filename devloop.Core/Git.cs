using System.Security.Cryptography;
using System.Text;

namespace DevLoop;

// Read-only Git access with fixed arguments. It never stages, commits, resets or fetches.
public sealed class GitClient(Workspace workspace, ArtifactStore artifacts, LoopOptions options, IProcessRunner processes) : IGitClient
{
    public async Task<string> GetTopLevelAsync(CancellationToken token) => (await RunAsync(token, "rev-parse", "--show-toplevel")).Trim();

    public async Task<string> GetHeadAsync(CancellationToken token) => (await RunAsync(token, "rev-parse", "--verify", "HEAD")).Trim();

    public async Task<GitChange[]> GetChangesAsync(CancellationToken token)
    {
        var output = await RunAsync(token, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".", .. Exclusions()]);
        var parts = output.Split('\0');
        var changes = new List<GitChange>();
        for (var index = 0; index < parts.Length; index++)
        {
            var entry = parts[index];
            if (entry.Length < 4) continue;
            var status = entry[..2];
            changes.Add(new(status, entry[3..]));
            if (status.Contains('R') || status.Contains('C')) index++; // Skip the rename/copy source.
        }
        return changes.ToArray();
    }

    public Task<string> GetDiffAsync(string baseline, CancellationToken token) =>
        RunAsync(token, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", baseline, "--", ".", .. Exclusions()]);

    // The artifact directory is DevLoop's own output, not a builder change.
    private string[] Exclusions()
    {
        var relative = Path.GetRelativePath(workspace.Root, artifacts.Root);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? [] : [$":(exclude){relative.Replace('\\', '/')}"];
    }

    private async Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        // fsmonitor is disabled and diffs skip external/textconv drivers, so reading changes cannot run repository-configured programs.
        string[] all = ["-c", "core.fsmonitor=false", "-c", "core.quotepath=off", "--no-optional-locks", .. arguments];
        var environment = ProcessEnvironment.Sanitized(new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        foreach (var name in environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && key != "GIT_TERMINAL_PROMPT").ToList())
            environment.Remove(name);
        var result = await processes.RunAsync(new ProcessSpec(options.GitExecutable, all, workspace.Root, TimeSpan.FromSeconds(60))
            { Environment = environment, MaxOutputChars = 50_000_000 }, token);
        if (!result.Passed)
            throw new GitException($"git {arguments[0]} failed (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")}): {result.StandardError.Trim()}");
        return result.StandardOutput;
    }
}

// Establishes the Git baseline and captures, after each build, exactly what changed and whether it is allowed.
public sealed class WorkspaceChangeInspector(Workspace workspace, IGitClient git, CodexOptions options, SecretScanner scanner)
{
    private const int MaxUntrackedBytes = 256 * 1024;

    public async Task<string> PrepareAsync(CancellationToken token)
    {
        string topLevel;
        try { topLevel = await git.GetTopLevelAsync(token); }
        catch (GitException exception) { throw new WorkspacePolicyException($"Workspace must be a Git repository: {exception.Message}"); }
        if (!SamePath(topLevel, workspace.Root))
            throw new WorkspacePolicyException($"Workspace must be the root of a dedicated Git working directory (repository root is {topLevel}).");
        if (IsInside(AppContext.BaseDirectory, workspace.Root))
            throw new WorkspacePolicyException("The workspace contains the DevLoop orchestrator itself. Use a separate clone as the workspace.");
        string head;
        try { head = await git.GetHeadAsync(token); }
        catch (GitException) { throw new WorkspacePolicyException("Workspace repository has no commits. Commit a baseline first."); }
        if (options.RequireCleanWorkspace && (await git.GetChangesAsync(token)).Length > 0)
            throw new WorkspacePolicyException("Workspace has uncommitted changes. Commit or stash them so builder changes can be attributed.");
        return head;
    }

    public async Task<ChangeSet> CaptureAsync(string baseline, CancellationToken token)
    {
        string head, trackedDiff;
        GitChange[] changes;
        try
        {
            head = await git.GetHeadAsync(token);
            changes = await git.GetChangesAsync(token);
            trackedDiff = await git.GetDiffAsync(baseline, token);
        }
        catch (GitException exception)
        {
            return new([], "", "", [$"Unable to capture workspace changes: {exception.Message}"]);
        }

        var violations = new List<string>();
        if (head != baseline) violations.Add("Git HEAD moved during the build. The builder must not commit, reset or switch branches.");
        var untracked = new StringBuilder();
        foreach (var change in changes)
        {
            string fullPath;
            try { fullPath = workspace.Resolve(change.Path); }
            catch (InvalidOperationException exception) { violations.Add($"{change.Path}: {exception.Message}"); continue; }
            if (IsProtected(change.Path)) violations.Add($"Protected path modified: {change.Path}");
            if (SecretScanner.IsSensitiveFile(change.Path)) violations.Add($"Credential-like file created or modified: {change.Path}");
            if (change.Status == "??" && File.Exists(fullPath)) untracked.Append(DescribeUntracked(change.Path, fullPath));
        }
        var diff = trackedDiff + untracked;
        var added = string.Join('\n', diff.Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)));
        if (scanner.ContainsSecret(added)) violations.Add("Possible credential detected in the changes.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diff)));
        return new(changes.Select(change => change.Path).Distinct().ToArray(), diff, hash, violations.Distinct().ToArray());
    }

    public bool IsProtected(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var name = Path.GetFileName(normalized);
        return options.ProtectedPaths.Select(entry => entry.Replace('\\', '/')).Any(entry => entry.EndsWith('/')
            ? normalized.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, entry, StringComparison.OrdinalIgnoreCase) || string.Equals(name, entry, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeUntracked(string path, string fullPath)
    {
        var header = $"diff --git a/{path} b/{path}\nnew file (untracked)\n";
        var length = new FileInfo(fullPath).Length;
        if (length > MaxUntrackedBytes) return $"{header}[content omitted: {length} bytes]\n";
        var content = File.ReadAllText(fullPath);
        if (content.Contains('\0')) return $"{header}[binary content omitted: {length} bytes]\n";
        var lines = content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return $"{header}--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n{string.Concat(lines.Select(line => $"+{line}\n"))}";
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), Comparison);

    private static bool IsInside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, Comparison);
}
