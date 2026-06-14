using Godot;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

public partial class NPC : Node3D
{
	[Export] public string ModelPath      = "";
	[Export] public float  ModelScale     = 0.47f;
	[Export] public string DialogueFile   = "";
	[Export] public bool   IsGiver        = false;
	[Export] public int    MaxNoResponses = 3;
	[Export] public bool   IsFragile      = false;

	private Area3D      _interactionArea;
	private Label3D     _promptLabel;
	private Label3D     _floatLabel;
	private Camera3D    _cinematicCamera;
	private CanvasLayer _dialogueUI;
	private Label       _dialogueLabel;
	private Label       _continueLabel;
	private Label       _choiceLabel;

	private Node3D   _player      = null;
	private Camera3D _playerCamera = null;

	private bool  _playerInRange  = false;
	private bool  _dialogueActive = false;
	private int   _noCount        = 0;
	private int   _yesCount       = 0;
	private bool  _despawning     = false;
	private float _despawnTimer   = 0f;

	private List<DialogueEntry> _dialogues     = new();
	private int                 _dialogueIndex = 0;
	private bool                _awaitingChoice = false;
	private bool                _altActive      = false;

	private bool Exhausted => _dialogues.Count > 0 && _dialogueIndex >= _dialogues.Count;

	// Short key derived from the dialogue file ("res://Dialogues/gf.json" -> "gf").
	// Used for cross-NPC memory flags like "helped_wife" / "lost_grandma".
	public string NpcKey => System.IO.Path.GetFileNameWithoutExtension(DialogueFile);

	private class DialogueEntry
	{
		[JsonPropertyName("text")]         public string Text        { get; set; } = "";
		[JsonPropertyName("yes_response")] public string YesResponse { get; set; } = "";
		[JsonPropertyName("no_response")]  public string NoResponse  { get; set; } = "";
		[JsonPropertyName("type")]         public string Type        { get; set; } = "";
		[JsonPropertyName("amount")]       public int    Amount      { get; set; } = 0;
		[JsonPropertyName("hours")]        public int    Hours       { get; set; } = 0;

		// Optional reaction to prior choices (feature 3). When "requires" flag is set,
		// the alt_* strings (if present) replace the default lines.
		[JsonPropertyName("requires")]         public string Requires { get; set; } = "";
		[JsonPropertyName("alt_text")]         public string AltText  { get; set; } = "";
		[JsonPropertyName("alt_yes_response")] public string AltYes   { get; set; } = "";
		[JsonPropertyName("alt_no_response")]  public string AltNo    { get; set; } = "";
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
		AddToGroup("npc");
		UpdatePromptLabel();

		if (ModelPath != "")
			LoadModel();
	}

	private void UpdatePromptLabel()
	{
		if (Exhausted)
		{
			_promptLabel.Text     = "...";
			_promptLabel.Modulate = new Color(0.5f, 0.5f, 0.5f, 1f);
			return;
		}

		if (_dialogues.Count == 0) return;

		var first = _dialogues[0];
		string moneyTag = first.Type == "time_trade"
			? $"-{first.Hours}h"
			: IsGiver ? $"+${first.Amount}" : $"-${first.Amount}";

		_promptLabel.Text = GetDisplayName();

		float t = MaxNoResponses > 0 ? (float)_noCount / MaxNoResponses : 0f;
		_promptLabel.Modulate = t switch
		{
			0f              => new Color(0.2f, 1f,   0.2f, 1f),
			< 0.5f          => new Color(1f,   1f,   0.2f, 1f),
			_               => new Color(1f,   0.2f, 0.2f, 1f)
		};
	}

	public string GetDisplayName() => DialogueFile switch
	{
		var f when f.Contains("mom")      => "Mother",
		var f when f.Contains("dad")      => "Father",
		var f when f.Contains("wife")     => "Wife",
		var f when f.Contains("grandma")  => "Grandmother",
		var f when f.Contains("boss")     => "Your Boss",
		var f when f.Contains("friend")   => "Your Friend",
		var f when f.Contains("doctor")   => "The Doctor",
		var f when f.Contains("gf")       => "Her",
		var f when f.Contains("rich_guy") => "The Old Man",
		var f when f.Contains("criminal1")=> "Him",
		var f when f.Contains("cop")      => "The Officer",
		_                                 => "Someone"
	};

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
		if (_dialogues.Count == 0 || Exhausted)
		{
			_dialogueLabel.Text    = "...";
			_choiceLabel.Visible   = false;
			_continueLabel.Visible = true;
			_awaitingChoice        = false;
			return;
		}

		var entry = _dialogues[_dialogueIndex];
		_altActive = entry.Requires != ""
			&& (GameManager.Instance?.HasFlag(entry.Requires) ?? false)
			&& entry.AltText != "";

		_dialogueLabel.Text    = _altActive ? entry.AltText : entry.Text;
		_choiceLabel.Visible   = true;
		_continueLabel.Visible = false;
		_awaitingChoice        = true;
	}

	private void HandleChoice(bool yes)
	{
		if (_dialogues.Count == 0) return;

		var entry = _dialogues[_dialogueIndex];
		string yesResp = _altActive && entry.AltYes != "" ? entry.AltYes : entry.YesResponse;
		string noResp  = _altActive && entry.AltNo  != "" ? entry.AltNo  : entry.NoResponse;
		_dialogueLabel.Text    = yes ? yesResp : noResp;
		_choiceLabel.Visible   = false;
		_continueLabel.Visible = true;
		_awaitingChoice        = false;

		// The Doctor's reveal fires regardless of the player's answer (feature 6)
		if (entry.Type == "truth_reveal")
			GameManager.Instance?.RevealTruth();

		if (yes)
		{
			_yesCount++;

			// Remember who the player chose to help, for other NPCs to react to (feature 3)
			GameManager.Instance?.SetFlag($"helped_{NpcKey}");
			if (DialogueFile.Contains("criminal"))
				GameManager.Instance?.SetFlag("took_criminal_money");

			if (entry.Type == "money")
			{
				int delta = IsGiver ? entry.Amount : -entry.Amount;
				GameManager.Instance?.OnMoneyChanged(delta);
			}
			else if (entry.Type == "time_trade")
			{
				GameManager.Instance?.OnTimeTrade(entry.Hours);
			}
			else if (entry.Type == "surgery_offer")
			{
				GameManager.Instance?.OnSurgeryChoice(accepted: true);
			}

			if (_yesCount == 2)
			{
				if (DialogueFile.Contains("gf"))
					GameManager.Instance?.DespawnByDialogue("wife");
				else if (DialogueFile.Contains("criminal"))
					GameManager.Instance?.ActivateCopDrain();
			}
		}
		else
		{
			if (entry.Type == "surgery_offer")
			{
				GameManager.Instance?.OnSurgeryChoice(accepted: false);
			}
			else
			{
				_noCount++;
				if (_noCount >= MaxNoResponses)
					TriggerDespawn();
			}
		}

		_dialogueIndex++;
		UpdatePromptLabel();
	}

	public void TriggerDespawn()
	{
		if (_despawning) return;
		_despawning = true;
		_interactionArea.SetDeferred("monitoring", false);
		_promptLabel.Visible = false;
		if (_dialogueActive) EndDialogue();

		_floatLabel = new Label3D();
		_floatLabel.Text      = $"{GetDisplayName()} has accepted the fog";
		_floatLabel.FontSize  = 28;
		_floatLabel.Modulate  = new Color(1f, 1f, 1f, 1f);
		_floatLabel.Position  = Vector3.Up * 2.2f;
		_floatLabel.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
		_floatLabel.NoDepthTest = true;
		AddChild(_floatLabel);
	}

	public void ApplyPassiveDrain()
	{
		_noCount++;
		UpdatePromptLabel();
		if (_noCount >= MaxNoResponses)
			TriggerDespawn();
	}

	private void EndDialogue()
	{
		bool wasExhausted = Exhausted;

		_dialogueActive     = false;
		Player.IsInDialogue = false;

		_playerCamera.Current    = true;
		_cinematicCamera.Current = false;
		_dialogueUI.Visible      = false;
		_promptLabel.Visible     = _playerInRange;

		Input.MouseMode = Input.MouseModeEnum.Captured;

		GameManager.Instance?.OnInteractionComplete();

		if (wasExhausted)
			ApplyPassiveDrain();
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
		if (_despawning)
		{
			_despawnTimer += (float)delta;
			float t = Mathf.Clamp(_despawnTimer / 1.5f, 0f, 1f);
			Scale = Vector3.One * (1f - t);

			if (_floatLabel != null)
			{
				_floatLabel.Position += Vector3.Up * (float)delta * 0.6f;
				_floatLabel.Modulate  = new Color(1f, 1f, 1f, 1f - t);
			}

			if (_despawnTimer >= 1.5f)
			{
				GameManager.Instance?.OnNPCDespawned(this);
				QueueFree();
			}
			return;
		}

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
