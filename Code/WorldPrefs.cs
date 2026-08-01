using Sandbox;

namespace Terryball;

/// <summary>
/// Client-local, <see cref="Cookie"/>-backed WORLD settings — the per-user look of the alley, edited at the
/// WORLD podium on the spawn concourse (<see cref="WorldSettingsStation"/>). Sibling of
/// <see cref="VoicePrefs"/> and the same shape: every getter reads its cookie and every setter writes it
/// straight back, so a choice persists across sessions the moment it's made.
///
/// This is deliberately NOT networked. Rotaliate and gambit split their settings boards into a LOCAL one
/// (what only you see) and a HOST one (what everybody gets); terryball has only the local half today, so
/// everything here is "my client's look" and nothing needs an admin check or a snapshot. If a host-wide
/// setting is ever wanted, it goes on a networked component, not in this file — keep the cookie side pure.
/// </summary>
public static class WorldPrefs
{
	private const string ThemeKey = "terryball.world.theme";

	/// <summary>The world theme colour as a CSS hex string (<c>#rrggbb</c>), or <c>""</c> for AUTO — the
	/// canonical terryball amber. Everything the UI actually draws with derives from this via
	/// <see cref="WorldTheme"/>; nothing reads the raw hex except the settings UI.</summary>
	public static string ThemeColor
	{
		get => Cookie.Get( ThemeKey, "" );
		set => Cookie.Set( ThemeKey, value ?? "" );
	}

	/// <summary>True while the theme is on AUTO (no explicit choice) — the settings UI highlights the AUTO
	/// swatch from this rather than string-comparing the hex in two places.</summary>
	public static bool ThemeIsAuto => string.IsNullOrEmpty( ThemeColor );

	/// <summary>The named swatches offered on the settings board, in display order. The label is what the
	/// board prints; the hex is what lands in the cookie. AUTO is offered separately (empty hex), so no
	/// entry here may use an empty string.</summary>
	public static readonly (string Label, string Hex)[] Swatches = new[]
	{
		("AMBER",  "#f1c40f"),
		("LANE",   "#5aa0e6"),
		("BALL",   "#b07de6"),
		("PIN",    "#2f9450"),
		("CRIMSON","#e05a5a"),
		("CYAN",   "#5ac8dc"),
	};
}
