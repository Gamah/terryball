using Sandbox;

namespace Terryball;

/// <summary>
/// Runtime palette for terryball's in-world panels, derived from the one world-theme hue the player picks
/// at the SETTINGS podium (<see cref="WorldPrefs.ThemeColor"/>). The same idea as rotaliate-client's and
/// gambit's <c>WallTheme</c>: everything keys off a single <see cref="Accent"/>, so changing the theme
/// retints the whole world UI at once, and AUTO falls back to the canonical amber.
///
/// <para>The near-black fill gets a few percent of the accent lifted into it (see <see cref="Bg"/>), which
/// is what makes a theme visible on the panel body and not just its trim.</para>
///
/// <para>WHY A C# STATIC AND NOT A STYLESHEET: these panels bind the values as inline <c>style=</c>
/// attributes, which re-render when the value changes. A compiled <c>&lt;style&gt;</c> block (or an .scss
/// variable) is baked at build time and cannot take a runtime colour — that is the whole reason the theme
/// lives here. Only static tokens (fonts, radii, sizes) belong in markup.</para>
///
/// <para>WHAT THIS DOES NOT OWN: the per-station identity colours. Those say "which podium is this" —
/// they are wayfinding, not decoration, so
/// they stay hardcoded at their call sites and are passed in (see <see cref="StationSign.Accent"/>). This
/// type owns the CHROME every panel shares: the near-black fill, the borders and dividers, the text ramp,
/// and the fallback accent for panels that don't have an identity of their own.</para>
/// </summary>
public static class WorldTheme
{
	/// <summary>The canonical terryball amber — the accent on AUTO.</summary>
	public static readonly Color DefaultAccent = Color.Parse( "#f1c40f" ) ?? Color.White;

	/// <summary>The hue everything derives from: the player's world-theme colour, or the default amber on
	/// AUTO / an unparseable cookie.</summary>
	public static Color Accent
	{
		get
		{
			var hex = WorldPrefs.ThemeColor;
			return string.IsNullOrEmpty( hex ) ? DefaultAccent : ( Color.Parse( hex ) ?? DefaultAccent );
		}
	}

	// The near-black panel fill is terryball's, not the accent's: the alley's panels sit against bright
	// lane wood and coloured shadows, so a fill that took the accent's hue (as rotaliate's does) reads
	// muddy against them. The accent gets a faint lift into the fill instead, which reads as "themed" at
	// a glance without moving the fill off the house colour.
	private static readonly Color BaseFill = new( 8f / 255f, 10f / 255f, 16f / 255f );

	/// <summary>Panel fill — the house near-black, lifted a few percent toward the accent.</summary>
	public static string Bg => Rgba( Mix( BaseFill, Accent, 0.06f ), 0.92f );

	/// <summary>Panel fill for a modal / focused board — the same hue, more opaque.</summary>
	public static string BgSolid => Rgba( Mix( BaseFill, Accent, 0.06f ), 0.97f );

	/// <summary>Accent border around a panel.</summary>
	public static string Border => Rgba( Accent, 0.5f );

	/// <summary>Hairline dividers between sections.</summary>
	public static string Divider => Rgba( Accent, 0.22f );

	/// <summary>The accent itself — headline rows, highlighted keys, active ticks.</summary>
	public static string AccentCss => Rgb( Accent );

	/// <summary>Accent wash behind an active toggle / selected cell.</summary>
	public static string AccentBg => Rgba( Accent, 0.2f );

	// The text ramp stays NEUTRAL white-alpha rather than accent-tinted. Terryball prints live numbers
	// (scores, pin counts) on these panels and a tinted white costs legibility at a glance for nothing.
	/// <summary>Primary text / titles.</summary>
	public static string Text => "rgba(255,255,255,0.92)";
	/// <summary>Body lines.</summary>
	public static string TextDim => "rgba(255,255,255,0.62)";
	/// <summary>Hints and muted labels.</summary>
	public static string TextFaint => "rgba(255,255,255,0.38)";

	/// <summary>Unfilled cell / button fill.</summary>
	public static string Cell => Rgba( Mix( BaseFill, Accent, 0.1f ), 0.85f );

	/// <summary>The accent as a plain <c>#rrggbb</c> — for anywhere a hex is wanted rather than a
	/// <c>rgb()</c> string.</summary>
	public static string AccentHex => $"#{To255( Accent.r ):x2}{To255( Accent.g ):x2}{To255( Accent.b ):x2}";

	/// <summary>Changes whenever the theme changes — panels fold this into their <c>BuildHash</c> so a
	/// theme switch re-renders them immediately instead of on their next unrelated state change.</summary>
	public static int Hash => WorldPrefs.ThemeColor?.GetHashCode() ?? 0;

	// Component-wise helpers (no reliance on Color operators), matching the sibling repos' WallTheme.
	private static Color Mix( Color a, Color b, float t ) => new(
		a.r + ( b.r - a.r ) * t,
		a.g + ( b.g - a.g ) * t,
		a.b + ( b.b - a.b ) * t );

	private static string Rgb( Color c ) => $"rgb({To255( c.r )},{To255( c.g )},{To255( c.b )})";
	private static string Rgba( Color c, float a ) => $"rgba({To255( c.r )},{To255( c.g )},{To255( c.b )},{a})";
	private static int To255( float v ) => (int)System.MathF.Round( System.Math.Clamp( v, 0f, 1f ) * 255f );
}
