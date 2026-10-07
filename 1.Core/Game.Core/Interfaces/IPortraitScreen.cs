using Game.Core.Moderation;

namespace Game.Core.Interfaces;

/// <summary>
/// Checks an uploaded portrait for harmful content before anyone else can see it.
/// See docs/adr/0029-portrait-screening.md.
/// </summary>
public interface IPortraitScreen
{
    Task<PortraitScreening> ScreenAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default);
}
