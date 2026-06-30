using Sandbox;
using System.Collections.Generic;

namespace Terryball;

/// <summary>
/// The lane's announcer voice-over, on the lane root (one bank per lane, like
/// <see cref="PinImpactAudio"/>). The <see cref="GameDirector"/> classifies each settled ball into a
/// call id (strike / spare / split / turkey / so-close / a spoken pin count, …) and routes it here.
///
/// Each call has two recorded takes under <c>sounds/announcer/fx/&lt;call&gt;_0.wav</c> and <c>_1.wav</c>
/// (the processed "PA in a big room" set built by <c>process_fx.py</c>); both takes go into one
/// <see cref="SoundEvent"/> so s&amp;box randomizes which one plays and the announcer never sounds
/// identical twice. We build the events in C# from the raw <c>.wav</c> via <see cref="SoundFile.Load"/>
/// (there are no <c>.sound</c> assets in the project), mirroring <see cref="BallAudio"/>.
///
/// Playback is 2D (a clear PA, not a muffled positional source). The director gates playback to the
/// LOCAL player's lane before calling <see cref="Play"/>, so with multiple lanes a client only ever
/// hears its own lane's announcer — the call can't carry across the alley.
/// </summary>
public sealed class AnnouncerAudio : Component
{
	[Property] public float Volume { get; set; } = 1.0f;

	// Every call id the director can ask for — the filename stems under announcer/fx (each with a
	// _0 and _1 take). Numbers are the spoken pin count; the rest are results / streaks / reactions.
	private static readonly string[] Calls =
	{
		"one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
		"strike", "spare", "gutter", "miss", "open-frame",
		"split", "pocket",
		"double", "turkey", "four-bagger", "five-bagger", "six-pack",
		"nice", "so-close", "good-try",
		"perfect-game",
	};

	private readonly Dictionary<string, SoundEvent> _events = new();

	protected override void OnEnabled()
	{
		_events.Clear();
		foreach ( var call in Calls )
		{
			var sounds = new List<SoundFile>();
			for ( var take = 0; take < 2; take++ )
			{
				var sf = SoundFile.Load( $"sounds/announcer/fx/{call}_{take}.wav" );
				if ( sf != null ) sounds.Add( sf );
			}
			if ( sounds.Count == 0 ) continue;
			_events[call] = new SoundEvent { Sounds = sounds, Volume = 1.0f };
		}
	}

	/// <summary>Play the announcer line for <paramref name="callId"/> as a 2D PA cue. No-op for an
	/// unknown id or a clip that failed to load.</summary>
	public void Play( string callId )
	{
		if ( string.IsNullOrEmpty( callId ) || !_events.TryGetValue( callId, out var ev ) ) return;
		var h = Sound.Play( ev );
		if ( !h.IsValid() ) return;
		h.SpacialBlend = 0f; // force 2D — a clear PA regardless of camera position
		h.Volume = Volume;
	}
}
