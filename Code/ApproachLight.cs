using Sandbox;
using System.Linq;

namespace Terryball;

/// <summary>
/// Parks a light over the ball's spawn/rack point (the run-up centre) so it lights the ball during the
/// start-of-frame crane choreography. The rack point (LaneBuilder.RunUpWorldPosition) is procedural, so we
/// resolve it at runtime. Lives at the scene root; aims at the lane's spawn point plus an elevation/offset
/// (the offset sets which way the extra shadow falls).
/// </summary>
[Title( "Approach Light Aim" )]
[Category( "Terryball" )]
[Icon( "wb_incandescent" )]
public sealed class ApproachLight : Component
{
	/// <summary>Height above the spawn point (units).</summary>
	[Property] public float Height { get; set; } = 450f;

	/// <summary>Lateral offset from the spawn point (units) - sets the direction the extra shadow is cast.</summary>
	[Property] public Vector3 Offset { get; set; } = new Vector3( 0f, 220f, 0f );

	protected override void OnUpdate()
	{
		var lane = Scene.GetAllComponents<LaneBuilder>().FirstOrDefault( b => b.IsValid() );
		if ( !lane.IsValid() ) return;
		WorldPosition = lane.RunUpWorldPosition + Vector3.Up * Height + Offset;
	}
}
