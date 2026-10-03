// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
namespace NetPI.Providers.Kit;

internal static class Secrets
{
    /// <summary>"env:NAME" or "$NAME" reads an environment variable; anything else is the literal secret.</summary>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        string? env = value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? value[4..]
            : value.StartsWith('$') && value.Length > 1 ? value[1..] : null;
        if (env is null) return value;
        var resolved = Environment.GetEnvironmentVariable(env.Trim());
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }
}