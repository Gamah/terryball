using Sandbox;

namespace Terryball;

/// <summary>
/// Builds a single flat floor at runtime so the scene needs no authored geometry.
/// Visual on a non-uniformly-scaled child, collider on the uniformly-scaled root
/// (s&box freezes physics if a BoxCollider sits on a non-uniform scale).
/// </summary>
public sealed class FloorBuilder : Component, Component.ExecuteInEditor
{
	[Property] public float Size { get; set; } = 102400f;
	[Property] public float Thickness { get; set; } = 256f;
	[Property] public Color FloorColor { get; set; } = Color.White;

	protected override void OnEnabled()
	{
		BuildFloor();
	}

	private void BuildFloor()
	{
		var floor = new GameObject( true, "Floor" )
		{
			Parent = GameObject,
			Flags = GameObjectFlags.NotSaved
		};

		// Collider on the uniformly-scaled root child.
		var col = floor.Components.GetOrCreate<BoxCollider>();
		col.Scale = new Vector3( Size, Size, Thickness );
		col.Center = new Vector3( 0, 0, -Thickness * 0.5f );

		// Visual on a non-uniformly-scaled grandchild driven by model bounds.
		var visual = new GameObject( true, "FloorVisual" ) { Parent = floor };
		var model = visual.Components.GetOrCreate<ModelRenderer>();
		model.Model = Model.Load( "models/dev/box.vmdl" );
		// Built-in dev measure grid (compiled, texture baked in) — tiles per-unit on the
		// scaled box so the ball's speed/motion is readable as it rolls.
		model.MaterialOverride = Material.Load( "materials/dev/dev_measuregeneric01.vmat" );
		model.Tint = FloorColor;

		var bounds = model.Model.Bounds.Size;
		visual.LocalScale = new Vector3(
			Size / bounds.x,
			Size / bounds.y,
			Thickness / bounds.z );
		visual.LocalPosition = new Vector3( 0, 0, -Thickness * 0.5f );
	}
}
