using System.Net;
using Game.Core.Interfaces;

namespace Game.Api.Recovery;

/// <summary>The words of the emails account recovery sends.</summary>
public static class RecoveryEmails
{
    public static EmailMessage ConfirmEmail(string to, string hero, Uri link, TimeSpan lifetime) => Build(
        to,
        "Confirm your recovery email for Kings of the Card Arena",
        hero,
        $"Confirm this address so you can reset {hero}'s password if you ever forget it.",
        "Confirm my email",
        link,
        $"The link works for {Describe(lifetime)}. If you didn't ask for this, ignore this email and nothing changes.");

    public static EmailMessage ResetPassword(string to, string hero, Uri link, TimeSpan lifetime) => Build(
        to,
        "Reset your Kings of the Card Arena password",
        hero,
        $"Someone asked to reset the password of your hero {hero}. Choose a new one here:",
        "Choose a new password",
        link,
        $"The link works once, for {Describe(lifetime)}. If you didn't ask for this, ignore this email: your password stays the same.");

    private static EmailMessage Build(string to, string subject, string hero, string intro, string action, Uri link, string footer)
    {
        var plain = $"Hello {hero},\n\n{intro}\n\n{link}\n\n{footer}\n\nKings of the Card Arena";
        static string e(string text) => WebUtility.HtmlEncode(text);
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:520px;color:#0f172a">
              <h1 style="font-size:20px">⚔️ Kings of the Card Arena</h1>
              <p>Hello {e(hero)},</p>
              <p>{e(intro)}</p>
              <p><a href="{e(link.ToString())}" style="display:inline-block;background:#4f46e5;color:#ffffff;padding:10px 18px;border-radius:8px;text-decoration:none;font-weight:bold">{e(action)}</a></p>
              <p style="color:#475569;font-size:13px">Or paste this into your browser: {e(link.ToString())}</p>
              <p style="color:#475569;font-size:13px">{e(footer)}</p>
            </div>
            """;
        return new EmailMessage(to, subject, plain, html);
    }

    private static string Describe(TimeSpan lifetime) =>
        lifetime.TotalHours >= 1
            ? $"{lifetime.TotalHours:0.#} hour{(lifetime.TotalHours == 1 ? "" : "s")}"
            : $"{lifetime.TotalMinutes:0} minutes";
}
