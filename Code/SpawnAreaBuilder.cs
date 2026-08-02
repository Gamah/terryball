using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Builds the shared SPAWN CONCOURSE behind the lanes — the back-of-house plaza players drop into. Each
/// lane's settee (with its LANE controls) sits up on a raised backboard deck behind the pins; this lays a
/// ground-level concourse behind all of them, a flight of stairs from the concourse up to EACH lane's
/// settee, and duplicate BALL (ball-designer) and HELP (how-to-play) podiums on the concourse so players
/// can set up before picking a lane. Plus two the settees don't have: TELE (jump to a lane) and the SETTINGS
/// pedestal, whose board reads out this client's world settings — see <see cref="WorldSettingsBoard"/>
/// and <see cref="WorldTheme"/>.
///
/// Everything is built procedurally in WORLD space from the lanes' own exposed anchors
/// (<see cref="LaneBuilder.SetteeDeckBackWorldPosition"/> etc.), so it adapts to however many lanes exist
/// and wherever they're placed. The build is deferred to the first frame the lanes have finished their own
/// geometry (their deck-top Z is published), then runs once. It also positions the authored
/// <see cref="SpawnPoint"/> child that <see cref="AlleyServer"/> spawns players on.
///
/// Like the lane geometry this is client-local, non-networked structure: every client builds an identical
/// copy in place, so nothing here needs to replicate. Keep this GameObject at the world origin (identity)
/// so local == world for the children it parents.
/// </summary>
public sealed class SpawnAreaBuilder : Component
{
	/// <summary>The empty child transform players spawn on (referenced by <see cref="AlleyServer"/>). Moved
	/// to the middle of the built concourse, facing the lanes.</summary>
	[Property] public GameObject SpawnPoint { get; set; }

	/// <summary>Horizontal depth (world units) of the staircase from the concourse up to a settee.</summary>
	[Property] public float StairsRun { get; set; } = 2200f;
	/// <summary>Depth (world units, along the lane axis) of the flat concourse behind the stairs.</summary>
	[Property] public float ConcourseDepth { get; set; } = 2400f;
	/// <summary>Extra half-width (world units) the concourse extends past the outermost lanes.</summary>
	[Property] public float SideMargin { get; set; } = 1200f;
	/// <summary>Distance (world units) between the BALL and HELP podiums; players spawn midway between them.</summary>
	[Property] public float PodiumSeparation { get; set; } = 300f;

	[Property, Group( "Colors" )] public Color FloorColor { get; set; } = new( 0.14f, 0.15f, 0.20f, 1f );
	[Property, Group( "Colors" )] public Color StairColor { get; set; } = new( 0.20f, 0.22f, 0.28f, 1f );
	[Property, Group( "Colors" )] public Color PodiumColor { get; set; } = new( 0.16f, 0.17f, 0.22f, 1f );
	[Property, Group( "Colors" )] public Color DesignTopColor { get; set; } = new( 0.45f, 0.32f, 0.60f, 1f );
	[Property, Group( "Colors" )] public Color HelpTopColor { get; set; } = new( 0.30f, 0.52f, 0.40f, 1f );
	[Property, Group( "Colors" )] public Color TeleportTopColor { get; set; } = new( 0.28f, 0.55f, 0.62f, 1f );
	[Property, Group( "Colors" )] public Color WorldTopColor { get; set; } = new( 0.55f, 0.45f, 0.15f, 1f );

	private bool _built;
	private Material _matFloorDark; // dark terrazzo on the ramps up to the settees
	private Material _matCarpet;    // retro carpet on the concourse floor (the old settee texture)

	protected override void OnUpdate() => EnsureBuilt();

	/// <summary>Build the concourse once the lanes are ready. Safe to call repeatedly (guarded); called both
	/// from OnUpdate and eagerly by <see cref="AlleyServer"/> right before it spawns a player, so the spawn
	/// point is already positioned on the concourse (not still at its authored placeholder).</summary>
	public void EnsureBuilt()
	{
		if ( _built ) return;

		// The lanes are authored (all present at load), but each publishes its deck-top Z only when its
		// DEFERRED geometry build finishes — one lane per frame via BuildScheduler. Wait for EVERY lane before
		// laying the concourse: building off whichever lanes happened to be ready first (often just lane 1) and
		// then locking (_built) is what left the plaza sized to a single lane instead of spanning all four.
		var all = Scene.GetAllComponents<Lane>()
			.Select( l => l.Builder )
			.Where( b => b.IsValid() )
			.ToList();
		if ( all.Count == 0 ) return;
		if ( all.Any( b => b.SetteeDeckTopWorldZ <= 0.01f ) ) return; // some lane hasn't finished building yet

		var lanes = all.OrderBy( b => b.WorldPosition.y ).ToList();

		_built = true;
		Build( lanes );
	}

	private void Build( List<LaneBuilder> lanes )
	{
		// Citizen units-per-inch — the concourse furniture is sized to the walking players, not the giant
		// ball (matches LaneBuilder's settee/podium scale so the podiums read the same up here).
		const float cu = 1f;

		// All lanes share orientation and differ only in Y, so the deck back edge X + top Z are common.
		var deckBackX = lanes.Max( b => b.SetteeDeckBackWorldPosition.x );
		var deckTopZ = lanes.Max( b => b.SetteeDeckTopWorldZ );

		var yMin = lanes.Min( b => b.WorldPosition.y - b.SetteeHalfWidthUnits ) - SideMargin;
		var yMax = lanes.Max( b => b.WorldPosition.y + b.SetteeHalfWidthUnits ) + SideMargin;
		var ySpan = yMax - yMin;
		var yMid = (yMin + yMax) * 0.5f;

		// X layout, going AWAY from the lanes (+x): the stairs climb up to the deck back edge, then the flat
		// concourse runs on behind them.
		var stairTopX = deckBackX;                 // top of the stairs meets the settee deck
		var stairFootX = stairTopX + StairsRun;    // foot of the stairs, on the concourse floor
		var concFrontX = stairFootX;
		var concBackX = stairFootX + ConcourseDepth;
		var concMidX = (concFrontX + concBackX) * 0.5f;

		// ---- Concourse floor slab (thin, on the building floor). A raised lip reads it as a defined area.
		// Dressed in the retro carpet (the settees' old texture), tiled at that same human-scale density so
		// the motifs read at roughly person size across the plaza.
		var slabThk = 8f * cu;
		_matCarpet ??= Material.Load( "materials/carpet.vmat" );
		_matFloorDark ??= Material.Load( "materials/floordark.vmat" ); // terrazzo on the ramps (below)
		MakeBox( "ConcourseFloor", new Vector3( concMidX, yMid, slabThk * 0.5f ),
			new Vector3( ConcourseDepth, ySpan, slabThk ), FloorColor, mat: _matCarpet, tileWorld: 160f );

		// ---- Stairs: one flight per lane, from the concourse floor up to that lane's settee deck.
		foreach ( var b in lanes )
			BuildStairs( b.WorldPosition.y, MathF.Max( b.SetteeHalfWidthUnits * 1.2f, 300f ),
				stairFootX, stairTopX, deckTopZ );

		// ---- Podiums on the concourse: BALL (designer) and HELP (how-to-play), duplicated from the lanes.
		var podiumTopZ = slabThk;
		var podiumH = 40f * cu;
		var podRow = concFrontX + ConcourseDepth * 0.42f; // a bit forward of the concourse middle
		var podHalf = PodiumSeparation * 0.5f * cu;        // BALL / HELP a short way either side of centre
		BuildPodium( "SpawnDesignTable", cu, podRow, yMid - podHalf, podiumTopZ, podiumH,
			DesignTopColor, "BALL", "rgba(176,125,230,0.95)" );
		Attach<BallDesignerStation>( new Vector3( podRow, yMid - podHalf, podiumTopZ ), s => s.InteractRange = 160f );

		BuildPodium( "SpawnHelpPodium", cu, podRow, yMid + podHalf, podiumTopZ, podiumH,
			HelpTopColor, "HELP", "rgba(120,220,160,0.95)" );
		Attach<HelpStation>( new Vector3( podRow, yMid + podHalf, podiumTopZ ), s => s.InteractRange = 160f );

		// TELEPORT podium: directly AHEAD of the spawn (toward the lanes) so players face it on spawn.
		var telePodX = podRow - (300f + PodiumSeparation * 0.5f) * cu;
		BuildPodium( "SpawnTeleportPodium", cu, telePodX, yMid, podiumTopZ, podiumH,
			TeleportTopColor, "TELE", "rgba(90,200,220,0.95)" );
		Attach<TeleportStation>( new Vector3( telePodX, yMid, podiumTopZ ), s => s.InteractRange = 160f );

		// SETTINGS pedestal: the world-settings board. On the TELE row (so it's inside the view cone a player
		// spawns facing — a board nobody ever turns around to see is a board nobody reads) but offset well
		// clear in +y, past the HELP podium's side, so the two prompts can't fight over the E key. Unlike
		// the other three this podium carries a READABLE board as well as its label sign — the summary of
		// what the theme is currently set to, the same display-only-board-plus-engage-screen split
		// rotaliate-client and gambit use for their settings walls.
		var worldPodY = yMid + PodiumSeparation * 1.3f * cu;
		BuildPodium( "SpawnWorldPodium", cu, telePodX, worldPodY, podiumTopZ, podiumH,
			WorldTopColor, "SETTINGS", "rgba(241,196,15,0.95)" );
		Attach<WorldSettingsStation>( new Vector3( telePodX, worldPodY, podiumTopZ ), s => s.InteractRange = 160f );
		BuildWorldBoard( cu, telePodX, worldPodY, podiumTopZ + podiumH );

		// ---- Spawn point: BETWEEN the BALL/HELP podiums (same row, centre Y), facing the lanes (-x) so the
		// TELEPORT podium is straight ahead.
		if ( SpawnPoint.IsValid() )
		{
			SpawnPoint.WorldPosition = new Vector3( podRow, yMid, slabThk + 2f );
			SpawnPoint.WorldRotation = Rotation.FromYaw( 180f ); // face -x, toward the stairs/lanes
		}
	}

	// A plain walkable ramp up to a settee (rises toward the lanes, -x). For now this is just the ramp —
	// no separate stepped treads — so the surface players walk on is the same one they see.
	private void BuildStairs( float laneY, float halfWidth, float footX, float topX, float topZ )
	{
		var run = footX - topX;                 // positive (foot is farther out in +x)
		if ( run <= 1f || topZ <= 1f ) return;
		var angle = MathF.Atan2( topZ, run );   // rises as x decreases toward the deck
		var angleDeg = MathX.RadianToDegree( angle );
		var thk = 60f;
		var len = MathF.Sqrt( run * run + topZ * topZ );
		var midX = (footX + topX) * 0.5f;

		// A plain sloped RAMP — one solid collider whose top surface runs foot(z=0) → deck(z=topZ), like the
		// lane approach ramp (mirrored in x since it rises toward -x). This is the walkable surface; the
		// cosmetic stepped treads are dropped for now (they were collider-less and players fell through).
		MakeBox( $"StairRamp_{laneY:F0}",
			new Vector3( midX - MathF.Sin( angle ) * thk * 0.5f, laneY, topZ * 0.5f - MathF.Cos( angle ) * thk * 0.5f ),
			new Vector3( len, halfWidth * 2f, thk ), StairColor,
			rot: Rotation.FromPitch( angleDeg ), mat: _matFloorDark, tileWorld: 200f );
	}

	/// <summary>A walk-up podium: a solid desk box + a themed top + a floating billboarded label sign
	/// (BALL / HELP). Mirrors <see cref="LaneBuilder.BuildPodium"/> so the concourse podiums match the
	/// settee ones.</summary>
	private void BuildPodium( string name, float cu, float x, float y, float baseTopZ, float podiumH,
		Color topColor, string label, string accentHex )
	{
		MakeBox( name, new Vector3( x, y, baseTopZ + podiumH * 0.5f ),
			new Vector3( 18f * cu, 44f * cu, podiumH ), PodiumColor );
		MakeDecal( $"{name}Top", new Vector3( x, y, baseTopZ + podiumH + 1f * cu ),
			new Vector3( 20f * cu, 46f * cu, 2f * cu ), topColor );

		const float s2w = 0.05f;
		// Shared with LaneBuilder's settee podiums via StationSign, whose markup is sized to them.
		var panelPx = StationSign.PanelPixels;
		var scale = (StationSign.WorldWidth * cu) / (panelPx.x * s2w);
		var signH = panelPx.y * s2w * scale;
		var signZ = baseTopZ + podiumH + 14f * cu + signH * 0.5f;

		var sign = new GameObject( true, $"{name}Sign" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		// Client-local UI: don't replicate a duplicate (empty-Label) StationSign into the parent's snapshot.
		sign.NetworkMode = NetworkMode.Never;
		sign.WorldPosition = new Vector3( x, y, signZ );
		sign.WorldScale = scale;

		var wp = sign.Components.Create<WorldPanel>();
		wp.PanelSize = panelPx;
		wp.RenderScale = 1f;
		wp.LookAtCamera = true;
		wp.InteractionRange = 100000f;

		var s = sign.Components.Create<StationSign>();
		s.Label = label;
		s.Accent = accentHex;
	}

	/// <summary>The SETTINGS pedestal's read-out board — a <see cref="WorldSettingsBoard"/> on a WorldPanel
	/// standing above the podium, facing the spawn point. Display-only; editing is the walk-up screen.</summary>
	private void BuildWorldBoard( float cu, float x, float y, float podiumTopZ )
	{
		const float s2w = 0.05f;                            // WorldPanel.ScreenToWorldScale (world units per px @ scale 1)
		// CRITICAL: a WorldPanel's root has Scale == 2, so the CSS layout area is PanelSize / 2 — the
		// razor's root is sized 800x550 for this 1600x1100. Change the two together or the board clips.
		var panelPx = new Vector2( 1600f, 1100f );
		var worldW = 96f * cu;                              // a bit wider than the podium, readable on approach
		var scale = worldW / (panelPx.x * s2w);             // GO scale so the panel fills that width (RenderScale stays 1)
		var boardH = panelPx.y * s2w * scale;

		var go = new GameObject( true, "SpawnWorldBoard" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		// Client-local UI, like the station signs: don't replicate an empty duplicate into the snapshot.
		go.NetworkMode = NetworkMode.Never;
		go.WorldPosition = new Vector3( x, y, podiumTopZ + 10f * cu + boardH * 0.5f );
		go.WorldScale = scale;
		// An identity WorldPanel faces +x, and the spawn point is at +x of this podium looking -x at it —
		// so identity is already the right way round here. (The lane boards flip by yaw 180 because they
		// face the approach, the other direction.) No LookAtCamera: this one is a fixed pedestal board,
		// not a floating label, so it holds still while you walk around it.
		var wp = go.Components.Create<WorldPanel>();
		wp.PanelSize = panelPx;
		wp.RenderScale = 1f;
		wp.InteractionRange = 100000f;

		go.Components.Create<WorldSettingsBoard>();
	}

	// Add an interaction-station component at a world spot (an empty GO — the visual desk is the podium).
	private void Attach<T>( Vector3 worldPos, Action<T> configure ) where T : Component, new()
	{
		var go = new GameObject( true, typeof( T ).Name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		configure( go.Components.Create<T>() );
	}

	// Static box with a box collider (walkable/solid). World-positioned; root is identity so no local math.
	// Optional material tiled to a fixed physical size (units per tile) across the box's dominant face.
	private void MakeBox( string name, Vector3 worldPos, Vector3 size, Color tint, Rotation? rot = null,
		Material mat = null, float tileWorld = 0f )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		if ( rot.HasValue ) go.WorldRotation = rot.Value;

		var col = go.Components.GetOrCreate<BoxCollider>();
		col.Scale = size;

		var visual = new GameObject( true, name + "Visual" ) { Parent = go };
		var mr = visual.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;
		if ( mat is not null && tileWorld > 0f )
		{
			// Tile across the box's dominant face (the two axes either side of the THIN one) at a fixed
			// physical density. Keep each world axis on its own UV — the box's top-face U runs along X and
			// V along Y — instead of sorting by magnitude, or a floor wider in Y than X comes out stretched.
			var (ud, vd) = size.z <= size.x && size.z <= size.y ? (size.x, size.y)   // flat slab: U=x, V=y
				: size.y <= size.x && size.y <= size.z ? (size.x, size.z)             // wall (thin in y): U=x, V=z
				: (size.y, size.z);                                                   // thin in x: U=y, V=z
			var m = mat.CreateCopy();
			m.Set( "g_vTexCoordScale", new Vector2( MathF.Max( ud / tileWorld, 0.02f ), MathF.Max( vd / tileWorld, 0.02f ) ) );
			mr.MaterialOverride = m;
		}
		var b = mr.Model.Bounds.Size;
		visual.LocalScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}

	// Visual-only thin slab (no collider).
	private void MakeDecal( string name, Vector3 worldPos, Vector3 size, Color tint )
	{
		var go = new GameObject( true, name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		var mr = go.Components.GetOrCreate<ModelRenderer>();
		mr.Model = Model.Load( "models/dev/box.vmdl" );
		mr.Tint = tint;
		var b = mr.Model.Bounds.Size;
		go.WorldScale = new Vector3( size.x / b.x, size.y / b.y, size.z / b.z );
	}
}
