using System.Reflection;

namespace Game.Client.Services;

// The build's version, e.g. "1.0.42+21e6a73...": the number comes from the deploy run and the part
// after '+' is the commit it was built from.
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string Number { get; } = "v" + Informational.Split('+')[0];

    public static string Commit { get; } =
        Informational.Split('+') is [_, var sha, ..] ? sha[..Math.Min(7, sha.Length)] : "local build";
}
