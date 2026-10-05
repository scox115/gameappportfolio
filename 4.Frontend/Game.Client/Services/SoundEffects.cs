using Microsoft.JSInterop;

namespace Game.Client.Services;

public enum Sound
{
    Fireball,
    Claw,
    Shield,
    Hit,
    Block,
    Miss,
    Drain,
    Victory,
    Defeat,
    MatchFound,
    YourTurn,
    Purchase
}

/// <summary>
/// Plays battle sounds (synthesized in wwwroot/js/sound.js) and remembers whether the player
/// turned them off, in this browser's local storage.
/// </summary>
public class SoundEffects(IJSRuntime js)
{
    private bool? _enabled;

    public event Action? OnChanged;

    public bool Enabled => _enabled ?? true;

    public async Task LoadAsync()
    {
        if (_enabled is not null) return;
        _enabled = await js.InvokeAsync<bool>("gameSound.isEnabled");
        OnChanged?.Invoke();
    }

    public async Task ToggleAsync()
    {
        _enabled = !Enabled;
        await js.InvokeVoidAsync("gameSound.setEnabled", _enabled);
        OnChanged?.Invoke();
    }

    public async Task PlayAsync(Sound sound)
    {
        if (!Enabled) return;
        var name = char.ToLowerInvariant(sound.ToString()[0]) + sound.ToString()[1..];
        await js.InvokeVoidAsync("gameSound.play", name);
    }

    /// <summary>The sound for a card that landed.</summary>
    public static Sound ForCard(string cardName) => cardName switch
    {
        "Fireball" => Sound.Fireball,
        "Dragon Claw" or "DragonClaw" => Sound.Claw,
        _ => Sound.Shield
    };
}
