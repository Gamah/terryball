using Sandbox;
using Sandbox.UI;

namespace Terryball;

/// <summary>
/// "Our other games" link copy helper — the sibling of <see cref="DiscordButton"/>, and for
/// the same reason: no API to open a URL from game code, but <c>Clipboard.SetText</c> works,
/// so the link is click-to-copy. Lives on the MORE podium's screen (<c>MorePanel</c>).
/// </summary>
/// <remarks>
/// A separate static rather than a second pair of members on <see cref="DiscordButton"/>: the
/// two have independent "just copied" clocks, and a shared one would flash the wrong
/// confirmation for whichever button was not pressed.
/// </remarks>
public static class GamesButton
{
	/// <summary>The publisher page, filtered to games. Shown verbatim as well as copied — a
	/// player who cannot open a link has to be able to read the whole thing off the screen, so
	/// this must never become a display string that differs from what lands on the clipboard.</summary>
	public const string GamesUrl = "https://sbox.game/gamah/~packages?type=game";

	/// <summary>Time since the link was last copied — MorePanel shows brief feedback.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( GamesUrl );
		SinceCopied = 0f;
	}
}
