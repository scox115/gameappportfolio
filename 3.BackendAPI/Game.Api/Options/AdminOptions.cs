namespace Game.Api.Options;

// Bound from the "Admin" configuration section; see AdminRoleSync.
public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// The usernames that are admins, separated by commas (e.g. "Scottie,Morgan"). In Azure this comes
    /// from the ADMIN_USERNAMES repository variable. Taking a name out removes the role at its next sign-in.
    /// </summary>
    public string Usernames { get; set; } = string.Empty;

    public bool IsAdmin(string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        Usernames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Contains(username.Trim(), StringComparer.OrdinalIgnoreCase);
}
