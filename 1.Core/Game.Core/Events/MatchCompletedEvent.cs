namespace Game.Core.Events;

// Using a record ensures our event message is immutable
public record MatchCompletedEvent(
    Guid MatchId, 
    Guid WinnerId, 
    Guid LoserId
);
