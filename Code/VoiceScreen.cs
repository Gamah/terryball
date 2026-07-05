using Sandbox;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Client-local keyboard driver for proximity voice — the voice counterpart to <see cref="MusicScreen"/>.
/// Lives on the <see cref="LocalHud"/> GO (never networked). Each frame it pushes the local player's
/// transmit config onto their own <see cref="TerryVoice"/> and reads the V / B keys:
///
///   G — toggle the master voice switch (<see cref="VoicePrefs.VoiceEnabled"/>). OFF = neither send nor
///       hear; ON = transmit (subject to the player's own s&amp;box voice mode) + hear un-muted players.
///       Only read while walking — G doubles as the bowler's Exit-ball key mid-throw (GameDirector), so we
///       stand down while Bowling. (V is the engine's default push-to-talk key, so it can't be ours.)
///   B — open/close a keyboard-navigable mute roster: ↑/↓ move the selection over everyone but yourself,
///       Enter/Space toggles mute on the highlighted player, Esc/B closes. No mouse — the cursor stays
///       captured so mouselook is never interrupted.
///
/// Transmit is gated owner-locally by driving <see cref="Voice.Mode"/>: we never touch a networked Enabled
/// flag. When ON we set AlwaysOn + the real "voice" push-to-talk binding (so a player whose s&amp;box mode is
/// Push-To-Talk still has to hold their key); when OFF we set Manual + IsListening false + an unbound
/// sentinel input so nothing can ever leak through.
///
/// The engine ultimately decides whether the mic actually transmits (Voice.IsListening honours the user's
/// s&amp;box voip_mode, which game code can't change) — this just surfaces + gates our side of it.
/// </summary>
public sealed class VoiceScreen : Component
{
	/// <summary>An input action name that is deliberately never bound, so Input.Down( it ) is always false.
	/// Used as the push-to-talk input while voice is OFF to hard-gate transmit closed.</summary>
	public const string NoVoiceInput = "__terryball_novoice__";

	/// <summary>True while the mute roster is open. The panel reads this to show/hide the roster.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>Index into <see cref="Roster"/> of the highlighted row.</summary>
	public int SelectedIndex { get; private set; }

	/// <summary>Everyone on the server except us, in a stable order — the roster the panel renders and B navigates.</summary>
	public IReadOnlyList<Connection> Roster { get; private set; } = System.Array.Empty<Connection>();

	// One-shot guard for the first-run help pop-up.
	private bool _helpFired;

	protected override void OnDisabled() => IsOpen = false;
	protected override void OnDestroy() => IsOpen = false;

	protected override void OnUpdate()
	{
		var voice = LocalVoice();

		// Push transmit config every frame so it survives anything else poking the component.
		if ( voice.IsValid() )
		{
			if ( VoicePrefs.VoiceEnabled )
			{
				voice.Mode = Voice.ActivateMode.AlwaysOn;
				voice.PushToTalkInput = "voice"; // real binding: PTT-mode users still hold their key
			}
			else
			{
				voice.Mode = Voice.ActivateMode.Manual;
				voice.IsListening = false;
				voice.PushToTalkInput = NoVoiceInput; // unbound → never transmits
			}
		}

		// First-run help: the moment our avatar exists and we've never seen it, force the how-to-play modal
		// open (sticky, so it isn't force-closed by not standing at the HELP podium). Fires exactly once.
		if ( !_helpFired && !VoicePrefs.HelpSeen )
		{
			var me = LocalAvatar();
			if ( me.IsValid() )
			{
				me.RequestOpenHelp();
				if ( me.HelpOpen ) // actually opened (not suppressed by e.g. bowling) — only then count it seen
				{
					VoicePrefs.HelpSeen = true;
					_helpFired = true;
				}
			}
		}

		// Don't eat keys while the chat box has focus.
		if ( ChatPanel.IsOpen )
			return;

		if ( IsOpen )
		{
			ServiceRoster();
			return;
		}

		// G — master voice toggle. Only read while the roster is closed (while it's open B/↑/↓/Enter own the
		// keyboard — ServiceRoster returned above) AND while walking, since G is the bowler's Exit-ball key
		// mid-throw. Voice is disabled during a throw anyway, so there's nothing to toggle then.
		if ( Input.Keyboard.Pressed( "G" ) && !LocalIsBowling() )
			VoicePrefs.VoiceEnabled = !VoicePrefs.VoiceEnabled;

		// B — open the mute roster.
		if ( Input.Keyboard.Pressed( "B" ) )
			OpenRoster();
	}

	private void OpenRoster()
	{
		RebuildRoster();
		if ( Roster.Count == 0 ) return; // nobody to mute — don't bother opening
		IsOpen = true;
		SelectedIndex = 0;
	}

	private void ServiceRoster()
	{
		RebuildRoster();
		if ( Roster.Count == 0 ) { IsOpen = false; return; }

		SelectedIndex = System.Math.Clamp( SelectedIndex, 0, Roster.Count - 1 );

		// Esc or B closes.
		if ( Input.EscapePressed )
		{
			Input.EscapePressed = false;
			IsOpen = false;
			return;
		}
		if ( Input.Keyboard.Pressed( "B" ) )
		{
			IsOpen = false;
			return;
		}

		if ( Input.Keyboard.Pressed( "UP" ) )
			SelectedIndex = (SelectedIndex - 1 + Roster.Count) % Roster.Count;
		if ( Input.Keyboard.Pressed( "DOWN" ) )
			SelectedIndex = (SelectedIndex + 1) % Roster.Count;

		if ( Input.Keyboard.Pressed( "ENTER" ) || Input.Keyboard.Pressed( "SPACE" ) )
		{
			var target = Roster[SelectedIndex];
			if ( target is not null )
				VoicePrefs.ToggleMute( target.SteamId );
		}
	}

	// Everyone but us, ordered by name for stability.
	private void RebuildRoster()
	{
		var local = Connection.Local;
		Roster = Connection.All
			.Where( c => c is not null && c != local )
			.OrderBy( c => c.DisplayName )
			.ToList();
	}

	/// <summary>Our own (non-proxy) voice transmitter, if the avatar's spawned yet.</summary>
	public TerryVoice LocalVoice()
		=> Scene?.GetAllComponents<TerryVoice>().FirstOrDefault( v => v.IsValid() && !v.IsProxy );

	/// <summary>A given connection's voice transmitter (their avatar's), so the roster can show a speaking
	/// dot. Matches the avatar by network owner — the same mapping AlleyServer uses on disconnect.</summary>
	public TerryVoice VoiceFor( Connection c )
		=> c is null ? null : Scene?.GetAllComponents<TerryVoice>()
			.FirstOrDefault( v => v.IsValid() && v.GameObject.Network.Owner?.Id == c.Id );

	private TerryAvatar LocalAvatar()
		=> Scene?.GetAllComponents<TerryAvatar>().FirstOrDefault( a => a.IsValid() && !a.IsProxy );

	// True while our local player is riding the ball — G means Exit-ball then, not voice-toggle.
	private bool LocalIsBowling()
	{
		var me = LocalAvatar();
		return me.IsValid() && me.Bowling;
	}
}
