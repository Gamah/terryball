using Sandbox;

namespace Terryball;

/// <summary>
/// The local player's on-screen HUD — built once per client and NEVER networked. Spawned by
/// <see cref="LocalHudSystem"/> (a purely-local GameObjectSystem) on a runtime <see cref="NetworkMode"/>
/// .Never root object, with no dependency on any networked object. There is one HUD per player,
/// independent of how many lanes exist, and it follows whatever lane that player has joined (the panels
/// resolve their director via <see cref="Lane.ForLocalPlayer"/>).
///
/// This replaces the old in-Director HUD, which was built as a child of the networked Director — that
/// made the whole subtree (music player, scoreboard, chat) part of the Director's network snapshot, so
/// the host's mute / music / panel state leaked onto every client. Building it off any networked object
/// keeps it strictly client-local.
/// </summary>
public sealed class LocalHud : Component
{
	private bool _built;

	protected override void OnStart() => Build();

	private void Build()
	{
		if ( _built ) return;
		_built = true;

		// One screen-space root carries every panel. They each find the lane the local player has joined
		// via Lane.ForLocalPlayer, so the board/turn display follows the player across lanes.
		Components.Create<ScreenPanel>();
		Components.Create<PinStatusOverlay>();
		Components.Create<ScoreBoard>();
		Components.Create<LaneControls>();
		Components.Create<BallDesigner>();
		Components.Create<ChatPanel>();
		Components.Create<BowlerHud>();

		// Client-local background music: the Skafinity engine plays on its own; the drop-in panel starts
		// DISABLED (so its ♪ button never shows over the alley and steals the cursor) and is driven from
		// the keyboard by MusicScreen — N toggles the board, M mutes. See MusicScreen.
		var music = Components.Create<Skafinity.SkafinityPlayer>();
		music.Volume = 0.25f; // start quiet — background ambience, not the main event
		Components.Create<Skafinity.SkafinityMusicPanel>( false );
		Components.Create<MusicScreen>();
		Components.Create<MusicHud>();
	}
}
