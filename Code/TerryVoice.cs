using Sandbox;

namespace Terryball;

/// <summary>
/// The built-in <see cref="Voice"/> transmitter, subclassed so we can gate playback on this client's
/// local <see cref="VoicePrefs"/>. One of these rides every player's avatar (created host-side in
/// <see cref="AlleyServer.SpawnAvatar"/>); only the owner records their mic, every other client plays it
/// back in 3D (<see cref="Voice.WorldspacePlayback"/>).
///
/// <see cref="ShouldHearVoice"/> runs on the RECEIVER when a voice packet arrives, with the SENDER's
/// connection — so reading our own <see cref="VoicePrefs"/> here is exactly right: master-off hears
/// nobody, otherwise we drop packets from anyone we've muted. Transmit is gated separately, owner-locally,
/// by <see cref="VoiceScreen"/> flipping <see cref="Voice.Mode"/> / IsListening.
///
/// NOTE: <see cref="Voice.OnUpdate"/> is sealed in the engine, so we can't override it — and don't need
/// to. Only the two hear/exclude hooks are virtual.
/// </summary>
public sealed class TerryVoice : Voice
{
	protected override bool ShouldHearVoice( Connection c )
		=> VoicePrefs.VoiceEnabled && !VoicePrefs.IsMuted( c.SteamId );
}
