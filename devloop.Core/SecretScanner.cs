using System.Text.RegularExpressions;

namespace DevLoop;

// Detects and redacts credentials before content is stored for review or sent to a cloud model.
public sealed class SecretScanner
{
    private const RegexOptions Flags = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // High-confidence formats; finding one in builder changes is a policy violation.
    private static readonly Regex[] Definite =
    [
        new(@"(?<![A-Za-z0-9])sk-(?:proj-|svcacct-|admin-)?[A-Za-z0-9_\-]{32,}", Flags),
        new(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_\-]{32,}", Flags),
        new(@"\bAKIA[0-9A-Z]{16}\b", Flags),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", Flags),
        new(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", Flags),
        new(@"\bgithub_pat_[A-Za-z0-9_]{40,}\b", Flags),
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", Flags),
        new(@"(?i)\b(?:AccountKey|SharedAccessKey)\s*=\s*[A-Za-z0-9+/=]{30,}", Flags)
    ];

    // Broader patterns that are only redacted, because they also match ordinary code and tests.
    private static readonly Regex[] Probable =
    [
        new(@"(?i)\b(?:password|passwd|pwd|client_secret|api_?key|access_?token)\b\s*[=:]\s*[""']?[^\s;""',]{8,}", Flags),
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", Flags)
    ];

    private static readonly string[] SensitiveExtensions = [".pfx", ".p12", ".pem", ".key", ".snk", ".publishsettings", ".kdbx", ".jks"];
    private static readonly string[] SensitiveNames = ["secrets.json", "id_rsa", "id_ed25519", "id_ecdsa", ".npmrc", ".pypirc", "auth.json"];

    private readonly string[] known;

    public SecretScanner(params string?[] knownSecrets) =>
        known = knownSecrets.Where(secret => secret is { Length: >= 8 }).Select(secret => secret!).ToArray();

    public bool ContainsSecret(string text) =>
        known.Any(secret => text.Contains(secret, StringComparison.Ordinal)) || Definite.Any(pattern => pattern.IsMatch(text));

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var secret in known) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        foreach (var pattern in Definite.Concat(Probable)) text = pattern.Replace(text, "[REDACTED]");
        return text;
    }

    public static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return SensitiveExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
               SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               name.StartsWith(".env", StringComparison.OrdinalIgnoreCase);
    }
}
