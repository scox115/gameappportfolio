using Game.Client.Models;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// One hero's panel in a PvP duel.
public partial class DuelPlayerPanel
{
    [Parameter, EditorRequired] public PvpPlayerDto Player { get; set; } = default!;
    [Parameter] public string Color { get; set; } = "";
    [Parameter] public string Label { get; set; } = "";
    [Parameter] public bool AgainstBot { get; set; }
    // The signed-in hero, so the opponent's panel can offer a report and the bot's can hide its rating.
    [Parameter] public Guid YourPlayerId { get; set; }
}
