using Sandbox;
using Sandbox.Audio;
using Sandbox.Volumes;

namespace Terryball;

/// <summary>
/// Gives the whole alley an in-engine reverb tail. Rather than baking room ambience into every sample,
/// we let s&amp;box's DSP reverb do it live: this drops an <b>infinite</b> <see cref="DspVolume"/> that
/// runs a named reverb preset over everything routed to <see cref="MixerName"/> — the pins' clacks, the
/// ball roll, the announcer. Music sits on its own "Music" mixer, so it stays dry and clean.
///
/// Reverb is purely LOCAL audio DSP (it's applied on each listener's machine, not networked state), so
/// every client just runs its own copy — nothing to replicate. Building the DspVolume in code (vs.
/// authoring it in the scene) keeps the nested volume/mixer/preset config in one tunable place.
///
/// Preset is a <see cref="Sound.DspNames"/> name. Good alley candidates: "brite.large" (lively, hard
/// tiled surfaces), "concrete.large" (harder/darker), "room.diffuse.huge" (big, even, natural). Try a
/// few live in the inspector and pick by ear.
/// </summary>
[Icon( "graphic_eq" )]
public sealed class AlleyReverb : Component
{
	/// <summary>DSP reverb preset name (see <see cref="Sound.DspNames"/>). Change live to audition.</summary>
	[Property] public string Preset { get; set; } = "brite.large";

	/// <summary>Which mixer the reverb applies to. "Game" catches SFX (pins/ball/announcer) but not the
	/// separately-routed "Music", so music stays dry.</summary>
	[Property] public string MixerName { get; set; } = "Game";

	private DspVolume _dsp;

	protected override void OnEnabled()
	{
		_dsp = Components.GetOrCreate<DspVolume>();
		_dsp.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Infinite };
		_dsp.Dsp = Preset;
		_dsp.TargetMixer = new MixerHandle { Name = MixerName };
	}

	// Let inspector tweaks to the preset/mixer take effect without a scene reload.
	protected override void OnUpdate()
	{
		if ( !_dsp.IsValid() ) return;
		if ( _dsp.Dsp.Name != Preset ) _dsp.Dsp = Preset;
		if ( _dsp.TargetMixer.Name != MixerName ) _dsp.TargetMixer = new MixerHandle { Name = MixerName };
	}

	protected override void OnDisabled() => _dsp?.Destroy();
}
