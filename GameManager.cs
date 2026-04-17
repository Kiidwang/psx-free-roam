using Godot;

public partial class GameManager : Node
{
	public static GameManager Instance { get; private set; }

	private const int   MaxHours = 24;
	private const float MaxFog   = 1.0f;

	private int                  _hours = 0;
	private Godot.Environment    _env;
	private float                _baseFog;
	private Label                _hudLabel;
	private Panel                _gameOverPanel;
	private Label                _gameOverLabel;

	public override void _Ready()
	{
		Instance = this;

		var worldEnv = GetTree().Root.GetNode<WorldEnvironment>("Root/WorldEnvironment");
		_env     = worldEnv.Environment;
		_baseFog = _env.FogDensity;

		BuildHUD();
		UpdateHUD();
	}

	public void OnInteractionComplete()
	{
		if (_hours >= MaxHours) return;

		_hours++;
		UpdateFog();
		UpdateHUD();

		if (_hours >= MaxHours)
			ShowGameOver();
	}

	private void UpdateFog()
	{
		// Quadratic curve — fog stays mild early, surges toward the end
		float t = (float)_hours / MaxHours;
		_env.FogDensity = _baseFog + (MaxFog - _baseFog) * t * t;
	}

	private void BuildHUD()
	{
		var layer = new CanvasLayer { Layer = 129 };
		AddChild(layer);

		// Hour counter — top left
		_hudLabel = new Label();
		_hudLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
		_hudLabel.OffsetLeft   =  16f;
		_hudLabel.OffsetTop    =  16f;
		_hudLabel.OffsetRight  =  300f;
		_hudLabel.OffsetBottom =  48f;
		_hudLabel.AddThemeColorOverride("font_color", new Color(1f, 0.9f, 0.6f, 1f));
		_hudLabel.AddThemeFontSizeOverride("font_size", 20);
		layer.AddChild(_hudLabel);

		// Game over overlay
		_gameOverPanel = new Panel();
		_gameOverPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_gameOverPanel.Visible = false;
		var style = new StyleBoxFlat();
		style.BgColor = new Color(0f, 0f, 0f, 0.85f);
		_gameOverPanel.AddThemeStyleboxOverride("panel", style);
		layer.AddChild(_gameOverPanel);

		_gameOverLabel = new Label();
		_gameOverLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_gameOverLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_gameOverLabel.VerticalAlignment   = VerticalAlignment.Center;
		_gameOverLabel.AddThemeFontSizeOverride("font_size", 36);
		_gameOverLabel.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f, 1f));
		_gameOverLabel.Text = "The fog takes you.\n\n24 hours have passed.";
		_gameOverPanel.AddChild(_gameOverLabel);
	}

	private void UpdateHUD()
	{
		int remaining = MaxHours - _hours;
		_hudLabel.Text = $"Time remaining: {remaining}h";
	}

	private void ShowGameOver()
	{
		_gameOverPanel.Visible  = true;
		Player.IsInDialogue     = true; // freeze player
		Input.MouseMode         = Input.MouseModeEnum.Visible;
	}
}
