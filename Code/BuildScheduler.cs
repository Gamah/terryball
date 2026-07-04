using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Terryball;

/// <summary>
/// Spreads heavy one-off scene construction across frames so a JOINING client isn't blocked long enough on
/// the main thread to drop the connection.
///
/// The four <see cref="LaneBuilder"/>s used to run their whole procedural build synchronously inside
/// OnEnabled while the scene loaded — 160+ colliders plus lathed pin meshes, all in the same frame the
/// scene came up — which pegged a joining client's main thread straight through the network handshake and
/// timed the join out. Instead each lane now enqueues its build here, and this runs ONE job per frame:
/// OnEnabled returns immediately, the scene finishes loading, the handshake completes, and the alley streams
/// in over the next handful of frames while the network keeps ticking.
///
/// Client-local, auto-created, NotSaved — there's nothing to replicate; every client fills its own alley.
/// </summary>
public sealed class BuildScheduler : Component
{
	private readonly List<(int Priority, Action Job)> _jobs = new();

	/// <summary>The scene's scheduler, created on first use (a NotSaved holder at the root). All four lanes
	/// enqueue on the same load frame; they run sequentially on the main thread, so the find-or-create is
	/// race-free.</summary>
	public static BuildScheduler For( Scene scene )
	{
		var existing = scene?.GetAllComponents<BuildScheduler>().FirstOrDefault();
		if ( existing.IsValid() ) return existing;
		var go = new GameObject( true, "BuildScheduler" ) { Flags = GameObjectFlags.NotSaved };
		return go.Components.Create<BuildScheduler>();
	}

	/// <summary>Queue a build to run on a future frame. Lower <paramref name="priority"/> runs first.</summary>
	public void Enqueue( int priority, Action job )
	{
		if ( job is not null ) _jobs.Add( (priority, job) );
	}

	protected override void OnUpdate()
	{
		if ( _jobs.Count == 0 ) return;

		// One job per frame — keep each post-load frame light so the network stays responsive while the
		// geometry fills in. Pick the highest-priority (lowest number) pending job.
		var idx = 0;
		for ( var i = 1; i < _jobs.Count; i++ )
			if ( _jobs[i].Priority < _jobs[idx].Priority ) idx = i;

		var job = _jobs[idx].Job;
		_jobs.RemoveAt( idx );
		job?.Invoke();
	}
}
