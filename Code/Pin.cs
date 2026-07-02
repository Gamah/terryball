using Sandbox;

namespace Terryball;

/// <summary>
/// One of a lane's ten pins, as a single NETWORKED object. The pin's GameObject (transform +
/// Rigidbody) is spawned once by the host and replicated to every client; whoever is bowling owns all
/// ten for the duration of their turn (the director hands ownership over at <c>BeginTurn</c>), so the
/// active bowler simulates the whole crash locally and the engine streams the result — reliable,
/// interpolated, no hand-rolled transform broadcast. Spectators (the host included) see the one shared
/// rack, not a private local copy slammed to ~20 Hz samples.
///
/// Spawned at the SCENE ROOT (not parented into the lane subtree): a networked object parented under a
/// non-networked scene object doesn't replicate cleanly to late-joiners, so the pin finds its lane via
/// <see cref="Lane.Local"/> instead and registers with that lane's builder.
///
/// The catch: the render/collision MESH is the lane's runtime-lathed PROCEDURAL model, and procedural
/// models do NOT replicate across the network (they aren't serialisable). So a freshly-replicated pin
/// arrives on a proxy with a null model and renders as the purple ERROR checker. The fix is here: every
/// client re-attaches its OWN local copy of that identical, deterministic model in <see cref="OnStart"/>
/// via <see cref="LaneBuilder.ConfigurePin"/>. The object is networked; the mesh is rebuilt locally.
///
/// --- Re-rack animation (fade + glide) ---------------------------------------------------------------
/// The pin owns its own respot presentation, driven by the synced <see cref="Phase"/> the director/bowler
/// sets. A knocked pin FADES OUT where it fell before it's hidden off the deck; a re-racked pin FADES IN
/// and GLIDES (kinematically) back onto its spot from wherever it lies. Because it's all keyed off the
/// SYNCED phase (+ the glide's start pose), every client plays the same animation — and if the rack is
/// still gliding when the next bowler grabs ownership of it, the new owner just carries the glide on to
/// completion instead of freezing it mid-air. Only the owner touches the transform/physics; every client
/// animates the tint.
/// </summary>
public sealed class Pin : Component
{
	/// <summary>This pin's number, 1 (head) to 10. Host-set at spawn and synced, so every client orders
	/// the rack identically for the director's scoring and respotting.</summary>
	[Sync] public int Number { get; set; }

	/// <summary>The world spot this pin racks on. Host-set at spawn; the director respots the pin here
	/// between balls. Synced so the spot is identical on every client.</summary>
	[Sync] public Vector3 Home { get; set; }

	/// <summary>The id of the <see cref="Lane"/> root this pin belongs to. Host-set at spawn and synced,
	/// so a root-spawned pin (which can't resolve its lane by parent) registers with the RIGHT lane's
	/// builder — essential once there's more than one lane, or lane 2's pins would attach to lane 1.</summary>
	[Sync] public System.Guid LaneId { get; set; }

	/// <summary>How a pin is currently presenting itself, set by the rack owner and synced so every
	/// client plays the matching animation:
	///   Up      — settled and standing on the deck (normal physics).
	///   Down    — knocked off the deck: fades out where it fell, then the owner disables the GameObject.
	///   Racking — being respotted: fades in (if it was hidden) and glides kinematically onto its spot.</summary>
	public enum PinPhase { Up, Down, Racking }
	[Sync] public PinPhase Phase { get; set; } = PinPhase.Up;

	/// <summary>The pose the current glide (<see cref="PinPhase.Racking"/>) started from — synced so a
	/// client that inherits ownership mid-glide can finish the lerp toward <see cref="Home"/>.</summary>
	[Sync] public Vector3 RackFrom { get; set; }
	[Sync] public Rotation RackFromRot { get; set; }
	/// <summary>True when the current glide is REVEALING a hidden pin (fade its tint in from 0); false when
	/// it's just recentering an already-visible standing pin (which must NOT blink out and back).</summary>
	[Sync] public bool RackFadeIn { get; set; }

	/// <summary>Seconds to hold before this pin's glide/fade-in begins, so the director can stagger the rack
	/// into a wave rather than every pin landing at once. Synced so the cascade plays identically on every
	/// client; the pin holds hidden (or at its fallen pose) until the delay elapses.</summary>
	[Sync] public float RackDelay { get; set; }

	/// <summary>Seconds a knocked pin takes to fade out before it's swept off the deck.</summary>
	public const float FadeOutSeconds = 0.35f;
	/// <summary>Seconds a re-racked pin takes to glide onto its spot (and, if revealed, fade in over).</summary>
	public const float RackSeconds = 0.5f;

	private LaneBuilder _builder;
	private ModelRenderer _mr;
	private Rigidbody _rb;
	private Color _baseTint = Color.White; // the opaque pin tint ConfigurePin set; fades multiply this

	private PinPhase _animPhase = PinPhase.Up; // the phase our local animation is currently playing
	private float _animElapsed;                // seconds into the current fade/glide

	// "Do I simulate this pin?" — true if I own it (or there's no live session, e.g. the editor).
	private bool IsSimOwner => !Networking.IsActive || !GameObject.Network.Active || GameObject.Network.IsOwner;

	protected override void OnStart()
	{
		// Resolve our lane by the synced LaneId (we're root-spawned, so we can't go by parent) and register
		// so the director can find us even while we're swept off the deck (disabled) — a scene-wide
		// component scan skips disabled objects, which would lose swept pins and break the re-rack. Fall
		// back to the single local lane for the pre-multi-lane case / editor.
		_builder = (Lane.ById( Scene, LaneId ) ?? Lane.Of( this ) ?? Lane.Local( Scene ))?.Builder;
		_builder?.ConfigurePin( this ); // attach the local procedural mesh + physics (re-skins proxies)
		_builder?.RegisterPin( this );

		_mr = Components.Get<ModelRenderer>();
		_rb = Components.Get<Rigidbody>();
		if ( _mr.IsValid() ) _baseTint = _mr.Tint; // ConfigurePin set the solid pin colour; remember it
		_animPhase = Phase;
	}

	protected override void OnDestroy() => _builder?.UnregisterPin( this );

	// --- Owner-authoritative rack commands (called by the director/bowler that owns the rack) -----------

	/// <summary>Knock this pin off the deck: it fades out where it lies, then (on the owner) the GameObject
	/// is disabled once the fade finishes — leaving it exactly where it fell so a later
	/// <see cref="RackTo"/> can fade it back in from that spot.</summary>
	public void Sweep()
	{
		if ( Phase == PinPhase.Down ) return;
		Phase = PinPhase.Down; // triggers the fade-out everywhere; the owner disables when it completes
	}

	/// <summary>Bring this pin onto <paramref name="home"/>. A hidden pin is re-enabled and fades in while
	/// gliding up from where it fell; a standing-but-nudged pin just glides back onto its spot (no fade).
	/// <paramref name="instant"/> snaps with no animation — for the lobby / first placement.</summary>
	public void RackTo( Vector3 home, bool instant = false, float delay = 0f )
	{
		Home = home;

		if ( instant )
		{
			if ( !GameObject.Enabled ) GameObject.Enabled = true;
			Phase = PinPhase.Up;
			_animPhase = PinPhase.Up;
			SnapHome();
			return;
		}

		var wasHidden = !GameObject.Enabled;
		if ( wasHidden ) GameObject.Enabled = true; // re-enable the swept pin (replicates); it's still at its fallen pose

		RackDelay = delay < 0f ? 0f : delay;        // hold this long before the glide begins (staggered wave)
		RackFadeIn = wasHidden;                     // reveal fades tint in; a visible standing pin does not
		RackFrom = WorldPosition;
		RackFromRot = WorldRotation;
		Phase = PinPhase.Racking;                   // fade-in (if revealed) + glide to Home on every client
	}

	// --- Animation (runs on every client) --------------------------------------------------------------

	protected override void OnUpdate()
	{
		// A synced phase change (re)starts the matching local animation. Owner-only bits set up the glide.
		if ( _animPhase != Phase )
		{
			_animPhase = Phase;
			_animElapsed = 0f;

			switch ( Phase )
			{
				case PinPhase.Racking:
					if ( RackFadeIn ) SetAlpha( 0f );        // revealed pin fades in from invisible
					break;
				case PinPhase.Up:
					SetAlpha( 1f );
					break;
				// Down: keep it visible and let it lie where it fell; the fade runs below.
			}
		}

		_animElapsed += Time.Delta;

		switch ( Phase )
		{
			case PinPhase.Down:
			{
				var t = (_animElapsed / FadeOutSeconds).Clamp( 0f, 1f );
				SetAlpha( 1f - t );
				if ( t >= 1f && IsSimOwner && GameObject.Enabled )
					GameObject.Enabled = false; // off the deck (replicates); it rests where it fell
				break;
			}

			case PinPhase.Racking:
			{
				// Hold at the fallen pose (and invisible, if revealing) until this pin's stagger delay elapses,
				// then run the glide over RackSeconds — so the deck fills in a wave rather than all at once.
				var t = ((_animElapsed - RackDelay) / RackSeconds).Clamp( 0f, 1f );
				if ( RackFadeIn ) SetAlpha( t );

				if ( IsSimOwner )
				{
					SetKinematic( true ); // assert every frame: an owner that INHERITED this glide mid-flight
					                      // (the next bowler grabbed the rack) never saw the phase-change frame
					var e = EaseInOut( t );
					WorldPosition = Vector3.Lerp( RackFrom, Home, e );
					WorldRotation = Rotation.Lerp( RackFromRot, Rotation.Identity, e );

					if ( t >= 1f )
					{
						SetKinematic( false ); // hand it back to physics, settled on its spot
						ZeroVelocity();
						SetAlpha( 1f );
						Phase = PinPhase.Up;
						_animPhase = PinPhase.Up;
					}
				}
				break;
			}
		}
	}

	// Snap straight onto the spot, upright, opaque and dead still (instant rack).
	private void SnapHome()
	{
		WorldPosition = Home;
		WorldRotation = Rotation.Identity;
		SetKinematic( false );
		ZeroVelocity();
		SetAlpha( 1f );
	}

	// Set the render alpha and keep the object in the right pass: translucent while fading, opaque (so it
	// takes shadows again) once solid.
	private void SetAlpha( float a )
	{
		if ( !_mr.IsValid() ) return;
		var solid = a >= 0.999f;
		if ( _mr.SceneObject.IsValid() ) _mr.SceneObject.Flags.IsTranslucent = !solid;
		_mr.Tint = _baseTint.WithAlpha( a );
	}

	private void SetKinematic( bool kinematic )
	{
		if ( _rb.IsValid() ) _rb.MotionEnabled = !kinematic;
	}

	private void ZeroVelocity()
	{
		if ( !_rb.IsValid() ) return;
		_rb.Velocity = Vector3.Zero;
		_rb.AngularVelocity = Vector3.Zero;
	}

	private static float EaseInOut( float t ) => t * t * (3f - 2f * t); // smoothstep
}
