using Sandbox;
using System;
using System.Text.Json.Serialization;

namespace Terryball;

/// <summary>The shape/skin family a player can pick for their bowling ball in the designer.</summary>
public enum BallPattern
{
	/// <summary>Two hemispheres split at the equator (the classic pokeball look).</summary>
	HalfHalf = 0,
	/// <summary>Accent-coloured dots scattered over a primary field.</summary>
	Polkadot = 1,
	/// <summary>A blocky checker of primary/accent cells with dark grout lines — reads as cubes.</summary>
	Cubes = 2,
	/// <summary>A swirly noise nebula between the two hues, flecked with white stars.</summary>
	Galaxy = 3,
}

/// <summary>
/// A player's chosen ball look: a <see cref="BallPattern"/> plus two hues (primary + accent, 0–360°).
/// Saved client-local to a JSON in <see cref="FileSystem.Data"/> so it persists across sessions,
/// mirrored onto the player's <see cref="TerryAvatar"/>, and submitted to the lane's
/// <see cref="GameDirector"/>, whose synced active design skins the ball for everyone. <see cref="BuildColorMap"/> turns it into the equirectangular colour
/// map the glass shell renders AND the ball's projected shadow samples — one texture drives both.
/// </summary>
public struct BallDesign : IEquatable<BallDesign>
{
	public BallPattern Pattern { get; set; }
	/// <summary>Primary hue in degrees (0–360). Full saturation/value; the base field colour.</summary>
	public float PrimaryHue { get; set; }
	/// <summary>Accent hue in degrees (0–360). The dots / second hemisphere / star-nebula colour.</summary>
	public float AccentHue { get; set; }

	// The default look — a red/blue half-and-half (the pokeball). Exposed as
	// consts so they can seed the [Sync] fields on TerryAvatar (whose initializers must be constant).
	public const BallPattern DefaultPattern = BallPattern.HalfHalf;
	public const float DefaultPrimaryHue = 0f;
	public const float DefaultAccentHue = 220f;

	public static BallDesign Default => new() { Pattern = DefaultPattern, PrimaryHue = DefaultPrimaryHue, AccentHue = DefaultAccentHue };

	[JsonIgnore] public readonly Color Primary => new ColorHsv( PrimaryHue, 0.85f, 1.0f ).ToColor();
	[JsonIgnore] public readonly Color Accent => new ColorHsv( AccentHue, 0.85f, 1.0f ).ToColor();

	// --- Persistence (client-local, per-user) ------------------------------------------------
	private const string SavePath = "balldesign.json";

	/// <summary>Read this machine's saved design, or <see cref="Default"/> if none/garbage on disk.</summary>
	public static BallDesign Load()
	{
		try
		{
			if ( FileSystem.Data.FileExists( SavePath ) )
				return FileSystem.Data.ReadJson( SavePath, Default );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[terryball] ball design load failed: {e.Message}" );
		}
		return Default;
	}

	/// <summary>Write this design to the local player's data json.</summary>
	public readonly void Save()
	{
		try { FileSystem.Data.WriteJson( SavePath, this ); }
		catch ( Exception e ) { Log.Warning( $"[terryball] ball design save failed: {e.Message}" ); }
	}

	public readonly bool Equals( BallDesign o )
		=> Pattern == o.Pattern && PrimaryHue.AlmostEqual( o.PrimaryHue ) && AccentHue.AlmostEqual( o.AccentHue );
	public override readonly bool Equals( object o ) => o is BallDesign d && Equals( d );
	public override readonly int GetHashCode() => System.HashCode.Combine( Pattern, PrimaryHue, AccentHue );

	// --- Texture generation ------------------------------------------------------------------
	// Equirectangular: x = longitude (0..1 wraps), y = latitude (0 = north pole ... 1 = south pole).
	// The dev sphere the ball uses is UV-mapped this way, so patterns painted here land the right way up.
	private const int TexW = 512;
	private const int TexH = 256;

	/// <summary>Build the equirectangular colour map for this design. Fed to the glass shell's
	/// <c>g_tColor</c> and to the <see cref="ShadowBall"/> so the ball and its coloured shadow match.</summary>
	public readonly Texture BuildColorMap()
	{
		var px = new Color32[TexW * TexH];
		var primary = Primary;
		var accent = Accent;

		for ( int y = 0; y < TexH; y++ )
		{
			float v = (y + 0.5f) / TexH;       // 0..1 top->bottom
			for ( int x = 0; x < TexW; x++ )
			{
				float u = (x + 0.5f) / TexW;   // 0..1 around
				px[y * TexW + x] = Sample( u, v, primary, accent );
			}
		}

		return Texture.Create( TexW, TexH )
			.WithData( ToBytes( px ) )
			.WithMips()
			.Finish();
	}

	private readonly Color32 Sample( float u, float v, Color primary, Color accent )
	{
		switch ( Pattern )
		{
			case BallPattern.HalfHalf:
			{
				// A thin dark band at the equator, primary above / accent below.
				if ( MathF.Abs( v - 0.5f ) < 0.035f ) return Grout;
				return (v < 0.5f ? primary : accent).ToColor32();
			}
			case BallPattern.Polkadot:
			{
				// Accent dots on a lattice over the primary field. The lattice is offset every other row
				// so the dots read as a scatter, not a grid.
				const int cellsX = 9, cellsY = 5;
				float row = v * cellsY;
				float col = u * cellsX + (((int)row & 1) == 1 ? 0.5f : 0f);
				float fx = Frac( col ) - 0.5f;
				float fy = Frac( row ) - 0.5f;
				float d = MathF.Sqrt( fx * fx + fy * fy );
				return (d < 0.30f ? accent : primary).ToColor32();
			}
			case BallPattern.Cubes:
			{
				// Blocky checker with dark grout lines between the cells.
				const int cellsX = 8, cellsY = 4;
				float cx = u * cellsX, cy = v * cellsY;
				float gx = MathF.Min( Frac( cx ), 1f - Frac( cx ) );
				float gy = MathF.Min( Frac( cy ), 1f - Frac( cy ) );
				if ( MathF.Min( gx, gy ) < 0.06f ) return Grout;
				bool even = (((int)cx + (int)cy) & 1) == 0;
				return (even ? primary : accent).ToColor32();
			}
			case BallPattern.Galaxy:
			default:
			{
				// FBM nebula between the two hues over a near-black void, flecked with white stars.
				float n = Fbm( u, v, 8 );                       // 0..1, seamless around u
				float neb = MathF.Pow( n, 1.6f );               // bias toward the dark void
				var voidCol = primary * 0.14f;
				var col = Color.Lerp( voidCol, accent, neb );
				col = Color.Lerp( col, primary, 0.35f * neb );

				// Sparse white stars from an independent hash.
				float s = Hash( MathF.Floor( u * TexW ), MathF.Floor( v * TexH ), 7.13f );
				if ( s > 0.9975f ) return Color.White.ToColor32();
				if ( s > 0.994f ) return (col + Color.White * 0.5f).ToColor32();
				return col.ToColor32();
			}
		}
	}

	// Dark grout / band colour shared by HalfHalf and Cubes.
	private static readonly Color Grout = new( 0.05f, 0.05f, 0.06f );

	private static float Frac( float x ) => x - MathF.Floor( x );

	private static byte[] ToBytes( Color32[] px )
	{
		var bytes = new byte[px.Length * 4];
		for ( int i = 0; i < px.Length; i++ )
		{
			bytes[i * 4 + 0] = px[i].r;
			bytes[i * 4 + 1] = px[i].g;
			bytes[i * 4 + 2] = px[i].b;
			// Force a fully-opaque alpha: the glass material reads g_tColor.a as base opacity, so pattern
			// maths that dims alpha (galaxy) must not leak into it.
			bytes[i * 4 + 3] = 255;
		}
		return bytes;
	}

	// --- Value-noise (galaxy) ----------------------------------------------------------------
	// A deterministic hash in [0,1). periodX lets the lattice wrap horizontally so there's no seam
	// down the back of the ball where u wraps 1->0.
	private static float Hash( float xi, float yi, float seed )
	{
		float h = MathF.Sin( xi * 127.1f + yi * 311.7f + seed * 74.7f ) * 43758.5453f;
		return Frac( h );
	}

	private static float ValueNoise( float x, float y, int periodX )
	{
		int x0 = (int)MathF.Floor( x );
		int y0 = (int)MathF.Floor( y );
		float fx = x - x0, fy = y - y0;
		// Smoothstep the interpolants for a rounded look.
		fx = fx * fx * (3f - 2f * fx);
		fy = fy * fy * (3f - 2f * fy);

		float Corner( int cx, int cy ) => Hash( ((cx % periodX) + periodX) % periodX, cy, 0f );
		float a = Corner( x0, y0 ), b = Corner( x0 + 1, y0 );
		float c = Corner( x0, y0 + 1 ), d = Corner( x0 + 1, y0 + 1 );
		return MathX.Lerp( MathX.Lerp( a, b, fx ), MathX.Lerp( c, d, fx ), fy );
	}

	private static float Fbm( float u, float v, int baseCellsX )
	{
		float sum = 0f, amp = 0.5f, total = 0f;
		int cells = baseCellsX;
		for ( int o = 0; o < 4; o++ )
		{
			// Latitude is scaled to half the longitude cells (u spans 2x the range of v on a sphere).
			sum += amp * ValueNoise( u * cells, v * (cells * 0.5f), cells );
			total += amp;
			amp *= 0.5f;
			cells *= 2;
		}
		return sum / total;
	}
}
