using Sandbox;
using Sandbox.Citizen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Terryball;

/// <summary>
/// The hamster-ball mechanic.
///
/// A large glass ball (Rigidbody + SphereCollider) with Terry (the Citizen) riding the
/// INSIDE surface.
///
/// Control: WASD is camera-relative and names a target travel direction. Each tick we steer
/// the ball's horizontal velocity toward wishDir * <see cref="MaxSpeed"/> with a proportional
/// force applied at the ball's centre (friction then spins it up to roll). With no key held
/// the target is zero, so the same controller becomes a brake that opposes the current
/// velocity — the ball coasts to a stop and never accelerates on its own.
///
/// Terry is purely visual: he "runs" up the inner wall toward the wish direction and gravity
/// settles him to the bottom, but his motion no longer drives the ball (it used to, via an
/// off-centre-weight torque). His run animation is driven from how fast he's actually moving
/// through the world, and no Terry↔ball collision is simulated (by design). The standalone
/// <see cref="TerryController"/> stub is kept in the repo for other reuse.
/// </summary>
public sealed class BallController : Component, Component.ICollisionListener
{
	[Property, Group( "Ball" )] public float Radius { get; set; } = 100f;
	/// <summary>Surface friction of the ball. Kept LOW because a real lane/pin is slick (oiled): with
	/// high friction the ball's topspin GRIPS the pins and walks UP them (it climbs like a topspin ball
	/// on a wall — the upward kick comes from tangential friction, not the collision). Low friction lets
	/// it drive through low. The ball is force-driven, so it doesn't need grip to move.</summary>
	[Property, Group( "Ball" )] public float Friction { get; set; } = 0.3f;
	/// <summary>Restitution (bounciness) of the ball's surface. By the collision algebra (M=6.8 vs
	/// m=1.6), HIGH restitution makes the ball lose MORE speed to the pin (keeps ~63% at e=0.95 vs
	/// ~81% at e=0) AND makes the pin fly faster — both wanted. Low restitution makes ball and pin
	/// move together (ball barely slows = plows). So keep this high; the steamroll was a contact/CCD
	/// problem, not restitution.</summary>
	[Property, Group( "Ball" )] public float Elasticity { get; set; } = 0.3f;
	/// <summary>Pit catch strength: once the ball passes the back of the pin deck it's over the pit, and
	/// its speed bleeds at this rate (per-second exponential decay). A deterministic "cushion" so the
	/// ball dumps its energy and settles in the pit instead of pinging off the dead back wall and
	/// rocketing back up the lane — independent of any collision/restitution quirk. 0 = off.</summary>
	[Property, Group( "Ball" )] public float PitDamping { get; set; } = 6f;
	/// <summary>Passive rolling resistance (per-second decay of spin). Braking is active now —
	/// releasing the keys applies a <see cref="BrakeGain"/> counter-force whose effective decay
	/// rate is BrakeGain/Mass — so this MUST stay well below that or it silently dominates the
	/// coast and overrides the brake. It mainly exists to bleed off lone vertical (yaw) spin the
	/// brake can't see. Higher also lowers top speed under drive.</summary>
	[Property, Group( "Ball" )] public float AngularDamping { get; set; } = 0.05f;
	/// <summary>Mass in kg. A regulation bowling ball is ~6.8 kg (15 lb), but that makes the ball too
	/// dominant ("meaty") over the pins. Detached down to reduce the ball:pin ratio so it deflects
	/// more and clears fewer pins. NOTE: the live value is serialized in the scene — change it there,
	/// the code default only applies to freshly-added components.</summary>
	[Property, Group( "Ball" )] public float Mass { get; set; } = 5.3f;

	[Property, Group( "Occupant" )] public float TerryScale { get; set; } = 1.5f;

	/// <summary>Ball speed — in the SAME bowling-scale mph the HUD shows — at or above which Terry switches
	/// from running into the Citizen's continuous Roll animation. His legs ramp up walk→run over 0..this,
	/// maxing out just as he tucks into the roll. Kept in mph so it tracks the gauge regardless of Radius.</summary>
	[Property, Group( "Occupant" )] public float RollAtMph { get; set; } = 11.5f;
	/// <summary>Top leg speed (u/s) Terry's run ramps to right before he rolls. The Citizen is ~1 unit/inch,
	/// so ≈167 u/s reads as a ~9.5 mph run; below it the Auto move-style eases him from a walk into a run.</summary>
	[Property, Group( "Occupant" )] public float RunTopSpeed { get; set; } = 167f;
	/// <summary>Animgraph playback multiplier while Terry is rolling. The Roll clip plays at a fixed baked
	/// cadence, so we drive its speed here.</summary>
	[Property, Group( "Occupant" )] public float RollPlaybackRate { get; set; } = 5.25f;
	/// <summary>Animgraph playback multiplier for his legs at the SLOW end (a near-stationary walk). The
	/// move-speed cadence alone is too slow, so we scale the whole walk→run ramp via the playback rate.</summary>
	[Property, Group( "Occupant" )] public float WalkPlaybackRate { get; set; } = 0.5f;
	/// <summary>Animgraph playback multiplier the run ramps UP to right at the roll threshold.</summary>
	[Property, Group( "Occupant" )] public float RunPlaybackRate { get; set; } = 4.5f;
	/// <summary>How far toward the roll threshold (0..1) Terry stays at his base WalkPlaybackRate before
	/// his legs start speeding up. e.g. 0.3 = no speed-up over the first 30% of the run-up, then ramp.</summary>
	[Property, Group( "Occupant" )] public float WalkRampStart { get; set; } = 0.3f;
	/// <summary>Seconds to crossfade the run↔roll change. We ease a blend over this window (flipping the
	/// special-move state at its mid-point and crossfading the playback rate) so the switch isn't a hard
	/// snap. The Citizen animgraph crossfades the actual pose on its own baked timing.</summary>
	[Property, Group( "Occupant" )] public float TransitionTime { get; set; } = 0.5f;
	/// <summary>Playback rate to dip to through the middle of the run↔roll swap. PlaybackRate also scales the
	/// animgraph's own pose crossfade, so easing to ~1× across the swap lets that crossfade play at its real
	/// (visible) baked duration instead of being sped up by the fast run/roll cadence rates.</summary>
	[Property, Group( "Occupant" )] public float TransitionPlaybackRate { get; set; } = 1f;

	// --- Ball steering (the control scheme) --------------------------------------------
	// WASD names a target travel direction; we steer the ball's horizontal velocity toward
	// wishDir * MaxSpeed with a proportional force at the ball's centre. With no key held the
	// target is zero, so the same controller brakes — the ball only ever loses speed when
	// nothing is pressed and never self-accelerates.

	/// <summary>Top travel speed (u/s) a full WASD push steers toward.</summary>
	[Property, Group( "Drive" )] public float MaxSpeed { get; set; } = 5625f;
	/// <summary>Proportional gain turning the velocity error (target − current) into a centre
	/// force while a key is held. Force = error · gain, so this is roughly "how many u/s² of
	/// accel per u/s you're off target." Higher = snappier accel/steering. Tune by feel.</summary>
	[Property, Group( "Drive" )] public float DriveGain { get; set; } = 15f;
	/// <summary>Proportional gain on the braking force applied when no key is held; it opposes
	/// the current velocity so the ball coasts to a stop. The coast's decay rate is
	/// BrakeGain/Mass per second, so with Mass ≈ 6.8 a gain of 1 barely slows the ball (≈5 s
	/// time-constant); 20 hauls the ball down hard, stopping it in well under a second. Tune by feel.</summary>
	[Property, Group( "Drive" )] public float BrakeGain { get; set; } = 20f;

	// --- Oiled-lane hook (guided-throw mode) -------------------------------------------
	// A real lane is oiled heavy up front and dry at the back, so a spinning ball SLIDES nearly straight
	// through the oil and only "hooks" once it reaches the dry back end near the pins. We model that honestly
	// from the ball's actual spin: the side rotation the bowler put on the ball (about its travel axis) makes
	// its contact patch slip sideways; over the dry back end (past OilFrontFraction) friction resists that
	// slip and drives the ball over — the hook — and it self-limits as the ball revs up to rolling on its new
	// line. So the spin visible on the ball is exactly what breaks it. Because the grip builds LATE, a big
	// spin doesn't fling the ball into the gutter off the foul line — it tracks down-lane first and breaks
	// toward the pocket near the deck. Only the aimed throw arms it; a free-drive ball keeps HookAmount 0.

	/// <summary>Arms the hook for an aimed throw: nonzero = this ball was launched with side rotation, so run
	/// the oiled-lane friction hook (the curve itself comes from the ball's real <see cref="Rigidbody.AngularVelocity"/>,
	/// not from this value). Set at launch by the <see cref="ThrowAimer"/> to the signed spin (−1..+1); forced
	/// back to 0 whenever the ball is under free drive so a free throw never hooks.</summary>
	public float HookAmount { get; set; }
	/// <summary>Max lateral grip (u/s² of sideways accel) friction can apply on the fully-dry back end — the
	/// Coulomb limit that resists the ball's sideways contact slip. Higher = the hook develops faster and
	/// SHARPER once the ball reaches the dry back end; it does NOT set how far the ball ultimately breaks
	/// (that's the revs the bowler put on — see ThrowAimer.SideRotationRate), because the grip self-limits once
	/// the ball revs up to rolling on its new line.</summary>
	[Property, Group( "Oil" )] public float HookAccel { get; set; } = 2000f;
	/// <summary>Fraction of the foul-line→pin-deck distance that is "oiled" — over this front stretch the
	/// hook stays near zero (the ball slides), then ramps to full over the remaining dry back end. Higher
	/// = the ball holds its line longer and breaks later/harder near the pins.</summary>
	[Property, Group( "Oil" )] public float OilFrontFraction { get; set; } = 0.6f;
	/// <summary>Slip (u/s) below which the contact is treated as ROLLING (static) and the hook friction cuts
	/// out — a deadband so a tiny residual slip after rev-out doesn't slam full grip back and forth and jitter
	/// the ball. Above it, kinetic friction grips at the full <see cref="HookAccel"/> limit.</summary>
	[Property, Group( "Oil" )] public float HookSlipDeadband { get; set; } = 40f;
	/// <summary>Angular damping used ONLY while an aimed throw is coasting — near zero, so the revs the bowler
	/// put on survive the long oiled front with plenty left to hook on the dry back end (the normal rolling
	/// <see cref="AngularDamping"/> would bleed roughly a third of them before the ball ever reaches the dry
	/// lane, killing the break and making the ball look under-spun). Friction handles the rev-out.</summary>
	[Property, Group( "Oil" )] public float HookCoastAngularDamping { get; set; } = 0.005f;

	/// <summary>Ball world velocity (units/s); zero before the body exists. For the roll audio.</summary>
	public Vector3 Velocity => _body.IsValid() ? _body.Velocity : Vector3.Zero;
	/// <summary>The drive target top speed (units/s) — the roll audio's reference for "full speed".</summary>
	public float MaxDriveSpeed => MaxSpeed;
	/// <summary>0..1 turning effort the player is applying (perpendicular correction), 0 after release.</summary>
	public float SteerEffort01 { get; private set; }

	/// <summary>When set, the ball ignores BOTH input and braking and just coasts on its own
	/// momentum (physics only). The round director flips this on once the ball crosses the throw
	/// line so the throw is committed to physics and you can watch it hit the pins.</summary>
	public bool ControlsLocked { get; set; }

	// --- Occupant (Terry) visual ride --------------------------------------------------
	// Terry rides CENTRED in the ball (purely visual — he doesn't drive it). He runs facing the
	// ball's travel direction and blends into a tucked forward somersault as it speeds up; see
	// RollAtMph / RunTopSpeed up top, OnUpdate (anim) and PlaceTerry.

	[Property, Group( "Debug" )] public bool ShowSpeed { get; set; } = true;
	/// <summary>Real ball size used only to report a bowling-scale mph on the HUD.</summary>
	[Property, Group( "Debug" )] public float RealBallInches { get; set; } = 8.5f;

	private Rigidbody _body;
	private SphereCollider _col;
	private GameObject _terry;
	private SkinnedModelRenderer _terryRenderer;
	private CitizenAnimationHelper _anim;

	// In-ball Terry is dressed in the ACTIVE bowler's avatar so everyone sees who's up riding inside.
	// Re-dressed (on every client) whenever the spotlight moves to a different bowler.
	private Guid _dressedBowler = Guid.Empty;
	private bool _dressedAny;
	private CancellationTokenSource _dressCts;

	// The glass shell + coloured-shadow projector, skinned to the active bowler's ball design. _shellMat is
	// a per-ball COPY of the glass material so writing its colour map doesn't clobber any other lane's ball.
	private ModelRenderer _shell;
	private Material _shellMat;
	private ShadowBall _shadow;
	private Texture _designMap;
	private BallDesign _skinnedDesign;
	private bool _skinnedAny;
	private GameDirector _director;
	// Resolve within our own lane (the director sharing our Lane root), never a scene-global scan, so
	// each lane's ball answers only to its own director.
	private GameDirector Director => _director.IsValid() ? _director : (_director = Lane.Of( this )?.Director);

	// Occupant state, in world space: Terry rides CENTRED in the ball (not on the inner wall). _faceDir
	// is his last meaningful heading (the ball's travel direction); _bodyCentreUp is the feet→torso-centre
	// offset that hangs his middle on the ball centre.
	private Vector3 _faceDir = Vector3.Forward; // last meaningful heading (ball travel dir)
	private float _bodyCentreUp;                // feet→centre height (scaled), so he sits centred in the ball
	private float _transitionT;                 // seconds left in the current run↔roll transition window
	private bool _wasRolling;                   // edge-detect the roll state change to open that window
	private bool _built;
	private float _throwLogAt;                  // last time the LogThrow hook trace was written (throttle)

	protected override void OnEnabled()
	{
		if ( _built ) return;
		_built = true;

		// Per-lane collision tag, set BEFORE the collider is built (shapes snapshot GameObject.Tags at build
		// time): this ball then only collides with its OWN lane's pins — the Collision.config ignores
		// DIFFERENT lane tags, so you can't drive your ball into a neighbour's rack and grief it.
		var laneTag = Lane.Of( this )?.CollisionTag;
		if ( !string.IsNullOrEmpty( laneTag ) )
			GameObject.Tags.Add( laneTag );

		// Physics: dynamic sphere that grips the floor so it rolls instead of sliding.
		// Create the Rigidbody first, then the collider.
		_body = Components.GetOrCreate<Rigidbody>();

		var col = Components.GetOrCreate<SphereCollider>();
		col.Radius = Radius;
		col.Friction = Friction;
		col.Elasticity = Elasticity; // springy hit so the ball deflects off the pins instead of plowing
		_col = col;

		// Body params go AFTER the collider: adding a collider shape rebuilds mass from
		// volume (BuildMass), which would otherwise clobber our MassOverride.
		_body.AngularDamping = AngularDamping;
		_body.MassOverride = Mass; // realistic bowling-ball mass vs the pins
		_body.EnhancedCcd = true;  // don't tunnel through the pins at speed
		// Mute the engine's default surface collision sounds for the ball too (ball↔lane drop, gutter,
		// etc.) — they're the generic clunks; the pins carry their own procedural crash audio.
		_body.RigidbodyFlags |= RigidbodyFlags.DisableCollisionSounds;

		// Procedural roll rumble + foul-line throw sound.
		Components.GetOrCreate<BallAudio>();

		// Cast a colour-projected shadow: the BallShadowTintPP post-process samples this ball's colour map
		// per-ray, so the shadow shows the ball's spatial colours (pokeball -> two-tone shadow), with Terry
		// (opaque occupant) reading black. Uses the same pokeball map the shell renders, so they match.
		var shadow = Components.GetOrCreate<ShadowBall>();
		shadow.Radius = Radius;
		shadow.ColorMapPath = "materials/pokeball_color.png"; // fallback until a bowler's design skins it (below)
		shadow.HasOpaqueOccupant = true;
		_shadow = shadow;

		// Visual: scaled dev sphere with the glass material so Terry inside shows through.
		var visual = new GameObject( true, "BallVisual" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		var shell = visual.Components.GetOrCreate<ModelRenderer>();
		shell.Model = Model.Load( "models/dev/sphere.vmdl" );
		// A per-ball COPY so skinning this ball's colour map (SkinToBowler) never touches another lane's ball.
		_shellMat = Material.Load( "materials/glassball_poke.vmat" ).CreateCopy();
		shell.MaterialOverride = _shellMat;
		_shell = shell;

		var shellBounds = shell.Model.Bounds.Size.x; // dev sphere ≈ 64 across (r 32)
		visual.LocalScale = new Vector3( (Radius * 2f) / shellBounds );

		// Terry: a Citizen riding the inside. His world transform is driven every frame, so
		// the ball's spin never carries him — he rides the inner surface independently.
		_terry = new GameObject( true, "Terry" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};
		_terry.LocalScale = new Vector3( TerryScale );
		_terry.Tags.Add( "occupant" ); // OccupantSilhouetteCapture renders only this tag => Terry's live silhouette for his coloured shadows

		var renderer = _terry.Components.GetOrCreate<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );
		_terryRenderer = renderer;

		// Feet→torso-centre height: the Citizen's origin is at his feet, so half his height is roughly his
		// middle. We hang him so this point sits on the ball centre and tumble him about it.
		_bodyCentreUp = (renderer.Model?.Bounds.Size.z ?? 64f) * 0.5f * TerryScale;

		_anim = _terry.Components.GetOrCreate<CitizenAnimationHelper>();
		_anim.Target = renderer;
		_anim.IsGrounded = true;

		PlaceTerry();
	}

	// Keep the in-ball Terry wearing whoever is currently up (runs on every client, bowler or
	// spectator, so all four feeds show the active bowler's avatar inside the ball).
	private void UpdateBowlerDress()
	{
		var id = Director?.ActiveBowlerId ?? Guid.Empty;

		// The Terry only appears once the crane has actually scooped the bowler IN (Carried) — so the
		// pickup flight is an empty ball, then Terry pops in on the scoop, and pops out again on the drop.
		// We still dress him off the active-bowler id below, which has him ready before the scoop lands.
		if ( _terry.IsValid() )
			_terry.Enabled = ActiveBowlerCarried( id );

		if ( id != _dressedBowler || !_dressedAny )
		{
			_dressedBowler = id;
			_dressedAny = true;
			_ = DressForBowler( id );
		}

		SkinToBowler( id );
	}

	// Skin the glass shell + coloured shadow to the active bowler's chosen ball design (networked on their
	// avatar, so every client resolves the same look). Regenerates the colour map only when the design
	// actually changes — so a bowler live-editing in the designer updates their ball, but it's otherwise idle.
	private void SkinToBowler( Guid id )
	{
		var avatar = id == Guid.Empty ? null
			: Scene.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && a.Network.Owner?.Id == id );
		if ( avatar is null ) return;

		var design = avatar.Design;
		if ( _skinnedAny && design.Equals( _skinnedDesign ) ) return;
		_skinnedDesign = design;
		_skinnedAny = true;

		_designMap?.Dispose();
		_designMap = design.BuildColorMap();
		_shellMat?.Set( "g_tColor", _designMap );
		if ( _shadow.IsValid() )
		{
			_shadow.ColorMap = _designMap;      // the projected shadow now samples the same map the shell shows
			_shadow.ColorMapPath = "";          // stop the post-process re-loading the pokeball fallback over it
		}
	}

	/// <summary>Dress the in-ball Terry in the bowler <paramref name="id"/>'s full avatar (community-market
	/// cosmetics included). Empty id (nobody up) leaves the previous outfit on — the ball is parked then
	/// anyway. Our own turn reads the complete local avatar; a remote bowler is read from their connection
	/// (their owned items). ApplyAsync downloads any missing workshop/inventory items first.</summary>
	private async Task DressForBowler( Guid id )
	{
		if ( !_terryRenderer.IsValid() || id == Guid.Empty ) return;

		_dressCts?.Cancel();
		_dressCts = new CancellationTokenSource();
		try
		{
			// Build the avatar container, then apply. CreateFromLocalUser/CreateFromConnection can throw
			// from inside the engine (e.g. an inventory read faulting in RemoveUnownedItems), so the whole
			// thing is guarded — a transient cosmetics failure must not take down the bowler's turn.
			ClothingContainer clothing;
			if ( Connection.Local is not null && id == Connection.Local.Id )
			{
				clothing = ClothingContainer.CreateFromLocalUser();
			}
			else
			{
				var conn = Connection.Find( id );
				clothing = conn is not null ? ClothingContainer.CreateFromConnection( conn ) : null;
			}
			if ( clothing is null ) return;

			await clothing.ApplyAsync( _terryRenderer, _dressCts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception e )
		{
			Log.Warning( $"[terryball] in-ball dressing failed: {e.Message}" );
		}
	}

	// Is the active bowler's avatar currently scooped into the ball? Drives the in-ball Terry's visibility
	// so it matches the crane's body↔ball swap on every client (the flag is synced from the bowler).
	private bool ActiveBowlerCarried( Guid id )
	{
		if ( id == Guid.Empty ) return false;
		foreach ( var a in Scene.GetAllComponents<TerryAvatar>() )
			if ( a.IsValid() && a.GameObject.Network.Owner?.Id == id )
				return a.Carried;
		return false;
	}

	protected override void OnDisabled() => _dressCts?.Cancel();

	// --- Crane transport (the claw mechanic) -------------------------------------------
	// Between the run-up and the bowler's seat the director flies the ball kinematically (a claw that
	// fetches/returns the player). It sails the full length of the lane and over the freshly-set rack, so
	// we drop the collider + gravity for the trip and the director sets the position each frame.

	/// <summary>Enter/leave crane mode: drop the collider + gravity (so the flight doesn't scatter the
	/// rack or sag) and kill any residual motion; leaving restores normal physics for the roll.</summary>
	public void SetCraneMode( bool on )
	{
		if ( _col.IsValid() ) _col.Enabled = !on;
		if ( _body.IsValid() )
		{
			_body.Gravity = !on;
			if ( on )
			{
				_body.Velocity = Vector3.Zero;
				_body.AngularVelocity = Vector3.Zero;
				_body.Sleeping = false;
			}
		}
	}

	// --- Aimed throw (guided-throw mode) -----------------------------------------------
	// While the bowler is lining up an aimed throw the ball is held frozen at the foul line (gravity off,
	// no motion) as the aimer shuttles it left/right; Launch then hands it a committed velocity + spin and
	// lets physics carry it. See ThrowAimer.

	/// <summary>Hold the ball dead still at <paramref name="pos"/> with gravity off — for the aim line-up,
	/// where the aimer positions it each frame. No hook until it's launched.</summary>
	public void HoldAt( Vector3 pos )
	{
		WorldPosition = pos;
		HookAmount = 0f;
		if ( _body.IsValid() )
		{
			_body.Gravity = false;
			_body.Velocity = Vector3.Zero;
			_body.AngularVelocity = Vector3.Zero;
			_body.Sleeping = false;
		}
	}

	/// <summary>Abort an aim line-up cleanly: restore gravity (a hold turned it off) and stop the ball where
	/// it sits, so a mode toggle / bail mid-line-up never leaves it floating. No hook.</summary>
	public void EndHold()
	{
		HookAmount = 0f;
		if ( _body.IsValid() )
		{
			_body.Gravity = true;
			_body.Velocity = Vector3.Zero;
			_body.AngularVelocity = Vector3.Zero;
		}
	}

	/// <summary>Commit the aimed throw: restore gravity, launch with <paramref name="velocity"/> and
	/// <paramref name="angularVelocity"/> (visual roll/spin), and arm the oiled-lane hook with
	/// <paramref name="hook"/> (−1..+1). The ball is expected to be ControlsLocked (coasting) from here.</summary>
	public void Launch( Vector3 velocity, Vector3 angularVelocity, float hook )
	{
		if ( _body.IsValid() )
		{
			_body.Gravity = true;
			_body.Sleeping = false;
			_body.Velocity = velocity;
			_body.AngularVelocity = angularVelocity;
		}
		HookAmount = hook;
	}

	/// <summary>Kinematically place the ball at <paramref name="pos"/> for a crane leg, held dead still.</summary>
	public void CraneMoveTo( Vector3 pos )
	{
		WorldPosition = pos;
		if ( _body.IsValid() )
		{
			_body.Velocity = Vector3.Zero;
			_body.AngularVelocity = Vector3.Zero;
		}
	}

	protected override void OnFixedUpdate()
	{
		var dt = Time.Delta;
		if ( dt <= 0f ) return;

		// Only the client whose turn it is drives the ball; for everyone else it sits parked (its
		// transform is networked from the active bowler). Nobody up = nobody drives.
		if ( Director is not null && !Director.LocalIsBowling ) return;

		// A free-drive ball never hooks: while the player can still steer (controls unlocked) keep the
		// oiled-lane hook disarmed, so only a committed aimed throw (which locks controls at launch and
		// sets HookAmount) ever curves.
		if ( !ControlsLocked ) HookAmount = 0f;

		// Preserve an aimed throw's revs through the long oiled front (so it still has spin to hook with on the
		// dry back end, and stays visibly spinning); the normal rolling damping would bleed ~a third of them
		// before the break. Friction does the rev-out, not damping. Restore normal damping otherwise.
		if ( _body.IsValid() )
			_body.AngularDamping = (ControlsLocked && MathF.Abs( HookAmount ) > 0.001f)
				? HookCoastAngularDamping : AngularDamping;

		// Camera-relative run direction on the horizontal plane. While controls are locked (the
		// ball has crossed the throw line) we read no input at all — wish stays zero.
		var move = ControlsLocked ? Vector3.Zero : Input.AnalogMove; // x = forward, y = left
		var wish = Vector3.Zero;
		var cam = Scene.Camera;
		if ( !ControlsLocked && cam.IsValid() )
		{
			var rot = cam.WorldRotation;
			var f = rot.Forward.WithZ( 0f );
			var l = rot.Left.WithZ( 0f );
			if ( f.Length > 0.001f ) f = f.Normal;
			if ( l.Length > 0.001f ) l = l.Normal;
			wish = f * move.x + l * move.y;
		}
		if ( wish.Length > 1f ) wish = wish.Normal;

		// --- Steer the ball ------------------------------------------------------------
		// wishDir names where you want to go; steer the ball's horizontal velocity toward
		// wishDir * MaxSpeed. With no input the target is zero, so the controller brakes
		// instead — the ball never gains speed on its own.
		//
		// Drive with a force at the ball's CENTRE, not a roll torque. A ball driven only by
		// roll torque is friction-limited to ~1 g of forward accel (any extra torque just
		// spins the contact out), which felt painfully slow. A centre force accelerates the
		// COM directly and lets ground friction spin the ball up to roll — far stronger,
		// friction-independent acceleration that still tapers smoothly toward MaxSpeed.
		var hasInput = wish.Length > 0.01f;
		var vel = _body.Velocity.WithZ( 0f );
		var targetVel = hasInput ? wish.Normal * (MaxSpeed * wish.Length.Clamp( 0f, 1f )) : Vector3.Zero;
		var velError = targetVel - vel;
		var gain = hasInput ? DriveGain : BrakeGain;
		// Locked = neither drive nor brake: the ball coasts on momentum alone into the pins.
		if ( !ControlsLocked )
			_body.ApplyForce( velError * gain );

		// Steering effort for the roll-rumble audio: the part of the correction perpendicular to
		// travel — i.e. how hard the player is TURNING, not just accelerating straight. Zero once
		// the ball is released (ControlsLocked), so the roll smooths out after the throw.
		if ( ControlsLocked || !hasInput )
		{
			SteerEffort01 = 0f;
		}
		else
		{
			var dir = vel.Length > 1f ? vel.Normal : (wish.Length > 0.01f ? wish.Normal : Vector3.Forward);
			var lateral = velError - velError.Dot( dir ) * dir;
			SteerEffort01 = (lateral.Length / (MaxSpeed * 0.5f)).Clamp( 0f, 1f );
		}

		// Terry rides CENTRED in the ball now (see PlaceTerry) — the old inner-wall climb is gone.

		// Pit catch: once the ball is behind the pin deck (out over the pit) bleed its speed hard, so it
		// dumps energy into the cushion and settles instead of ricocheting off the dead back wall and
		// flying back up the lane. Frame-rate-independent exponential decay; only post-throw (coasting).
		var lane = Lane.Of( this )?.Builder;
		if ( PitDamping > 0f && ControlsLocked && lane.IsValid() && WorldPosition.x > lane.PinDeckEndWorldX )
		{
			var k = MathF.Exp( -PitDamping * dt );
			_body.Velocity *= k;
			_body.AngularVelocity *= k;
		}

		// Oiled-lane hook — EMERGENT from the ball's real spin, not a scripted push. The side rotation the
		// bowler put on the ball (about its travel axis) makes its contact patch slip sideways over the lane.
		// The oiled front is slick so it slides straight; over the dry back end friction resists that slip and
		// drives the ball sideways — the hook — and it self-limits as the ball revs up to rolling on its new
		// line (the slip cancels). So the spin you see on the ball is exactly what breaks it. Only an aimed
		// throw arms this (HookAmount set at launch); a free-drive ball has no side rotation and keeps
		// HookAmount 0, so it never curves.
		if ( ControlsLocked && MathF.Abs( HookAmount ) > 0.001f && lane.IsValid() )
		{
			var horiz = _body.Velocity.WithZ( 0f );
			var speed = horiz.Length;
			if ( speed > 50f )
			{
				var foulX = lane.FoulLineWorldX;
				var deckX = lane.PinDeckWorldCenter.x;
				var span = MathF.Max( 1f, deckX - foulX );
				var p = ((WorldPosition.x - foulX) / span).Clamp( 0f, 1f ); // 0 at foul line, 1 at the deck
				// Oil pattern: no grip on the oiled front, ramps (smoothstep) to full over the dry back end.
				var front = OilFrontFraction.Clamp( 0f, 0.99f );
				var t = ((p - front) / MathF.Max( 0.01f, 1f - front )).Clamp( 0f, 1f );
				var oil = t * t * (3f - 2f * t);

				var dir = horiz / speed;
				var lateralDir = Vector3.Up.Cross( dir ); // unit, perpendicular to travel (+Y-ish for +X)

				// Sideways slip of the contact patch = lateral part of (ball travel + surface velocity from
				// spin). The surface velocity at the contact (ball bottom) is ω × r, r pointing straight down
				// at the radius — this is what the side rotation contributes, and what friction resists.
				var contactV = Vector3.Cross( _body.AngularVelocity, Vector3.Down * Radius );
				var lateralSlip = Vector3.Dot( horiz + contactV, lateralDir );

				// Kinetic friction opposing that slip at the full dry-lane grip limit (HookAccel·oil) — Coulomb,
				// so it grips hard the whole time the ball is sliding sideways and only quits once the ball has
				// revved up to rolling on its new line (slip inside the deadband). The deadband stops a tiny
				// residual slip from slamming full grip back and forth once it's set.
				var maxA = HookAccel * oil;
				var accel = MathF.Abs( lateralSlip ) < HookSlipDeadband
					? 0f
					: -MathF.Sign( lateralSlip ) * MathF.Min( maxA, MathF.Abs( lateralSlip ) / dt );
				var force = lateralDir * (accel * _body.Mass);
				// Apply at the CONTACT POINT (ball bottom), not the COM: the same friction that curves the path
				// also applies the torque that revs the side spin down — so the visible spin IS the hook and the
				// ball rolls out as it sets on its new line, instead of spinning on unchanged. This is what ties
				// the amount of spin you see to the amount of break you get.
				_body.ApplyForceAt( WorldPosition + Vector3.Down * Radius, force );

				// Throttled trace of the hook developing, for review (director's Log Throw checkbox): where
				// down-lane (x, progress p, oil grip), how fast, the actual sideways speed built up (vy — this
				// IS the hook), the side-spin rev-out (ω about travel), the spin's contact slip, and the accel.
				if ( (Director?.LogThrow ?? false) && Time.Now - _throwLogAt >= 0.06f )
				{
					_throwLogAt = Time.Now;
					var omegaSide = Vector3.Dot( _body.AngularVelocity, dir );      // spin about travel axis (revs)
					var contactLat = Vector3.Dot( contactV, lateralDir );           // slip the spin is producing
					Log.Info( $"[terryball][throw] p={p:F2} oil={oil:F2} x={WorldPosition.x:F0} y={WorldPosition.y:F0} " +
						$"spd={speed:F0} vy={_body.Velocity.y:F0} omegaSide={omegaSide:F1} " +
						$"contactLat={contactLat:F0} slip={lateralSlip:F0} accel={accel:F0}" );
				}
			}
		}

		// Keep the body awake while input is held or it's still moving.
		if ( hasInput || _body.Velocity.LengthSquared > 1f )
			_body.Sleeping = false;

		// DEBUG: trace the coast through the rack every physics step — speed vs position, so we can see
		// whether the ball steps down at each pin contact or sails through unaffected.
		if ( ControlsLocked && _body.IsValid() && (Director?.LogPinCrash ?? false) )
		{
			var v = _body.Velocity;
			Log.Info( $"[terryball][coast] x={WorldPosition.x:F0} y={WorldPosition.y:F0} spd={v.Length:F0} " +
				$"vx={v.x:F0} vy={v.y:F0} vz={v.z:F0} angVel={_body.AngularVelocity.Length:F1} mass={_body.Mass:F2}" );
		}
	}

	protected override void OnUpdate()
	{
		UpdateBowlerDress();

		PlaceTerry();

		// Drive Terry's locomotion from the BALL's speed (he's centred, so he barely moves himself). He
		// eases from a walk up to a run as the ball speeds up, then tucks into the Citizen's continuous Roll
		// animation once it's really moving. ramp01 maps 0..rollSpeed → walk..run; past that he rolls.
		if ( _anim.IsValid() )
		{
			var speed = _body.IsValid() ? _body.Velocity.WithZ( 0f ).Length : 0f;
			// Roll threshold expressed in the HUD's bowling-scale mph, so "11.5 mph and up" means what the
			// gauge says (and survives a Radius change). Same mph mapping as the ShowSpeed readout below.
			var rollSpeed = MphToUnits( RollAtMph );
			var rolling = speed >= rollSpeed;
			var ramp01 = rollSpeed > 0f ? (speed / rollSpeed).Clamp( 0f, 1f ) : 0f;

			// Flip the roll state the instant the speed crosses the threshold and open a TransitionTime
			// window. The engine crossfades the pose itself, starting now — we hold the playback rate down
			// across this window (below) so that crossfade plays at a visible speed instead of being
			// fast-forwarded by the fast run/roll cadence rate.
			if ( rolling != _wasRolling )
			{
				_transitionT = TransitionTime;
				_wasRolling = rolling;
			}
			_transitionT = MathF.Max( 0f, _transitionT - Time.Delta );

			_anim.SpecialMove = rolling
				? CitizenAnimationHelper.SpecialMoveStyle.Roll
				: CitizenAnimationHelper.SpecialMoveStyle.None;

			// Drive playback rate directly: the move-speed cadence alone tops out at a jog (way too slow
			// against a fast ball). His legs hold at WalkPlaybackRate over the first WalkRampStart of the
			// run-up, then ramp WalkPlaybackRate→RunPlaybackRate the rest of the way; the settled roll plays
			// at RollPlaybackRate.
			if ( _terryRenderer.IsValid() )
			{
				var walkT = ((ramp01 - WalkRampStart) / MathF.Max( 1f - WalkRampStart, 0.0001f )).Clamp( 0f, 1f );
				var runRate = MathX.Lerp( WalkPlaybackRate, RunPlaybackRate, walkT );
				var settledRate = rolling ? RollPlaybackRate : runRate;

				// PlaybackRate scales the animgraph's OWN pose crossfade too (animNative.Update advances
				// transitions by delta×rate), so the fast cadence rate would snap the swap. inWindow is 1 at
				// the start of the transition — when the crossfade fires — and eases to 0 as the window
				// closes, so we hold the rate down at TransitionPlaybackRate (≈1×) while the pose crossfades,
				// then ramp back up to the fast settled cadence. Slow window lasts exactly TransitionTime.
				var inWindow = TransitionTime > 0f ? (_transitionT / TransitionTime).Clamp( 0f, 1f ) : 0f;
				_terryRenderer.PlaybackRate = MathX.Lerp( settledRate, TransitionPlaybackRate, inWindow );
			}

			// Feed his legs a ramped speed (Auto move-style picks walk vs run from it), facing travel so the
			// run/roll go the way the ball's rolling. Capped at RunTopSpeed (~9.5 mph) right as the roll takes over.
			_anim.WithVelocity( _faceDir * (RunTopSpeed * ramp01) );
		}

		if ( ShowSpeed && _body.IsValid() )
		{
			// Speed in units, plus a real-world mph treating the ball's diameter as RealBallInches.
			var v = _body.Velocity.Length;
			DebugOverlay.Text( WorldPosition + Vector3.Up * (Radius + 90f),
				$"{UnitsToMph( v ):F1} mph   ({v:F0} u/s)", 24, TextFlag.Center, Color.White );
		}
	}

	// Bowling-scale mph ↔ units, treating the ball's diameter as RealBallInches (1 mph = 17.6 in/s). The
	// HUD readout and the roll threshold both go through these so "11.5 mph" means exactly what's on screen.
	private float UnitsToMph( float units ) => units * (RealBallInches / (Radius * 2f)) / 17.6f;
	private float MphToUnits( float mph ) => mph * 17.6f * (Radius * 2f) / RealBallInches;

	// Hang Terry CENTRED in the ball, upright, facing the way it's travelling. The run/roll motion is all
	// in the animation (see OnUpdate) — his root just faces travel and sits on the ball centre.
	private void PlaceTerry()
	{
		if ( !_terry.IsValid() ) return;

		// Always face the direction the ball is actually rolling ("if he can" — keep the last heading while
		// it's nearly stationary so he doesn't spin in place). The Citizen's Roll animation rolls forward
		// along this facing, so facing travel keeps the roll going the right way.
		var travel = (_body.IsValid() ? _body.Velocity : Vector3.Zero).WithZ( 0f );
		if ( travel.Length > 5f )
			_faceDir = travel.Normal;
		var fwd = _faceDir.Length > 0.001f ? _faceDir.Normal : Vector3.Forward;

		_terry.WorldRotation = Rotation.LookAt( fwd, Vector3.Up );

		// Hang his PELVIS on the ball centre, not his feet. His origin is at his feet, but each clip (walk,
		// run, the curled roll) holds the pelvis a different height above that origin — pinning the feet let
		// that per-clip difference bob his whole body up/down through the transitions. So we read the pelvis's
		// current height above the origin and offset by THAT, which cancels the shift and keeps his middle
		// framed dead-centre regardless of which animation is playing. (Falls back to the static half-height
		// if the bone isn't resolvable yet.)
		var pelvisUp = _bodyCentreUp;
		if ( _terryRenderer.IsValid() && _terryRenderer.TryGetBoneTransformLocal( "pelvis", out var pelvisLocal ) )
			pelvisUp = pelvisLocal.Position.z * TerryScale;
		_terry.WorldPosition = WorldPosition - Vector3.Up * pelvisUp;
	}

	// DEBUG: log every ball↔pin contact and its impulse. This is the crux diagnostic — if the ball
	// "isn't affected" by the pins, either no contacts fire (contact generation failing) or they fire
	// with near-zero Impulse. Both are visible here. NormalSpeed = closing speed along the contact
	// normal; Impulse = the actual momentum exchanged at the contact.
	public void OnCollisionStart( Collision c )
	{
		RecordPinContact( c );
		LogPinContact( "START", c );
	}
	public void OnCollisionUpdate( Collision c ) => LogPinContact( "UPDATE", c );

	// Which pins THIS ball has directly touched this throw — used to tell a pocket hit (drove between
	// the 1 and the 2/3) from a Brooklyn/high hit. Cleared each ball by the director (ResetContacts).
	private readonly HashSet<int> _contacts = new();

	private void RecordPinContact( Collision c )
	{
		var pin = c.Other.GameObject.IsValid() ? c.Other.GameObject.Components.Get<Pin>() : null;
		if ( pin.IsValid() ) _contacts.Add( pin.Number );
	}

	/// <summary>True if this throw drove the pocket: the ball directly contacted the head pin (1) AND
	/// the 2 or 3 (either side of the pocket). A flush pocket strike, not a high hit or a wide miss.</summary>
	public bool PocketHit => _contacts.Contains( 1 ) && (_contacts.Contains( 2 ) || _contacts.Contains( 3 ));

	/// <summary>Clear the recorded pin contacts so the next throw starts fresh.</summary>
	public void ResetContacts() => _contacts.Clear();

	private void LogPinContact( string phase, Collision c )
	{
		if ( !(Director?.LogPinCrash ?? false) ) return; // gated behind the director's Debug checkbox

		var other = c.Other.GameObject;
		if ( !other.IsValid() || !other.Name.StartsWith( "Pin" ) ) return;

		var pinBody = c.Other.Body;
		var con = c.Contact;
		Log.Info( $"[terryball][contact:{phase}] {other.Name} impulse={con.Impulse:F1} normalSpd={con.NormalSpeed:F0} " +
			$"ballSpd={(_body.IsValid() ? _body.Velocity.Length : 0):F0} ballMass={(_body.IsValid() ? _body.Mass : 0):F2} " +
			$"pinSpd={(pinBody.IsValid() ? pinBody.Velocity.Length : 0):F0} pinMass={(pinBody.IsValid() ? pinBody.Mass : 0):F2} " +
			$"normal={con.Normal} pt={con.Point}" );
	}
}
