using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Terryball;

/// <summary>
/// Client-local, <see cref="Cookie"/>-backed voice-chat preferences. Purely per-user state that never
/// touches the network: the master on/off toggle, the set of players this user has muted (by SteamID),
/// and whether they've seen the first-run help. Every getter reads its cookie and every setter writes it
/// straight back, so choices persist across sessions the moment they change (the engine flushes the
/// cookie store to disk on its own timer / shutdown).
///
/// This is the local side of proximity voice: <see cref="TerryVoice.ShouldHearVoice"/> reads
/// <see cref="VoiceEnabled"/> + <see cref="IsMuted"/> on the RECEIVER to decide who it hears, and
/// <see cref="VoiceScreen"/> reads/writes these from the keyboard.
/// </summary>
public static class VoicePrefs
{
	private const string EnabledKey = "terryball.voice.enabled";
	private const string MutedKey = "terryball.voice.muted";
	private const string HelpKey = "terryball.help_seen";

	/// <summary>Master voice switch. Default OFF — while false we neither transmit our mic nor hear anyone.</summary>
	public static bool VoiceEnabled
	{
		get => Cookie.Get( EnabledKey, false );
		set => Cookie.Set( EnabledKey, value );
	}

	/// <summary>Whether the first-run how-to-play pop-up has been shown to this user.</summary>
	public static bool HelpSeen
	{
		get => Cookie.Get( HelpKey, false );
		set => Cookie.Set( HelpKey, value );
	}

	// The muted set is stored as a long[] (SteamId.Value) so it round-trips cleanly through the cookie's
	// JSON serializer. We load it into a HashSet on each read for O(1) membership — the roster is tiny so
	// this is cheap even per-frame.
	private static HashSet<long> LoadMuted()
		=> new HashSet<long>( Cookie.Get( MutedKey, System.Array.Empty<long>() ) );

	private static void SaveMuted( HashSet<long> set )
		=> Cookie.Set( MutedKey, set.ToArray() );

	/// <summary>Is this SteamID currently muted? SteamId implicitly converts to long.</summary>
	public static bool IsMuted( long steamId ) => LoadMuted().Contains( steamId );

	/// <summary>Flip the muted state for a SteamID and persist immediately.</summary>
	public static void ToggleMute( long steamId )
	{
		var set = LoadMuted();
		if ( !set.Remove( steamId ) )
			set.Add( steamId );
		SaveMuted( set );
	}

	/// <summary>How many players are muted — used only for the panel's BuildHash.</summary>
	public static int MutedCount => LoadMuted().Count;
}
