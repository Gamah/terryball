using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Applies this client's SKY BRIGHTNESS choice (<see cref="WorldPrefs.SkyBrightness"/>, set at the SETTINGS
/// pedestal) to the scene's daylight: the Sun's <see cref="DirectionalLight"/> — both its light colour and
/// its sky/ambient term — and the 2D skybox's tint, all scaled by the same factor so the alley dims as one
/// thing instead of leaving a bright sky over dark wood.
///
/// <para>Modelled on gambit's ROOM LIGHT BRIGHTNESS (its <c>RoomLightOrbit.ApplyBrightness</c>): the
/// AUTHORED colours are captured once and every apply is base × scale, never scale × current, so dragging
/// the slider can't ratchet the lighting down a step at a time.</para>
///
/// <para>Client-local by construction. This lives on the <see cref="LocalHud"/> (NetworkMode.Never) and the
/// lights it writes to are authored scene objects, which each client owns its own copy of — nothing here
/// replicates, so turning your sky down does not turn anybody else's down.</para>
/// </summary>
public sealed class SkyBrightnessApplier : Component
{
	// The authored lighting, captured the first time we see each component. Keyed by component so a scene
	// with more than one sun (or a skybox swapped at runtime) still bases off what was authored for it.
	private readonly Dictionary<DirectionalLight, (Color Light, Color Sky)> _baseSun = new();
	private readonly Dictionary<SkyBox2D, Color> _baseSky = new();

	// Last WorldPrefs.Revision we applied. -1 so the first frame always applies, even on a fresh cookie.
	private int _appliedRevision = -1;

	// The alley builds itself over several frames, so a light can appear after our first apply and would
	// otherwise stay at authored brightness forever. Sweeping the whole scene every frame to catch that is
	// not worth it (this scene is thousands of objects), so we re-sweep on a slow timer as well as on change.
	private RealTimeSince _sinceSweep = 999f;
	private const float SweepInterval = 0.5f;

	protected override void OnUpdate()
	{
		if ( Scene is null ) return;
		if ( WorldPrefs.Revision == _appliedRevision && _sinceSweep < SweepInterval ) return;
		_appliedRevision = WorldPrefs.Revision;
		_sinceSweep = 0f;

		var suns = Scene.GetAllComponents<DirectionalLight>().Where( l => l.IsValid() );
		var skies = Scene.GetAllComponents<SkyBox2D>().Where( s => s.IsValid() );
		var scale = WorldPrefs.SkyBrightness;

		foreach ( var sun in suns )
		{
			if ( !_baseSun.TryGetValue( sun, out var authored ) )
			{
				authored = (sun.LightColor, sun.SkyColor);
				_baseSun[sun] = authored;
			}

			sun.LightColor = Scaled( authored.Light, scale );
			sun.SkyColor = Scaled( authored.Sky, scale );
		}

		foreach ( var sky in skies )
		{
			if ( !_baseSky.TryGetValue( sky, out var authored ) )
			{
				authored = sky.Tint;
				_baseSky[sky] = authored;
			}

			sky.Tint = Scaled( authored, scale );
		}
	}

	// Scale the RGB and leave alpha alone — alpha on these is a blend weight, not a brightness.
	private static Color Scaled( Color c, float s ) => new( c.r * s, c.g * s, c.b * s, c.a );
}
