using Sandbox;
using System.Linq;
using Skafinity;

namespace Terryball;

/// <summary>
/// Drives the Skafinity library's drop-in <see cref="SkafinityMusicPanel"/> from the keyboard
/// instead of its built-in floating ♪ button. Left always-on over the alley that button is an
/// interactive (pointer-events) element — it would keep the cursor released and kill mouselook.
/// So the panel is kept <em>disabled</em> while
/// closed (its button never shows, never steals input); opening it enables + opens it and frees
/// the cursor, closing it disables it again and hands the cursor back.
///
///   N — toggle the board open/closed.
///   M — mute/unmute the music outright, whether the board is open or not.
///
/// Lives on the client-local screen-space HUD GO alongside the panel and a <see cref="SkafinityPlayer"/>
/// (both built in code by <see cref="Terryball.LocalHud"/>). There's no in-world "wall" to walk up
/// to — the keys work from anywhere, like the built-in chat (Enter).
///
/// It is also where the board gets its COLOUR — see <see cref="PushTheme"/>. This component is
/// enabled all session while the panel is enabled only while open, so it is the one of the two
/// that can keep the world theme current.
/// </summary>
public sealed class MusicScreen : Component
{
	/// <summary>True while the music board is showing. Read by <see cref="BallCamera"/> to freeze look
	/// while the cursor's freed for the panel, the same way it gates on <see cref="ChatPanel.IsOpen"/>,
	/// and by <see cref="HudHints"/>, which hides the whole hint stack under it.</summary>
	public static bool IsOpen { get; private set; }

	/// <summary>The soundtrack's master switch, published for the M hint in <see cref="HudHints"/>.
	/// SkafinityPlayer.Enabled is a `new` bool distinct from the component's own Enabled; false =
	/// muted, matching the panel's own MUTE button.</summary>
	public static bool Muted { get; private set; }

	private SkafinityPlayer _player;
	private SkafinityMusicPanel _panel;

	/// <summary>The world-theme hex last handed to the library, so an unchanged setting costs a
	/// string compare. Starts null, which never equals the "" of AUTO, so the first frame pushes.</summary>
	private string _themeHex;

	protected override void OnDisabled() => Close();
	protected override void OnDestroy() => Close();

	/// <summary>
	/// The music board wears the WORLD THEME: hand the library the hex the player picked at the
	/// SETTINGS podium (<see cref="WorldPrefs.ThemeColor"/>, empty = AUTO).
	/// <see cref="SkafinityTheme"/> derives its whole palette from that one accent.
	///
	/// <para>A PUSH rather than the panel reading <see cref="WorldTheme"/>: the library is
	/// vendored and must not be patched, so it cannot know what a Terryball is — a settable
	/// static is the seam it offers. It STORES the value while the setting is live, so pushing
	/// once at startup would leave the board on whatever colour the alley had when the client
	/// booted. Compared against the hex string rather than <see cref="WorldPrefs.Revision"/> so
	/// any path that changes the theme reaches the board; the cookie read is cheap and this runs
	/// only while the component is enabled.</para>
	///
	/// <para>This deliberately passes the RAW hex rather than <see cref="WorldTheme.Accent"/>.
	/// WorldTheme resolves AUTO to terryball's amber, which would make the board amber for a
	/// player who never picked a theme; the library's own unset state is a neutral gray-on-black,
	/// which is the right look for "no theme picked". A coloured swatch is therefore the only
	/// thing that demonstrates this working.</para>
	///
	/// <para>Safe while the board is closed, which is the usual case: the panel folds the accent
	/// into its <c>BuildHash</c>, so it re-renders when it is next enabled.</para>
	/// </summary>
	private void PushTheme()
	{
		var hex = WorldPrefs.ThemeColor ?? "";
		if ( hex == _themeHex ) return;
		_themeHex = hex;
		// Color.Parse returns null on junk, which lands on the same neutral as AUTO.
		SkafinityTheme.Accent = string.IsNullOrEmpty( hex ) ? (Color?)null : Color.Parse( hex );
	}

	protected override void OnUpdate()
	{
		PushTheme();


		// The panel starts disabled, so the enabled-only Scene scan won't see it — find it on our own
		// GO including disabled. The player plays on its own and is found scene-wide.
		_panel ??= Components.Get<SkafinityMusicPanel>( FindMode.EverythingInSelf );
		_player ??= Scene.GetAllComponents<SkafinityPlayer>().FirstOrDefault();
		if ( _player != null ) Muted = !_player.Enabled;
		if ( _panel == null ) return;

		// Don't eat the keys while the chat box has them (it swallows typing anyway, but be explicit).
		if ( !ChatPanel.IsOpen )
		{
			// M: mute toggle — works open or closed (the engine plays even with the board hidden).
			if ( _player != null && Input.Keyboard.Pressed( "M" ) )
				_player.Enabled = !_player.Enabled;

			// N: toggle the board.
			if ( Input.Keyboard.Pressed( "N" ) )
			{
				if ( IsOpen ) Close(); else Open();
			}
		}

		if ( !IsOpen ) return;

		// The panel's own ✕ closes itself — mirror that back into our cursor/open state.
		if ( !_panel.IsOpen )
		{
			Close();
			return;
		}

		// Escape also closes it.
		if ( Input.EscapePressed )
		{
			Input.EscapePressed = false;
			Close();
			return;
		}

		// Keep the cursor freed for the buttons (re-assert in case anything resets it). With the mouse
		// un-captured AnalogLook reads ~0, so neither the ball nor the walk camera spins while it's up.
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void Open()
	{
		IsOpen = true;
		_panel.Enabled = true;
		if ( !_panel.IsOpen ) _panel.Toggle();
		Mouse.Visibility = MouseVisibility.Visible;
	}

	private void Close()
	{
		IsOpen = false;
		if ( _panel != null )
		{
			_panel.Enabled = false;          // hide it (and its ♪ button) so it stops stealing pointer input
			if ( _panel.IsOpen ) _panel.Toggle(); // normalise back to closed for the next open
		}
		Mouse.Visibility = MouseVisibility.Auto; // hand the cursor back to look control
	}
}
