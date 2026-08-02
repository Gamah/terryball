using Sandbox;

namespace Terryball;

/// <summary>
/// Client-local, <see cref="Cookie"/>-backed WORLD settings — the per-user look and feel of the alley (theme
/// colour, sky brightness, voice range), edited at the SETTINGS podium on the spawn concourse
/// (<see cref="WorldSettingsStation"/>). Sibling of
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
	private const string SkyKey = "terryball.world.sky_brightness";
	private const string VoiceRangeKey = "terryball.world.voice_range";

	/// <summary>Bumped by every setter here. Panels fold it into their <c>BuildHash</c> and the appliers
	/// (<see cref="SkyBrightnessApplier"/>, <see cref="VoiceScreen"/>) compare against it so they only do
	/// real work when something actually changed — the same job gambit's <c>SettingsVersion</c> does.
	/// Starts at 0 and an applier's "last applied" starts at -1, so everything applies once on load.</summary>
	public static int Revision { get; private set; }

	/// <summary>The world theme colour as a CSS hex string (<c>#rrggbb</c>), or <c>""</c> for AUTO — the
	/// canonical terryball amber. Everything the UI actually draws with derives from this via
	/// <see cref="WorldTheme"/>; nothing reads the raw hex except the settings UI.</summary>
	public static string ThemeColor
	{
		get => Cookie.Get( ThemeKey, "" );
		set { Cookie.Set( ThemeKey, value ?? "" ); Revision++; }
	}

	/// <summary>Multiplier on the alley's daylight — the scene's Sun (its directional light + sky term) and
	/// the 2D skybox, scaled together by <see cref="SkyBrightnessApplier"/>. 1 is the authored lighting; the
	/// floor is dusk, not black (see <see cref="SkyBrightnessMin"/>). gambit calls the same setting ROOM
	/// LIGHT BRIGHTNESS, but terryball's alley is lit through a sky rather than by a room lamp, so this is
	/// SKY BRIGHTNESS here and it drives the sky.</summary>
	public static float SkyBrightness
	{
		get => ClampSkyBrightness( Cookie.Get( SkyKey, 1f ) );
		set { Cookie.Set( SkyKey, ClampSkyBrightness( value ) ); Revision++; }
	}

	/// <summary>How far (world units) THIS client hears other players' proximity voice. Receive-side and
	/// per-client — the 3D falloff is computed on the receiver — which is why it lives in a cookie and is
	/// never networked. See <see cref="VoiceScreen"/> for where it lands on the Voice components.
	///
	/// <para>gambit splits this in two (SEATED at a chess table, ROAMING everywhere else). Terryball has no
	/// seated equivalent: a bowler is stood in the same open concourse as everyone else, just further down
	/// it, so there is one range and it is the roaming one.</para></summary>
	public static float VoiceRangeRoaming
	{
		get => ClampVoiceRange( Cookie.Get( VoiceRangeKey, DefaultVoiceRange ) );
		set { Cookie.Set( VoiceRangeKey, ClampVoiceRange( value ) ); Revision++; }
	}

	/// <summary>Sky-brightness slider bounds. 1.5 rather than 1 at the top so a player can push PAST the
	/// authored daylight, matching gambit's light-scale window.
	///
	/// <para>The FLOOR is deliberately not 0, where gambit's is. gambit's room keeps its table lights when
	/// the room light goes out; terryball's sun is the only thing lighting the alley, so 0 is a black screen
	/// — and the only way back is to walk to the SETTINGS pedestal you can no longer see. 0.2 is dusk: dim
	/// enough to be worth having, bright enough to walk back.</para></summary>
	public const float SkyBrightnessMin = 0.2f;
	public const float SkyBrightnessMax = 1.5f;

	// Voice-range window, in terryball's world units — a bigger space than gambit's ~800u room, so bigger
	// numbers. The floor is PodiumSeparation (300u, the BALL↔HELP gap: "only whoever is stood at the next
	// podium"); the ceiling is about twice ConcourseDepth (2400u), which covers the whole spawn plaza and
	// the climb up to a settee. The default sits mid-window.
	public const float VoiceRangeMin = 300f;
	public const float VoiceRangeMax = 5000f;
	private const float DefaultVoiceRange = 1500f;

	public static float ClampSkyBrightness( float v )
		=> System.Math.Clamp( v, SkyBrightnessMin, SkyBrightnessMax );

	public static float ClampVoiceRange( float v )
		=> System.Math.Clamp( v, VoiceRangeMin, VoiceRangeMax );

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
