using Godot;

// Front-end flow that runs before the player takes control:
//   Menu (floating camera orbiting the player model)
//     -> PLAY: fade to black, fade back into gameplay
//        -> tutorial prompt (Y/N)
//           -> Y: the Doctor explains the lore and how long you have left
//           -> N: straight into the day
// Built entirely in code and parented under GameManager, so the scene file
// stays untouched. Freezes the world via Player.IsInDialogue while it runs.
public partial class MainMenu : Node
{
	private enum State { Menu, FadingOut, FadingIn, Tutorial, Doctor, Done }
	private State _state = State.Menu;

	private Node3D   _player;
	private Camera3D _playerCamera;
	private Camera3D _menuCamera;

	private AudioStreamPlayer _sfx;
	private AudioStream        _sfxYes;
	private AudioStream        _sfxNo;

	private CanvasLayer _layer;
	private Control     _menuRoot;
	private Button      _playButton;
	private Button      _exitButton;
	private ColorRect   _fade;

	private Panel _promptPanel;
	private Panel _doctorPanel;
	private Label _doctorText;

	private float _orbitAngle = 0f;
	private float _bobTime    = 0f;
	private float _fadeAlpha  = 0f;

	private const float FadeSpeed   = 1f / 0.6f; // 0.6s per fade
	private const float OrbitRadius = 4.2f;
	private const float OrbitSpeed  = 0.25f;

	private int _doctorPage = 0;
	private readonly string[] _doctorPages =
	{
		"You came in for the results. I won't insult you by softening them.",
		"The fog you've been seeing at the edges of things — that isn't your eyes failing.\nThat's the world closing in.",
		"So I'll tell you what no one else will: you have one day.\nUntil this same hour tomorrow. No longer.",
		"Twenty-four hours. The people who matter to you are still out there.\nGo to them while the light holds.",
		"I've set a clock at the top of your sight. Watch it.\nIt's the only honest thing I have left to give you."
	};

	public override void _Ready()
	{
		_player       = GetTree().Root.GetNodeOrNull<Node3D>("Root/Player");
		_playerCamera = GetTree().Root.GetNodeOrNull<Camera3D>("Root/Player/Camera3D");

		Player.IsInDialogue = true;                       // freeze the world during the menu
		Input.MouseMode     = Input.MouseModeEnum.Visible;

		_sfx = new AudioStreamPlayer();
		AddChild(_sfx);
		_sfxYes = GD.Load<AudioStream>("res://SFX/Main_Menu_Yes.wav");
		_sfxNo  = GD.Load<AudioStream>("res://SFX/Main_Menu_No.wav");

		BuildMenuCamera();
		BuildUI();
	}

	private void PlayMenuSound(bool yes)
	{
		var stream = yes ? _sfxYes : _sfxNo;
		if (stream == null) return;
		_sfx.Stream = stream;
		_sfx.Play();
	}

	private void BuildMenuCamera()
	{
		_menuCamera = new Camera3D { Fov = 45f, Near = 0.05f };
		AddChild(_menuCamera);
		_menuCamera.Current = true;
		PositionMenuCamera();
	}

	private void PositionMenuCamera()
	{
		Vector3 center = (_player?.GlobalPosition ?? Vector3.Zero) + Vector3.Up * 1.1f;
		float   height = 1.6f + Mathf.Sin(_bobTime) * 0.3f;          // gentle vertical float
		_menuCamera.GlobalPosition = center + new Vector3(
			Mathf.Sin(_orbitAngle) * OrbitRadius,
			height,
			Mathf.Cos(_orbitAngle) * OrbitRadius);
		_menuCamera.LookAt(center, Vector3.Up);
	}

	private void BuildUI()
	{
		_layer = new CanvasLayer { Layer = 200 };
		AddChild(_layer);

		// --- Title + buttons -------------------------------------------------
		_menuRoot = new Control();
		_menuRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_menuRoot.MouseFilter = Control.MouseFilterEnum.Ignore;
		_layer.AddChild(_menuRoot);

		var title = new Label();
		title.SetAnchorsPreset(Control.LayoutPreset.TopWide);
		title.OffsetTop    = 130f;
		title.OffsetBottom = 220f;
		title.HorizontalAlignment = HorizontalAlignment.Center;
		title.AddThemeFontSizeOverride("font_size", 79);
		title.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.85f, 1f));
		title.Text = "ONE DAY";
		_menuRoot.AddChild(title);

		var subtitle = new Label();
		subtitle.SetAnchorsPreset(Control.LayoutPreset.TopWide);
		subtitle.OffsetTop    = 210f;
		subtitle.OffsetBottom = 250f;
		subtitle.HorizontalAlignment = HorizontalAlignment.Center;
		subtitle.AddThemeFontSizeOverride("font_size", 20);
		subtitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.68f, 0.9f));
		subtitle.Text = "before the fog comes in";
		_menuRoot.AddChild(subtitle);

		_playButton = MakeMenuButton("PLAY", topOffset: 20f);
		_playButton.Pressed += OnPlayPressed;
		_menuRoot.AddChild(_playButton);

		_exitButton = MakeMenuButton("EXIT", topOffset: 80f);
		_exitButton.Pressed += OnExitPressed;
		_menuRoot.AddChild(_exitButton);

		// --- Fade overlay (above the menu, ignores the mouse) ----------------
		_fade = new ColorRect();
		_fade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_fade.Color       = new Color(0f, 0f, 0f, 0f);
		_fade.MouseFilter = Control.MouseFilterEnum.Ignore;
		_layer.AddChild(_fade);

		// --- Tutorial prompt (centered) --------------------------------------
		_promptPanel = MakePanel(560f, 200f, centered: true);
		_promptPanel.Visible = false;
		_layer.AddChild(_promptPanel);

		var promptText = new Label();
		promptText.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		promptText.OffsetTop    = 34f;
		promptText.OffsetLeft   = 32f;
		promptText.OffsetRight  = -32f;
		promptText.HorizontalAlignment = HorizontalAlignment.Center;
		promptText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		promptText.AddThemeFontSizeOverride("font_size", 22);
		promptText.Text = "A clinic. A diagnosis. A single day left.\n\nWould you like the doctor to explain?";
		_promptPanel.AddChild(promptText);

		var promptHint = new Label();
		promptHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
		promptHint.OffsetTop    = -44f;
		promptHint.OffsetBottom = -16f;
		promptHint.HorizontalAlignment = HorizontalAlignment.Center;
		promptHint.AddThemeFontSizeOverride("font_size", 20);
		promptHint.AddThemeColorOverride("font_color", new Color(0.4f, 1f, 0.4f, 1f));
		promptHint.Text = "[ Y ] Yes        [ N ] No";
		_promptPanel.AddChild(promptHint);

		// --- Doctor intro (bottom box, mirrors the in-game dialogue) ---------
		_doctorPanel = new Panel();
		_doctorPanel.AnchorLeft = 0f; _doctorPanel.AnchorRight = 1f;
		_doctorPanel.AnchorTop  = 1f; _doctorPanel.AnchorBottom = 1f;
		_doctorPanel.OffsetLeft = 40f; _doctorPanel.OffsetRight = -40f;
		_doctorPanel.OffsetTop  = -212f; _doctorPanel.OffsetBottom = -24f;
		_doctorPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor          = new Color(0.04f, 0.04f, 0.14f, 0.93f),
			BorderColor      = new Color(0.65f, 0.65f, 1f, 0.9f),
			BorderWidthLeft  = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
			CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
			CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
		});
		_doctorPanel.Visible = false;
		_layer.AddChild(_doctorPanel);

		var doctorName = new Label();
		doctorName.Position = new Vector2(28f, 14f);
		doctorName.AddThemeFontSizeOverride("font_size", 22);
		doctorName.AddThemeColorOverride("font_color", new Color(0.6f, 0.8f, 1f, 1f));
		doctorName.Text = "The Doctor";
		_doctorPanel.AddChild(doctorName);

		_doctorText = new Label();
		_doctorText.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_doctorText.OffsetTop    = 54f;
		_doctorText.OffsetLeft   = 32f;
		_doctorText.OffsetRight  = -32f;
		_doctorText.OffsetBottom = -44f;
		_doctorText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_doctorText.AddThemeFontSizeOverride("font_size", 22);
		_doctorPanel.AddChild(_doctorText);

		var doctorHint = new Label();
		doctorHint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
		doctorHint.OffsetLeft   = -260f;
		doctorHint.OffsetTop    = -34f;
		doctorHint.OffsetRight  = -16f;
		doctorHint.OffsetBottom = -8f;
		doctorHint.HorizontalAlignment = HorizontalAlignment.Right;
		doctorHint.AddThemeFontSizeOverride("font_size", 18);
		doctorHint.AddThemeColorOverride("font_color", new Color(0.65f, 0.85f, 1f, 0.75f));
		doctorHint.Text = "[ Space ] continue";
		_doctorPanel.AddChild(doctorHint);
	}

	private static Button MakeMenuButton(string text, float topOffset)
	{
		var b = new Button();
		b.SetAnchorsPreset(Control.LayoutPreset.Center);
		b.OffsetLeft   = -120f;
		b.OffsetRight  =  120f;
		b.OffsetTop    =  topOffset;
		b.OffsetBottom =  topOffset + 52f;
		b.AddThemeFontSizeOverride("font_size", 26);
		b.Text = text;
		return b;
	}

	private static Panel MakePanel(float width, float height, bool centered)
	{
		var p = new Panel();
		p.SetAnchorsPreset(Control.LayoutPreset.Center);
		p.OffsetLeft   = -width  / 2f;
		p.OffsetRight  =  width  / 2f;
		p.OffsetTop    = -height / 2f;
		p.OffsetBottom =  height / 2f;
		p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor          = new Color(0.04f, 0.04f, 0.14f, 0.93f),
			BorderColor      = new Color(0.65f, 0.65f, 1f, 0.9f),
			BorderWidthLeft  = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
			CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
			CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
		});
		return p;
	}

	public override void _Process(double delta)
	{
		if (_state == State.Menu || _state == State.FadingOut)
		{
			_orbitAngle += (float)delta * OrbitSpeed;
			_bobTime    += (float)delta * 0.8f;
			PositionMenuCamera();
		}

		switch (_state)
		{
			case State.FadingOut:
				_fadeAlpha = Mathf.MoveToward(_fadeAlpha, 1f, (float)delta * FadeSpeed);
				_fade.Color = new Color(0f, 0f, 0f, _fadeAlpha);
				if (_fadeAlpha >= 1f) OnFullyBlack();
				break;

			case State.FadingIn:
				_fadeAlpha = Mathf.MoveToward(_fadeAlpha, 0f, (float)delta * FadeSpeed);
				_fade.Color = new Color(0f, 0f, 0f, _fadeAlpha);
				if (_fadeAlpha <= 0f) ShowTutorialPrompt();
				break;
		}
	}

	private void OnPlayPressed()
	{
		if (_state != State.Menu) return;
		PlayMenuSound(true);
		_playButton.Disabled = true;
		_exitButton.Disabled = true;
		_state = State.FadingOut;
	}

	private void OnExitPressed()
	{
		_playButton.Disabled = true;
		_exitButton.Disabled = true;
		PlayMenuSound(false);
		// Let the blip finish before the application tears down.
		GetTree().CreateTimer(0.35).Timeout += () => GetTree().Quit();
	}

	// At full black: hand the view to the player's own camera and drop the menu.
	private void OnFullyBlack()
	{
		_menuRoot.Visible = false;
		if (_menuCamera != null) _menuCamera.Current = false;
		if (_playerCamera != null) _playerCamera.Current = true;
		_state = State.FadingIn;
	}

	private void ShowTutorialPrompt()
	{
		if (_state == State.Tutorial) return;
		_promptPanel.Visible = true;
		_state = State.Tutorial;
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

		if (_state == State.Tutorial)
		{
			if (key.Keycode == Key.Y)
			{
				PlayMenuSound(true);
				_promptPanel.Visible = false;
				StartDoctor();
			}
			else if (key.Keycode == Key.N)
			{
				PlayMenuSound(false);
				_promptPanel.Visible = false;
				FinishIntro(revealTruth: false);
			}
		}
		else if (_state == State.Doctor && key.Keycode == Key.Space)
		{
			AdvanceDoctor();
		}
	}

	private void StartDoctor()
	{
		_state       = State.Doctor;
		_doctorPage  = 0;
		_doctorPanel.Visible = true;
		_doctorText.Text = _doctorPages[0];
	}

	private void AdvanceDoctor()
	{
		_doctorPage++;
		if (_doctorPage >= _doctorPages.Length)
		{
			_doctorPanel.Visible = false;
			FinishIntro(revealTruth: true); // the Doctor told the truth -> reveal the clock
			return;
		}
		_doctorText.Text = _doctorPages[_doctorPage];
	}

	private void FinishIntro(bool revealTruth)
	{
		_state = State.Done;
		Player.IsInDialogue = false;
		Input.MouseMode     = Input.MouseModeEnum.Captured;

		if (_menuCamera != null) { _menuCamera.QueueFree(); _menuCamera = null; }
		if (_playerCamera != null) _playerCamera.Current = true;

		GameManager.Instance?.OnIntroComplete(revealTruth);
		QueueFree();
	}
}
