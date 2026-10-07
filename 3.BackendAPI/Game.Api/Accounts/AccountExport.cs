using Game.Core.Admin;
using Game.Core.Battles;
using Game.Core.Bounties;
using Game.Core.Entities;
using Game.Core.Events;
using Game.Core.History;
using Game.Core.Moderation;

namespace Game.Api.Accounts;

/// <summary>
/// The file a player downloads with "Download my data": everything the game stores about them, in
/// plain JSON. Password hashes, token hashes and security stamps are left out, because they only
/// matter to the server and can't be read anyway.
/// </summary>
public record AccountExport(
    DateTimeOffset ExportedAt,
    AccountExport.SignInAccount Account,
    AccountExport.HeroProfile Profile,
    List<AccountExport.SignInSession> SignInSessions,
    List<AccountExport.Match> MatchHistory,
    List<AccountExport.BossFight> BossFights,
    List<AccountExport.Duel> Duels,
    List<AccountExport.AdminDecision> AdminDecisions,
    List<AccountExport.ReportFiled> ReportsFiled)
{
    /// <param name="RecoveryEmail">The confirmed address reset links go to, if the player added one.</param>
    /// <param name="SuspendedUntil">Set while an admin has suspended the account; the year 9999 means until reinstated.</param>
    /// <param name="PreviousUsername">The name the hero had before an admin renamed it (in capitals, as stored); it still signs in.</param>
    public record SignInAccount(
        Guid Id, string Username, DateTimeOffset? LockedOutUntil, int FailedSignInAttempts,
        DateTimeOffset? SuspendedUntil, string? SuspensionReason, string? RecoveryEmail, string? PreviousUsername = null,
        bool TwoFactorEnabled = false);

    /// <summary>Something an admin did to the account. Which admin did it is left out.</summary>
    public record AdminDecision(DateTime At, AdminAction Action, string Reason, string? Detail);

    /// <summary>A report this player made about another hero, and what came of it (null while it waits).</summary>
    public record ReportFiled(DateTime At, Guid HeroId, ReportReason Reason, string? Note, ReportOutcome? Outcome);

    public record SignInSession(DateTime StartedAt, DateTime ExpiresAt, DateTime? EndedAt);

    public record HeroProfile(
        string Username,
        HeroClass Class,
        int Level,
        int ExperiencePoints,
        int Gold,
        int Rating,
        int PvpWins,
        int PvpLosses,
        int WinStreak,
        int BattleElixirs,
        int DuelElixirs,
        string? PortraitUrl,
        List<CardUpgrade> CardUpgrades,
        List<ClassRecord> ClassRecords,
        List<string> Titles,
        string? EquippedTitle,
        List<string> Cosmetics,
        string? EquippedFrame,
        string? EquippedCardSkin,
        List<BountyProgress> Bounties)
    {
        public static HeroProfile From(Player p) => new(
            p.Username, p.Class, p.Level, p.ExperiencePoints, p.Gold, p.Rating, p.PvpWins, p.PvpLosses, p.WinStreak,
            p.Elixirs, p.DuelElixirs, p.AvatarUrl,
            p.CardUpgrades.ToList(),
            p.ClassRecords.ToList(),
            p.Titles.Select(t => t.Title.ToString()).ToList(),
            p.EquippedTitle?.ToString(),
            p.Cosmetics.Select(c => c.Cosmetic.ToString()).ToList(),
            p.EquippedFrame?.ToString(),
            p.EquippedCardSkin?.ToString(),
            p.Bounties.ToList());
    }

    public record Match(
        DateTime PlayedAt, MatchKind Kind, bool Won, HeroClass Class, string Opponent, HeroClass? OpponentClass,
        BossDifficulty? Difficulty, PvpEndReason? EndReason, int Turns, int GoldEarned, int ExperienceEarned,
        int? RatingChange, int WagerResult)
    {
        public static Match From(MatchHistoryEntry e) => new(
            e.PlayedAt, e.Kind, e.Won, e.Class, e.OpponentName, e.OpponentClass, e.Difficulty, e.EndReason,
            e.Turns, e.GoldEarned, e.ExperienceEarned, e.RatingChange, e.WagerResult);
    }

    public record BossFight(
        Guid Id, BossDifficulty Difficulty, HeroClass Class, BattleStatus Status, int Turns, DateTime StartedAt, DateTime? CompletedAt);

    /// <summary>A duel from the player's side; the opponent is in <see cref="MatchHistory"/> by name.</summary>
    public record Duel(
        Guid Id, HeroClass Class, HeroClass OpponentClass, PvpBattleStatus Status, bool? Won, PvpEndReason? EndReason,
        int Wager, bool Practice, int Turns, DateTime StartedAt, DateTime? CompletedAt)
    {
        public static Duel From(PvpBattle b, Guid playerId) => new(
            b.Id,
            b.PlayerOneId == playerId ? b.PlayerOneClass : b.PlayerTwoClass,
            b.PlayerOneId == playerId ? b.PlayerTwoClass : b.PlayerOneClass,
            b.Status,
            b.WinnerId is { } winner ? winner == playerId : null,
            b.EndReason, b.Wager, b.Practice, b.Turn, b.StartedAt, b.CompletedAt);
    }
}
