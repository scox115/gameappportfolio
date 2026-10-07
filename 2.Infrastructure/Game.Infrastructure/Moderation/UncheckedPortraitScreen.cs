using Game.Core.Interfaces;
using Game.Core.Moderation;

namespace Game.Infrastructure.Moderation;

/// <summary>
/// For local runs and copies of the game without Content Safety: every portrait is allowed, and
/// players' reports are the only check, as before screening existed.
/// </summary>
public class UncheckedPortraitScreen : IPortraitScreen
{
    public Task<PortraitScreening> ScreenAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
        Task.FromResult(PortraitScreening.Allowed);
}
