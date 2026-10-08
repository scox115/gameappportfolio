using Game.Client.Models;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The PvP lobby's markup. PvpArena owns the hub connection and the lobby state; this shows it and reports the clicks.
public partial class DuelLobby
{
    private static readonly int[] Stakes = [0, 50, 100, 250, 500];

    [Parameter] public string? ErrorMsg { get; set; }
    [Parameter] public ChallengeSentDto? Challenge { get; set; } // sent to a friend, waiting for their answer
    [Parameter] public int ChallengeSecondsLeft { get; set; }
    [Parameter] public bool Searching { get; set; }
    [Parameter] public bool Picking { get; set; }
    [Parameter] public int Stake { get; set; }
    [Parameter] public int Gold { get; set; }
    [Parameter] public bool IsGuest { get; set; }

    [Parameter] public EventCallback<int> OnSearch { get; set; }
    [Parameter] public EventCallback OnDuelBot { get; set; }
    [Parameter] public EventCallback OnCancelSearch { get; set; }
    [Parameter] public EventCallback OnWithdrawChallenge { get; set; }
    [Parameter] public EventCallback OnLeaveLobby { get; set; }
}
