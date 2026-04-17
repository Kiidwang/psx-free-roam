using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

public partial class NPC : Node3D
{
	[Export] public string ModelPath   = "";
	[Export] public float  ModelScale  = 0.47f;
	[Export] public string DialogueFile = "";

	private Area3D      _interactionArea;
	private Label3D     _promptLabel;
	private Camera3D    _cinematicCamera;
	private CanvasLayer _dialogueUI;
	private Label       _dialogueLabel;
	private Label       _continueLabel;
	private Label       _choiceLabel;

	private Node3D   _player       = null;
	private Camera3D _playerCamera = null;

	private bool _playerInRange  = false;
	private bool _dialogueActive = false;

	private List<DialogueEntry> _dialogues    = new();
	private int                 _dialogueIndex = 0;
	private bool                _awaitingChoice = false; // true = showing question; false = showing response

	private class DialogueEntry
	{
		[JsonPropertyName("text")]         public string Text        { get; set; } = "";
		[JsonPropertyName("yes_response")] public string YesResponse { get; set; } = "";
		[JsonPropertyName("no_response")]  public string NoResponse  { get; set; } = "";
		[JsonPropertyName("type")]         public string Type        { get; set; } = "";
		[JsonPropertyName("amount")]       public int    Amount      { get; set; } = 0;
	}

	private class DialogueData
	{
		[JsonPropertyName("dialogues")] public List<DialogueEntry> Dialogues { get; set; } = new();
	}

	public override void _Ready()
	{
		_interactionArea = GetNode<Area3D>("InteractionArea");
		_promptLabel     = GetNode<Label3D>("PromptLabel");
		_cinematicCamera = GetNode<Camera3D>("CinematicCamera");
		_dialogueUI      = GetNode<CanvasLayer>("DialogueUI");
		_dialogueLabel   = GetNode<Label>("DialogueUI/Panel/DialogueText");
		_continueLabel   = GetNode<Label>("DialogueUI/Panel/ContinueLabel");

		_interactionArea.BodyEntered += OnBodyEntered;
		_interactionArea.BodyExited  += OnBodyExited;

		_cinematicCamera.Current = false;
		_promptLabel.Visible     = false;
		_dialogueUI.Visible      = false;
		_dialogueUI.Layer        = 128;

		var panel = GetNode<Control>("DialogueUI/Panel");
		panel.AnchorLeft   = 0f;
		panel.AnchorRight  = 1f;
		panel.AnchorTop    = 1f;
		panel.AnchorBottom = 1f;
		panel.OffsetLeft   =  40f;
		panel.OffsetRight  = -40f;
		panel.OffsetTop    = -180f;
		panel.OffsetBottom = -20f;

		// Choice label added at runtime so it renders above PSX effect
		_choiceLabel = new Label();
		_choiceLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
		_choiceLabel.OffsetLeft   =  16f;
		_choiceLabel.OffsetBottom = -8f;
		_choiceLabel.OffsetTop    = -48f;
		_choiceLabel.OffsetRight  =  400f;
		_choiceLabel.AddThemeFontSizeOverride("font_size", 18);
		_choiceLabel.AddThemeColorOverride("font_color", new Color(0.4f, 1f, 0.4f, 1f));
		_choiceLabel.Text    = "[ Y ] Yes       [ N ] No";
		_choiceLabel.Visible = false;
		panel.AddChild(_choiceLabel);

		LoadDialogues();

		if (ModelPath != "")
			LoadModel();
	}

	private void LoadDialogues()
	{
		if (DialogueFile == "") return;
		var file = FileAccess.Open(DialogueFile, FileAccess.ModeFlags.Read);
		if (file == null) { GD.PrintErr($"NPC: cannot open {DialogueFile}"); return; }
		var json = file.GetAsText();
		file.Close();
		var data = JsonSerializer.Deserialize<DialogueData>(json);
		if (data != null) _dialogues = data.Dialogues;
	}

	private void LoadModel()
	{
		var scene = GD.Load<PackedScene>(ModelPath);
		if (scene == null) { GD.PrintErr($"NPC: failed to load {ModelPath}"); return; }
		var instance = scene.Instantiate<Node3D>();
		GetNode<Node3D>("Model").AddChild(instance);
		instance.Scale *= ModelScale;
		StartIdleAnimation();
	}

	private void StartIdleAnimation()
	{
		var ap = FindAnimationPlayer(this);
		if (ap == null) return;
		foreach (StringName a in ap.GetAnimationList())
			ap.GetAnimation(a).LoopMode = Animation.LoopModeEnum.Linear;
		if (ap.GetAnimationList().Length > 0)
			ap.Play(ap.GetAnimationList()[0]);
	}

	private static AnimationPlayer FindAnimationPlayer(Node root)
	{
		if (root is AnimationPlayer ap) return ap;
		foreach (Node child in root.GetChildren())
		{
			var found = FindAnimationPlayer(child);
			if (found != null) return found;
		}
		return null;
	}

	public override void _Input(InputEvent @event)
	{
		if (Player.IsInDialogue && !_dialogueActive) return;
		if (@event is not InputEventKey key) return;
		if (!key.Pressed || key.Echo)          return;

		if (_playerInRange && !_dialogueActive && key.Keycode == Key.Space)
		{
			StartDialogue();
			return;
		}

		if (!_dialogueActive) return;

		if (_awaitingChoice)
		{
			if (key.Keycode == Key.Y)      HandleChoice(yes: true);
			else if (key.Keycode == Key.N) HandleChoice(yes: false);
		}
		else
		{
			if (key.Keycode == Key.Space) EndDialogue();
		}
	}

	private void StartDialogue()
	{
		_dialogueActive     = true;
		Player.IsInDialogue = true;
		_awaitingChoice     = true;

		PositionCinematicCamera();
		_cinematicCamera.Current = true;
		_dialogueUI.Visible      = true;
		_promptLabel.Visible     = false;

		Input.MouseMode = Input.MouseModeEnum.Visible;

		ShowQuestion();
	}

	private void ShowQuestion()
	{
		if (_dialogues.Count == 0)
		{
			_dialogueLabel.Text  = "...";
			_choiceLabel.Visible = false;
			_continueLabel.Visible = true;
			_awaitingChoice = false;
			return;
		}

		var entry = _dialogues[_dialogueIndex % _dialogues.Count];
		_dialogueLabel.Text    = entry.Text;
		_choiceLabel.Visible   = true;
		_continueLabel.Visible = false;
		_awaitingChoice        = true;
	}

	private void HandleChoice(bool yes)
	{
		if (_dialogues.Count == 0) return;

		var entry = _dialogues[_dialogueIndex % _dialogues.Count];
		_dialogueLabel.Text    = yes ? entry.YesResponse : entry.NoResponse;
		_choiceLabel.Visible   = false;
		_continueLabel.Visible = true;
		_awaitingChoice        = false;

		_dialogueIndex++;
	}

	private void EndDialogue()
	{
		_dialogueActive     = false;
		Player.IsInDialogue = false;

		_playerCamera.Current    = true;
		_cinematicCamera.Current = false;
		_dialogueUI.Visible      = false;
		_promptLabel.Visible     = _playerInRange;

		Input.MouseMode = Input.MouseModeEnum.Captured;

		TeleportOtherNPCs();
		GameManager.Instance?.OnInteractionComplete();
	}

	private void TeleportOtherNPCs()
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();

		var others = new List<NPC>();
		foreach (Node sibling in GetParent().GetChildren())
			if (sibling is NPC other && other != this)
				others.Add(other);

		var positions = new List<Vector3>();
		foreach (var npc in others)
			positions.Add(npc.GlobalPosition);

		for (int i = positions.Count - 1; i > 0; i--)
		{
			int j = (int)(rng.Randi() % (uint)(i + 1));
			(positions[i], positions[j]) = (positions[j], positions[i]);
		}

		for (int i = 0; i < others.Count; i++)
			others[i].GlobalPosition = positions[i];
	}

	private void PositionCinematicCamera()
	{
		if (_player == null) return;

		Vector3 npcPos    = GlobalPosition         + Vector3.Up * 1.1f;
		Vector3 playerPos = _player.GlobalPosition + Vector3.Up * 1.1f;
		Vector3 mid       = (npcPos + playerPos)   * 0.5f;
		float   span      = npcPos.DistanceTo(playerPos);
		Vector3 dir       = (playerPos - npcPos).Normalized();
		Vector3 side      = dir.Cross(Vector3.Up).Normalized();

		_cinematicCamera.GlobalPosition = mid + side * (span * 1.2f) + Vector3.Up * (span * 0.5f);
		_cinematicCamera.LookAt(mid, Vector3.Up);
	}

	private void FacePlayer()
	{
		if (_player == null) return;
		Vector3 dir = _player.GlobalPosition - GlobalPosition;
		dir.Y = 0;
		if (dir.LengthSquared() < 0.001f) return;
		Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0);
	}

	public override void _Process(double delta)
	{
		if (_playerInRange && !_dialogueActive)
			FacePlayer();
	}

	private void OnBodyEntered(Node3D body)
	{
		if (body is not Player player) return;
		_player        = body;
		_playerCamera  = player.GetNode<Camera3D>("Camera3D");
		_playerInRange = true;
		if (!_dialogueActive)
			_promptLabel.Visible = true;
	}

	private void OnBodyExited(Node3D body)
	{
		if (body is not Player) return;
		_playerInRange       = false;
		_promptLabel.Visible = false;
		if (_dialogueActive) EndDialogue();
	}
}
