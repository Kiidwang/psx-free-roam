using Godot;

public partial class Goal : Area3D
{
	[Export] public Label WinLabel { get; set; }

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
	}

	private void OnBodyEntered(Node3D body)
	{
		if (body is CharacterBody3D)
		{
			if (WinLabel != null)
				WinLabel.Visible = true;
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
	}
}
