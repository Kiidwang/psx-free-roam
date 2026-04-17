using Godot;

public partial class NPC : Node3D
{
	[Export] public string ModelPath    = "";
	[Export] public float  ModelScale   = 0.47f;
	[Export] public string DialogueText = "...";

	private Area3D      _interactionArea;
	private Label3D     _promptLabel;
	private Camera3D    _cinematicCamera;
	private CanvasLayer _dialogueUI;
	private Label       _dialogueLabel;

	private Node3D   _player       = null;
	private Camera3D _playerCamera = null;

	private bool _playerInRange  = false;
	private bool _dialogueActive = false;

	public override void _Ready()
	{
		_interactionArea = GetNode<Area3D>("InteractionArea");
		_promptLabel     = GetNode<Label3D>("PromptLabel");
		_cinematicCamera = GetNode<Camera3D>("CinematicCamera");
		_dialogueUI      = GetNode<CanvasLayer>("DialogueUI");
		_dialogueLabel   = GetNode<Label>("DialogueUI/Panel/DialogueText");

		_interactionArea.BodyEntered += OnBodyEntered;
		_interactionArea.BodyExited  += OnBodyExited;

		_cinematicCamera.Current = false;
		_promptLabel.Visible     = false;
		_dialogueUI.Visible      = false;
		_dialogueUI.Layer        = 128;

		// Anchor dialogue panel to bottom of screen
		var panel = GetNode<Control>("DialogueUI/Panel");
		panel.AnchorLeft   = 0f;
		panel.AnchorRight  = 1f;
		panel.AnchorTop    = 1f;
		panel.AnchorBottom = 1f;
		panel.OffsetLeft   =  40f;
		panel.OffsetRight  = -40f;
		panel.OffsetTop    = -180f;
		panel.OffsetBottom = -20f;

		_dialogueLabel.Text = DialogueText;

		if (ModelPath != "")
			LoadModel();
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
		if (key.Keycode != Key.Space)          return;

		if (_playerInRange && !_dialogueActive)
			StartDialogue();
		else if (_dialogueActive)
			EndDialogue();
	}

	private void StartDialogue()
	{
		_dialogueActive     = true;
		Player.IsInDialogue = true;

		PositionCinematicCamera();
		_cinematicCamera.Current = true;
		_dialogueUI.Visible      = true;
		_promptLabel.Visible     = false;

		Input.MouseMode = Input.MouseModeEnum.Visible;
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

		var others = new System.Collections.Generic.List<NPC>();
		foreach (Node sibling in GetParent().GetChildren())
			if (sibling is NPC other && other != this)
				others.Add(other);

		// Collect their current positions
		var positions = new System.Collections.Generic.List<Vector3>();
		foreach (var npc in others)
			positions.Add(npc.GlobalPosition);

		// Fisher-Yates shuffle
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
		_player       = body;
		_playerCamera = player.GetNode<Camera3D>("Camera3D");
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
