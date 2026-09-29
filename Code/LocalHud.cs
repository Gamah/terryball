using Sandbox;

namespace Terryball;

/// <summary>
/// The local player's on-screen HUD — built once per client and NEVER networked. Spawned by
/// <see cref="LocalHudSystem"/> (a purely-local GameObjectSystem) on a runtime <see cref="NetworkMode"/>
/// .Never root object, with no dependency on any networked object. There is one HUD per player,
/// independent of how many lanes exist, and it follows whatever lane that player has joined (the panels
/// resolve their director via <see cref="Lane.ForLocalPlayer"/>). Nothing here may hang off a networked
/// object, or its state joins that object's snapshot and the host's mute / music / panel state reaches
/// every client.
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
		Components.Create<HowToPlay>();
		Components.Create<TeleportPanel>();
		// The SETTINGS podium's engage screen (theme, sky brightness, voice range). The podium itself is out
		// on the concourse (SpawnAreaBuilder) and carries nothing but its label — this screen is the whole UI.
		Components.Create<WorldSettings>();
		// The MORE podium's engage screen — the Discord invite and our other games, both
		// click-to-copy. Same deal: the podium out on the concourse carries only its label.
		Components.Create<MorePanel>();
		// …and the applier that turns one of those choices into actual scene lighting. Client-local like the
		// rest of this HUD: it writes the authored Sun/skybox on THIS client's copy of the scene only.
		Components.Create<SkyBrightnessApplier>();
		Components.Create<ChatPanel>();
		Components.Create<BowlerHud>();
		// The guided-throw ("Aim") mode driver — purely client-local, dormant until the player turns the
		// mode on in the lane controls and is the bowler up. See ThrowAimer. ThrowAimHud is the bottom-of-
		// screen step meter it drives (reads ThrowAimer's published state on this same object).
		Components.Create<ThrowAimer>();
		Components.Create<ThrowAimHud>();

		// Client-local background music: the Skafinity engine plays on its own; the drop-in panel starts
		// DISABLED (so its ♪ button never shows over the alley and steals the cursor) and is driven from
		// the keyboard by MusicScreen — N toggles the board, M mutes. See MusicScreen.
		var music = Components.Create<Skafinity.SkafinityPlayer>();
		music.Volume = 0.25f; // start quiet — background ambience, not the main event
		// Route to the "Music" mixer (default routing is the "Game" mixer). The engine only scales a
		// mixer named "Music" by the sound-options music slider (Preferences.MusicVolume), so without
		// this the slider does nothing to the soundtrack. That scaling is skipped in the editor
		// (Mixer.FinishMixing gates on !Application.IsEditor) [SOURCE].
		music.MixerName = "Music";
		Components.Create<Skafinity.SkafinityMusicPanel>( false );
		Components.Create<MusicScreen>();

		// Proximity voice: the keyboard driver (G toggles voice, B opens the mute roster) plus its HUD
		// chip/roster panel. VoiceScreen also fires the first-run how-to-play pop-up. Client-local, like
		// everything else on this HUD — the mute/enabled state lives in per-user cookies (VoicePrefs).
		Components.Create<VoiceScreen>();
		Components.Create<VoicePanel>();

		// Every keycap affordance in the game — chat, music (M/N), voice (G/B), respawn (R) — in one
		// bottom-left stack. See HudHints.
		Components.Create<HudHints>();
	}
}
