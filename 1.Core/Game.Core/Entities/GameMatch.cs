namespace Game.Core.Entities;

public class GameMatch
{
    public Guid Id { get; private set; }
    public Guid PlayerOneId { get; private set; }
    public Guid PlayerTwoId { get; private set; }
    public Guid? WinnerPlayerId { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private GameMatch() { }

    public GameMatch(Guid playerOneId, Guid playerTwoId)
    {
        Id = Guid.NewGuid();
        PlayerOneId = playerOneId;
        PlayerTwoId = playerTwoId;
        IsCompleted = false;
    }

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
