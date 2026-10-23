using System.Text.Json;

namespace DevLoop;

// All application-owned writes pass through this boundary. Reparse points are rejected.
public sealed class Workspace
{
    public string Root { get; }
    public Workspace(LoopOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Workspace)) throw new ArgumentException("Configure an explicit workspace.");
        Root = Path.GetFullPath(options.Workspace);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        Resolve(".");
    }
    public string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path != Root && !path.StartsWith(Root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Path escapes workspace.");
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((Directory.Exists(current.FullName) || File.Exists(current.FullName)) &&
                (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are not allowed.");
        return path;
    }
    public async Task WriteAsync(string relative, string content, CancellationToken token)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, token);
    }
    public Task JsonAsync(string relative, object value, CancellationToken token) =>
        WriteAsync(relative, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), token);
}
