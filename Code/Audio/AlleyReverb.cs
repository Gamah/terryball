using Sandbox;
using Sandbox.Audio;
using System;

namespace Terryball;

/// <summary>
/// Gives the PINS an in-engine reverb tail without wetting anything else. Pin impacts route to a dedicated
/// "Pins" mixer (a child of the "Game" SFX bus, so they still follow the game volume) and this inserts a
/// DSP reverb onto THAT mixer only — so the ball roll, announcer, UI and music all stay dry on their own
/// buses. (Earlier this sat on "Game" and reverbed everything, including the ball roll.)
///
/// We insert the <see cref="DspProcessor"/> directly rather than via a <see cref="DspVolume"/>: the volume
/// path is listener-gated and hard-disabled in the editor. Reverb is local audio DSP and the mixer graph is
/// per-client, so one of these (on the World object) wires up that client's pins reverb; each client runs
/// its own.
///
/// <see cref="Preset"/> is a <see cref="Sound.DspNames"/> name — use a diffuse, reverb-only preset (a
/// diffusor + reverb, NO delay line) so the tail is smooth with no fluttery repeated echoes; AVOID the
/// "cavern.*"/"outside.*" presets (their feedback delay line IS the echo). "room.diffuse.small" is a tight
/// pin-deck-sized room. <see cref="Wet"/> is the dry/wet blend.
/// </summary>
[Icon( "graphic_eq" )]
public sealed class AlleyReverb : Component
{
	/// <summary>The dedicated mixer pin sounds route through and the reverb inserts on — a child of "Game".</summary>
	public const string PinMixerName = "Pins";

	/// <summary>DSP reverb preset (see <see cref="Sound.DspNames"/>). Diffuse + no delay line = a smooth tail,
	/// no echoes; a short-decay small preset keeps it pin-deck sized.</summary>
	[Property] public string Preset { get; set; } = "room.diffuse.small";

	/// <summary>Dry/wet blend on the pins bus. Lower = drier / feels smaller and further back.</summary>
	[Property, Range( 0f, 1f )] public float Wet { get; set; } = 0.3f;

	private DspProcessor _processor;
	private Mixer _mixer;

	/// <summary>Get — creating it once, as a child of the "Game" bus — the dedicated pins mixer. Shared by
	/// <see cref="PinImpactAudio"/> (which routes pin sounds here) and by this reverb (which inserts onto it),
	/// so pin impacts get the tail while the ball roll and everything else stay dry.</summary>
	public static Mixer EnsurePinMixer()
	{
		var m = Mixer.FindMixerByName( PinMixerName );
		if ( m is not null ) return m;

		var parent = Mixer.FindMixerByName( "Game" ) ?? Mixer.Master;
		if ( parent is null ) return null; // audio graph not up yet

		m = parent.AddChild();
		m.Name = PinMixerName;
		return m;
	}

	protected override void OnEnabled() => Attach();
	protected override void OnDisabled() => Detach();

	private void Attach()
	{
		Detach();
		_mixer = EnsurePinMixer();
		if ( _mixer is null ) return; // graph not up yet — OnUpdate retries
		_processor = new DspProcessor( Preset ) { Mix = Wet };
		_mixer.AddProcessor( _processor );
	}

	private void Detach()
	{
		if ( _processor is not null )
			_mixer?.RemoveProcessor( _processor );
		_processor = null;
	}

	// Pick up the mixer once the audio graph is up, and apply live inspector tweaks.
	protected override void OnUpdate()
	{
		if ( _processor is null ) { Attach(); return; }
		if ( !string.Equals( _processor.Effect.Name, Preset, StringComparison.OrdinalIgnoreCase ) ) { Attach(); return; }
		if ( _processor.Mix != Wet ) _processor.Mix = Wet;
	}
}
