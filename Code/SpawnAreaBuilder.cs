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
/// can set up before picking a lane.
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
	/// <summary>Cosmetic tread spacing (world units of RISE per drawn step) on the stair ramps.</summary>
	[Property] public float TreadRise { get; set; } = 22f;

	[Property, Group( "Colors" )] public Color FloorColor { get; set; } = new( 0.14f, 0.15f, 0.20f, 1f );
	[Property, Group( "Colors" )] public Color StairColor { get; set; } = new( 0.20f, 0.22f, 0.28f, 1f );
	[Property, Group( "Colors" )] public Color TreadColor { get; set; } = new( 0.28f, 0.30f, 0.37f, 1f );
	[Property, Group( "Colors" )] public Color PodiumColor { get; set; } = new( 0.16f, 0.17f, 0.22f, 1f );
	[Property, Group( "Colors" )] public Color DesignTopColor { get; set; } = new( 0.45f, 0.32f, 0.60f, 1f );
	[Property, Group( "Colors" )] public Color HelpTopColor { get; set; } = new( 0.30f, 0.52f, 0.40f, 1f );

	private bool _built;

	protected override void OnUpdate()
	{
		if ( _built ) return;

		// Wait until the lanes have finished building (their deck-top Z is published on first geometry pass).
		var lanes = Scene.GetAllComponents<Lane>()
			.Select( l => l.Builder )
			.Where( b => b.IsValid() && b.SetteeDeckTopWorldZ > 0.01f )
			.OrderBy( b => b.WorldPosition.y )
			.ToList();
		if ( lanes.Count == 0 ) return;

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
		var slabThk = 8f * cu;
		MakeBox( "ConcourseFloor", new Vector3( concMidX, yMid, slabThk * 0.5f ),
			new Vector3( ConcourseDepth, ySpan, slabThk ), FloorColor );

		// ---- Stairs: one flight per lane, from the concourse floor up to that lane's settee deck.
		foreach ( var b in lanes )
			BuildStairs( b.WorldPosition.y, MathF.Max( b.SetteeHalfWidthUnits * 1.2f, 300f ),
				stairFootX, stairTopX, deckTopZ );

		// ---- Podiums on the concourse: BALL (designer) and HELP (how-to-play), duplicated from the lanes.
		var podiumTopZ = slabThk;
		var podiumH = 40f * cu;
		var podRow = concFrontX + ConcourseDepth * 0.42f; // a bit forward of the concourse middle
		BuildPodium( "SpawnDesignTable", cu, podRow, yMid - 380f * cu, podiumTopZ, podiumH,
			DesignTopColor, "BALL", "rgba(176,125,230,0.95)" );
		Attach<BallDesignerStation>( new Vector3( podRow, yMid - 380f * cu, podiumTopZ ), s => s.InteractRange = 160f );

		BuildPodium( "SpawnHelpPodium", cu, podRow, yMid + 380f * cu, podiumTopZ, podiumH,
			HelpTopColor, "HELP", "rgba(120,220,160,0.95)" );
		Attach<HelpStation>( new Vector3( podRow, yMid + 380f * cu, podiumTopZ ), s => s.InteractRange = 160f );

		// ---- Spawn point: middle-back of the concourse, facing the lanes (-x) so players look toward the stairs.
		if ( SpawnPoint.IsValid() )
		{
			SpawnPoint.WorldPosition = new Vector3( concBackX - ConcourseDepth * 0.25f, yMid, slabThk + 2f );
			SpawnPoint.WorldRotation = Rotation.FromYaw( 180f ); // face -x, toward the stairs/lanes
		}
	}

	// A solid staircase up to a settee: a sloped ramp collider (the walkable surface) with cosmetic tread +
	// riser slabs stacked on top so it reads as steps. Ramp rises toward the lanes (-x).
	private void BuildStairs( float laneY, float halfWidth, float footX, float topX, float topZ )
	{
		var run = footX - topX;                 // positive (foot is farther out in +x)
		if ( run <= 1f || topZ <= 1f ) return;
		var angle = MathF.Atan2( topZ, run );   // rises as x decreases toward the deck
		var angleDeg = MathX.RadianToDegree( angle );
		var thk = 30f;
		var len = MathF.Sqrt( run * run + topZ * topZ );
		var midX = (footX + topX) * 0.5f;

		// Sloped slab pushed below its top surface (like the lane ramp, mirrored in x since it rises toward -x).
		MakeBox( $"Stairs_{laneY:F0}",
			new Vector3( midX - MathF.Sin( angle ) * thk * 0.5f, laneY, topZ * 0.5f - MathF.Cos( angle ) * thk * 0.5f ),
			new Vector3( len, halfWidth * 2f, thk ), StairColor,
			rot: Rotation.FromPitch( angleDeg ) );

		// Cosmetic treads + risers up the slope.
		var steps = Math.Max( 1, (int)MathF.Ceiling( topZ / MathF.Max( 4f, TreadRise ) ) );
		var dz = topZ / steps;
		var dx = run / steps;
		var treadThk = 4f;
		for ( var i = 0; i < steps; i++ )
		{
			var stepTopZ = (i + 1) * dz;
			var stepX = footX - (i + 0.5f) * dx; // moves toward the deck as we climb
			MakeDecal( $"Tread_{laneY:F0}_{i}", new Vector3( stepX, laneY, stepTopZ + treadThk * 0.5f ),
				new Vector3( dx, halfWidth * 2f, treadThk ), TreadColor );
			// Riser: the vertical face at the front (foot side) of the step.
			MakeDecal( $"Riser_{laneY:F0}_{i}", new Vector3( stepX + dx * 0.5f, laneY, stepTopZ - dz * 0.5f ),
				new Vector3( treadThk, halfWidth * 2f, dz ), StairColor );
		}
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
		var panelPx = new Vector2( 400f, 160f );
		var scale = (55f * cu) / (panelPx.x * s2w);
		var signH = panelPx.y * s2w * scale;
		var signZ = baseTopZ + podiumH + 14f * cu + signH * 0.5f;

		var sign = new GameObject( true, $"{name}Sign" ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
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

	// Add an interaction-station component at a world spot (an empty GO — the visual desk is the podium).
	private void Attach<T>( Vector3 worldPos, Action<T> configure ) where T : Component, new()
	{
		var go = new GameObject( true, typeof( T ).Name ) { Parent = GameObject, Flags = GameObjectFlags.NotSaved };
		go.WorldPosition = worldPos;
		configure( go.Components.Create<T>() );
	}

	// Static box with a box collider (walkable/solid). World-positioned; root is identity so no local math.
	private void MakeBox( string name, Vector3 worldPos, Vector3 size, Color tint, Rotation? rot = null )
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
