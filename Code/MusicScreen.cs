using Sandbox;
using System.Linq;
using Skafinity;

namespace Terryball;

/// <summary>
/// Drives the Skafinity library's drop-in <see cref="SkafinityMusicPanel"/> from the keyboard
/// instead of its built-in floating ♪ button. Left always-on over the alley that button is an
/// interactive (pointer-events) element — it would keep the cursor released and kill mouselook
/// (the same problem Rotaliate's music board had). So the panel is kept <em>disabled</em> while
/// closed (its button never shows, never steals input); opening it enables + opens it and frees
/// the cursor, closing it disables it again and hands the cursor back.
///
///   N — toggle the board open/closed.
///   M — mute/unmute the music outright, whether the board is open or not.
///
/// Lives on the client-local screen-space HUD GO alongside the panel and a <see cref="SkafinityPlayer"/>
/// (both built in code by <see cref="Terryball.LocalHud"/>). There's no in-world "wall" to walk up
/// to — the keys work from anywhere, like the built-in chat (Enter).
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

	protected override void OnDisabled() => Close();
	protected override void OnDestroy() => Close();

	protected override void OnUpdate()
	{
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
