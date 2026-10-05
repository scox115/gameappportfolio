namespace Game.Core.Entities;

public class GameMatch
{
    /// <summary>
    /// Fixed opponent id for AI bosses. Bosses have no Player row, and Matches has no
    /// foreign key to Players, so this id is safe to store as PlayerTwoId.
    /// </summary>
    public static readonly Guid AiBossId = new("00000000-0000-0000-0000-00000000b055");

    public Guid Id { get; private set; }
    public Guid PlayerOneId { get; private set; }
    public Guid PlayerTwoId { get; private set; }
    public Guid? WinnerPlayerId { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private GameMatch() { }

    public GameMatch(Guid playerOneId, Guid playerTwoId)
    {
        if (playerOneId == Guid.Empty || playerTwoId == Guid.Empty)
            throw new ArgumentException("Match participants must have ids.");
        if (playerOneId == playerTwoId)
            throw new ArgumentException("A player cannot be matched against themselves.");

        Id = Guid.NewGuid();
        PlayerOneId = playerOneId;
        PlayerTwoId = playerTwoId;
        IsCompleted = false;
    }

    public bool IsPve => PlayerTwoId == AiBossId;

    public static GameMatch CreatePve(Guid playerId) => new(playerId, AiBossId);

    public void CompleteMatch(Guid winnerId)
    {
        if (IsCompleted) throw new InvalidOperationException("Match is already finalized.");
        if (winnerId != PlayerOneId && winnerId != PlayerTwoId)
        {
            throw new ArgumentException("The winner must be one of the match participants.");
        }

        WinnerPlayerId = winnerId;
        IsCompleted = true;
    }
}
