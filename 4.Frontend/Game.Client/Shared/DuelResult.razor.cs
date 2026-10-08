using Game.Client.Models;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The result panel of a finished PvP duel. PvpArena handles the follow-up actions.
public partial class DuelResult
{
    [Parameter, EditorRequired] public PvpBattleDto Battle { get; set; } = default!;
    [Parameter] public PvpRewardDto? Reward { get; set; }

    [Parameter] public EventCallback OnReturnToTown { get; set; }
    [Parameter] public EventCallback OnFindAnother { get; set; }
    [Parameter] public EventCallback OnSparAgain { get; set; }

    private bool Won => Battle.YouWon == true;

    private string ResultText(bool won) => Battle.EndReason switch
    {
        "Timeout" => won ? $"{Battle.Opponent.Username} ran out of time." : "You ran out of time.",
        "Forfeit" => won ? $"{Battle.Opponent.Username} forfeited." : "You forfeited the battle.",
        _ => won ? $"You knocked out {Battle.Opponent.Username}!" : $"{Battle.Opponent.Username} knocked you out."
    };
}
