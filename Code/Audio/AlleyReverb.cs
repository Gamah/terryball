using Sandbox;
using Sandbox.Audio;
using System;

namespace Terryball;

/// <summary>
/// Gives the whole alley an in-engine reverb tail by inserting a DSP reverb straight onto the target
/// mixer (default "Game" — the SFX bus the pins, ball roll and announcer play through; "Music" is a
/// separate mixer, so it stays dry and clean).
///
/// We add the <see cref="DspProcessor"/> to the mixer DIRECTLY rather than through a <see cref="DspVolume"/>:
/// the volume path only engages while the listener sits inside a registered volume AND is hard-disabled in
/// the editor — which is why an earlier DspVolume version was silent even in a played instance. Reverb is
/// local audio DSP, and the mixer graph is per-client global, so ONE of these (on the World object) reverbs
/// that client's whole SFX bus; each client runs its own.
///
/// <see cref="Preset"/> is a <see cref="Sound.DspNames"/> name. Use a diffuse, reverb-only preset (a
/// diffusor + reverb, NO delay line) so the tail is a smooth boomy wash with no fluttery repeated echoes
/// — AVOID the "cavern.*" and "outside.*" presets, whose feedback DELAY line is the echo source. Pick by
/// SIZE of space: "concrete.small" ≈ a tight pin-deck-sized room (small 50/20 ms, dark 4 kHz); step up
/// through "concrete.large" → "room.diffuse.huge" for progressively bigger halls.
/// <see cref="Wet"/> is the dry/wet blend (1 = fully wet). Lower it to make the space feel smaller/drier.
/// </summary>
[Icon( "graphic_eq" )]
public sealed class AlleyReverb : Component
{
	/// <summary>DSP reverb preset name (see <see cref="Sound.DspNames"/>). Diffuse + no delay line = a
	/// smooth boomy tail with no repeated echoes; small presets keep the space tight (pin-deck sized).</summary>
	[Property] public string Preset { get; set; } = "concrete.small";

	/// <summary>Mixer the reverb inserts on. "Game" is the SFX bus (pins/ball/announcer); "Music" is separate.</summary>
	[Property] public string MixerName { get; set; } = "Game";

	/// <summary>Dry/wet blend of the reverb on the bus. 1 = fully wet (cranked); dial down for subtlety.</summary>
	[Property, Range( 0f, 1f )] public float Wet { get; set; } = 1f;

	private DspProcessor _processor;
	private Mixer _mixer;

	protected override void OnEnabled() => Attach();
	protected override void OnDisabled() => Detach();

	private void Attach()
	{
		Detach();
		_mixer = Mixer.FindMixerByName( MixerName );
		if ( _mixer is null ) return; // audio graph not up yet — OnUpdate retries
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

		bool presetChanged = !string.Equals( _processor.Effect.Name, Preset, StringComparison.OrdinalIgnoreCase );
		if ( presetChanged || !string.Equals( _mixer?.Name, MixerName, StringComparison.OrdinalIgnoreCase ) )
		{
			Attach();
			return;
		}

		if ( _processor.Mix != Wet ) _processor.Mix = Wet;
	}
}
