namespace DevLoop;

// Root configuration section (environment variables: DEVLOOP_<Name>).
public sealed class LoopOptions
{
    public string Mode { get; set; } = "Mock";
    public string Workspace { get; set; } = "";
    public string Solution { get; set; } = "devloop.sln";
    public int MaxIterations { get; set; } = 3;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int RunTimeoutSeconds { get; set; } = 3600;
    public string DotnetExecutable { get; set; } = "dotnet";
    public string GitExecutable { get; set; } = "git";
    // Empty means <Workspace>/.devloop/runs. Relative paths resolve against the workspace.
    public string ArtifactDirectory { get; set; } = "";
    public int MockFailures { get; set; } = 1;

    public bool IsCodexMode => string.Equals(Mode, "Codex", StringComparison.OrdinalIgnoreCase);
}

// Section "Codex" (DEVLOOP_Codex__<Name>).
public sealed class CodexOptions
{
    public string Executable { get; set; } = "codex";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 900;
    public bool IgnoreUserConfig { get; set; } = true;
    public bool RequireCleanWorkspace { get; set; } = true;
    public int MaxOutputChars { get; set; } = 2_000_000;
    // Extra variables forwarded to the Codex process itself, e.g. CODEX_API_KEY. Commands Codex runs
    // only receive the core environment (shell_environment_policy.inherit=core).
    public string[] PassEnvironment { get; set; } = [];
    // Root-relative prefixes ending in '/' or file names the builder must not touch.
    public string[] ProtectedPaths { get; set; } = [".git/", ".github/", ".devloop/", ".codex/", ".vscode/", "nuget.config", "global.json"];
}

// Section "Critic" (DEVLOOP_Critic__<Name>). Keep ApiKey in user secrets or the environment.
public sealed class CriticOptions
{
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 4000;
    public int MaxDiffChars { get; set; } = 60_000;
    public int MaxContextChars { get; set; } = 40_000;
    public int MaxFileContextChars { get; set; } = 15_000;
    public int MaxCheckOutputChars { get; set; } = 6_000;
}

public static class OptionsValidator
{
    public static string[] Validate(LoopOptions loop, CodexOptions codex, CriticOptions critic)
    {
        var errors = new List<string>();
        if (!loop.IsCodexMode && !string.Equals(loop.Mode, "Mock", StringComparison.OrdinalIgnoreCase))
            errors.Add($"Mode must be Mock or Codex (was '{loop.Mode}').");
        if (string.IsNullOrWhiteSpace(loop.Workspace)) errors.Add("Workspace is required.");
        else if (!Directory.Exists(loop.Workspace)) errors.Add($"Workspace does not exist: {loop.Workspace}");
        if (loop.MaxIterations is < 1 or > 3) errors.Add("MaxIterations must be between 1 and 3.");
        if (loop.CommandTimeoutSeconds <= 0 || loop.RunTimeoutSeconds <= 0) errors.Add("Timeouts must be positive.");
        if (!loop.IsCodexMode) return errors.ToArray();

        if (codex.TimeoutSeconds <= 0 || critic.TimeoutSeconds <= 0) errors.Add("Codex and Critic timeouts must be positive.");
        var extension = Path.GetExtension(codex.Executable);
        if (string.IsNullOrWhiteSpace(codex.Executable)) errors.Add("Codex:Executable is required.");
        else if (OperatingSystem.IsWindows() && extension is not ("" or ".exe"))
            errors.Add("Codex:Executable must be the native codex.exe; .cmd/.ps1 shims would pass input through a shell.");
        else if (Path.IsPathRooted(codex.Executable) && !File.Exists(codex.Executable))
            errors.Add($"Codex executable not found: {codex.Executable}");
        if (string.IsNullOrWhiteSpace(critic.Model)) errors.Add("Critic:Model is required.");
        if (string.IsNullOrWhiteSpace(critic.ApiKey)) errors.Add("Critic:ApiKey is not configured (use dotnet user-secrets).");
        if (critic.MaxDiffChars <= 0 || critic.MaxContextChars < 0 || critic.MaxOutputTokens <= 0) errors.Add("Critic payload limits must be positive.");
        return errors.ToArray();
    }
}
