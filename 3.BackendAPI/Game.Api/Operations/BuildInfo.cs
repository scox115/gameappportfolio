using System.Reflection;

namespace Game.Api.Operations;

/// <summary>What is running: the build's version and commit, and the Container Apps revision serving it.</summary>
public sealed class BuildInfo
{
    public BuildInfo(IConfiguration configuration, TimeProvider clock)
    {
        // "1.0.31+21e6a73...": the deploy run number, then the commit it was built from.
        var informational = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        FullVersion = informational;
        Version = informational.Split('+')[0];
        Commit = informational.Split('+') is [_, var sha, ..] ? sha[..Math.Min(12, sha.Length)] : null;

        // Azure Container Apps sets these in every container. Locally they're absent.
        Revision = NullIfEmpty(configuration["CONTAINER_APP_REVISION"]);
        var appName = NullIfEmpty(configuration["CONTAINER_APP_NAME"]);
        var dnsSuffix = NullIfEmpty(configuration["CONTAINER_APP_ENV_DNS_SUFFIX"]);
        PublicHost = appName is not null && dnsSuffix is not null ? $"{appName}.{dnsSuffix}" : null;

        StartedAt = clock.GetUtcNow();
    }

    public string FullVersion { get; }
    public string Version { get; }
    public string? Commit { get; }
    public string? Revision { get; }

    /// <summary>
    /// The app's own address, which only receives traffic once this revision is live. A revision's
    /// own address (used by the blue-green smoke test) and health probes use other host names.
    /// </summary>
    public string? PublicHost { get; }

    /// <summary>When this replica started. It scales to zero when idle, so this is "awake since".</summary>
    public DateTimeOffset StartedAt { get; }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
