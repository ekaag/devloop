using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DevLoop;

public sealed record ProcessSpec(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout)
{
    // The complete child environment. Null means a sanitized copy of the current environment.
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    // Written to stdin, so untrusted text never travels on a command line.
    public string? StandardInput { get; init; }
    // Each stream keeps its most recent characters up to this limit.
    public int MaxOutputChars { get; init; } = 1_000_000;
}

// Runs one executable without a shell, with a timeout, bounded output and an explicit environment.
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<CommandResult> RunAsync(ProcessSpec spec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (spec.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(spec), "Timeout must be positive.");
        var startInfo = new ProcessStartInfo(spec.Executable)
        {
            WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = spec.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (spec.StandardInput is not null) startInfo.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (name, value) in spec.Environment ?? ProcessEnvironment.Sanitized()) startInfo.Environment[name] = value;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, spec.MaxOutputChars);
        var stderr = ReadBoundedAsync(process.StandardError, spec.MaxOutputChars);
        var input = WriteInputAsync(process, spec.StandardInput);
        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var timedOut = false;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr, input);
            token.ThrowIfCancellationRequested();
            timedOut = true;
        }
        await input;
        return new(string.Join(" ", spec.Arguments), timedOut ? -1 : process.ExitCode, await stdout, await stderr, timedOut);
    }

    private static async Task WriteInputAsync(Process process, string? text)
    {
        if (text is null) return;
        try
        {
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();
        }
        catch (IOException) { } // The child exited without reading all of its input.
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int max)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        long dropped = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > max + 65_536) { dropped += text.Length - max; text.Remove(0, text.Length - max); }
        }
        if (text.Length > max) { dropped += text.Length - max; text.Remove(0, text.Length - max); }
        return dropped == 0 ? text.ToString() : $"[{dropped} earlier characters truncated]\n{text}";
    }
}

public static class ProcessEnvironment
{
    private static readonly string[] SensitivePrefixes =
        ["DEVLOOP_", "OPENAI_", "CODEX_", "ANTHROPIC_", "AZURE_", "AWS_", "GOOGLE_", "GITHUB_", "GH_", "NUGET_", "CONNECTIONSTRINGS"];
    private static readonly Regex SensitiveName = new("KEY|SECRET|TOKEN|PASSWORD|PASSWD|CREDENTIAL|CONNECTIONSTRING|CONNSTR|_SAS|CERT|_PAT$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSensitive(string name) =>
        SensitivePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) || SensitiveName.IsMatch(name);

    // Current environment minus anything that looks like a credential or DevLoop configuration.
    public static Dictionary<string, string> Sanitized(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var environment = Current().Where(pair => !IsSensitive(pair.Key)).ToDictionary(Comparer);
        foreach (var (name, value) in overrides ?? new Dictionary<string, string>()) environment[name] = value;
        return environment;
    }

    // Only the named variables, for processes that must not inherit the operator's environment.
    public static Dictionary<string, string> Allowlisted(IEnumerable<string> names, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var allowed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var environment = Current().Where(pair => allowed.Contains(pair.Key)).ToDictionary(Comparer);
        foreach (var (name, value) in overrides ?? new Dictionary<string, string>()) environment[name] = value;
        return environment;
    }

    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static IEnumerable<KeyValuePair<string, string>> Current() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string>((string)entry.Key, entry.Value as string ?? ""));
}

public static class Text
{
    public static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : $"{text[..Math.Max(0, max)]}\n[... {text.Length - max} characters truncated]";
    }

    public static string Tail(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : $"[... {text.Length - max} characters truncated]\n{text[^Math.Max(0, max)..]}";
    }
}
