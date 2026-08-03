using Sandbox;
using Sandbox.UI;

namespace Terryball;

/// <summary>
/// Discord invite copy helper. There is no documented API to open a URL or the Steam overlay
/// from game code, but <c>Clipboard.SetText</c> works, so every outbound link in this game is
/// click-to-copy. The clickable link lives on the MORE podium's screen (<c>MorePanel</c>);
/// <see cref="SinceCopied"/> drives the brief "copied" confirmation there.
/// </summary>
/// <remarks>
/// Same community, same invite, same shape as rotaliate-client's and gambit's
/// <c>Rotaliate.World.DiscordButton</c> / <c>Gambit.World.DiscordButton</c> — the constant
/// used to be inline in HowToPlay.razor, which is how it came to be the one link with no
/// obvious home when the invite moved off the help screen.
/// </remarks>
public static class DiscordButton
{
	public const string InviteCode = "GG8HWUfFpD";
	public const string InviteUrl = "https://discord.gg/" + InviteCode;

	/// <summary>Time since the invite was last copied — MorePanel shows brief feedback.</summary>
	public static RealTimeSince SinceCopied { get; private set; } = 999f;

	public static void Copy()
	{
		Clipboard.SetText( InviteUrl );
		SinceCopied = 0f;
	}
}
