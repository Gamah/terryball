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
/// pin-deck-sized room. <see cref="Wet"/> and <see cref="DryLevel"/> set the reverb and completely-dry
/// levels INDEPENDENTLY (played in parallel), not as a coupled blend.
/// </summary>
[Icon( "graphic_eq" )]
public sealed class AlleyReverb : Component
{
	/// <summary>The dedicated mixer pin sounds route through and the reverb inserts on — a child of "Game".</summary>
	public const string PinMixerName = "Pins";

	/// <summary>DSP reverb preset (see <see cref="Sound.DspNames"/>). Diffuse + no delay line = a smooth tail,
	/// no echoes; a short-decay small preset keeps it pin-deck sized.</summary>
	[Property] public string Preset { get; set; } = "room.diffuse.small";

	/// <summary>Wet (reverberated) level. The DSP processor's <see cref="AudioProcessor.Mix"/> is a dry/wet
	/// CROSSFADE (out = (1-Mix)*dry + Mix*wet), which couples the two — turning up wet turns down dry. We want
	/// them independent, so this is the ABSOLUTE wet contribution and <see cref="DryLevel"/> the absolute dry;
	/// <see cref="ApplyLevels"/> back-solves the mixer volume + crossfade to hit both (see there).</summary>
	[Property, Range( 0f, 1f )] public float Wet { get; set; } = 0.225f;

	/// <summary>Absolute level of the COMPLETELY DRY pin signal, played in parallel with the <see cref="Wet"/>
	/// reverb. This is the untouched crossfade passthrough (same voice, so no flam vs a 2nd Sound.Play), so the
	/// clean clack keeps its presence/punch under the tail. Wet + DryLevel must stay <= 1 (mixer volume clamps).</summary>
	[Property, Range( 0f, 1f )] public float DryLevel { get; set; } = 0.5f;

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
		_processor = new DspProcessor( Preset );
		_mixer.AddProcessor( _processor );
		ApplyLevels();
	}

	private void Detach()
	{
		if ( _processor is not null )
			_mixer?.RemoveProcessor( _processor );
		_processor = null;
		if ( _mixer is not null ) _mixer.Volume = 1f; // reverb off -> pins play back at full, fully dry
	}

	// The mixer crossfade gives dryLeg = Volume*(1-Mix), wetLeg = Volume*Mix. Solve for Volume & Mix that
	// land dryLeg == DryLevel and wetLeg == Wet: Volume = DryLevel + Wet, Mix = Wet / (DryLevel + Wet).
	// So the completely-dry pin sits at DryLevel and the reverb at Wet, independently.
	private void ApplyLevels()
	{
		if ( _processor is null || _mixer is null ) return;
		float sum = DryLevel + Wet;
		_mixer.Volume = sum; // clamped to [0,1] by the mixer; keep DryLevel + Wet <= 1
		_processor.Mix = sum > 0f ? Wet / sum : 0f;
	}

	// Pick up the mixer once the audio graph is up, and apply live inspector tweaks.
	protected override void OnUpdate()
	{
		if ( _processor is null ) { Attach(); return; }
		if ( !string.Equals( _processor.Effect.Name, Preset, StringComparison.OrdinalIgnoreCase ) ) { Attach(); return; }
		ApplyLevels();
	}
}
