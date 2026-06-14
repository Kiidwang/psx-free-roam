using Godot;

public partial class GameManager : Node
{
	public static GameManager Instance { get; private set; }

	private const int   MaxHours = 24;
	private const float MaxFog   = 0.8f;

	private int                  _hours          = 0;
	private int                  _balance        = 100;
	private int                  _totalNPCs      = 0;
	private bool                 _copDrainActive   = false;
	private bool                 _badEndingActive  = false;
	private float                _badEndingTimer   = 0f;
	private bool                 _surgeryActive    = false;
	private Godot.Environment    _env;
	private float                _baseFog;

	// Ambient day-cycle state (feature 2 — diegetic time)
	private DirectionalLight3D   _sun;
	private Vector3              _baseSunRotation;
	private Color               _baseSunColor;
	private float                _baseSunEnergy;
	private Color               _baseFogColor;

	// Fog scar state (feature 1 — fog reacts to loss)
	private float                _hourFog;
	private float                _fogPulse       = 0f;
	private float                _saturation     = 1f;

	// Hidden timer made visible only after the Doctor's reveal (features 2 + 6)
	private bool                 _truthKnown     = false;
	private Control              _timeBar;
	private ColorRect            _timeBarFill;

	// Cross-NPC memory (feature 3 — NPCs react to who you've visited)
	private readonly System.Collections.Generic.HashSet<string> _flags = new();

	private Label                _balanceLabel;
	private ColorRect            _badFadeRect;
	private Label                _badEndingLabel;
	private Panel                _surgeryPanel;
	private Label                _surgeryLabel;
	private Panel                _gameOverPanel;
	private Label                _gameOverLabel;

	private VBoxContainer        _notifContainer;
	private readonly System.Collections.Generic.List<(Label label, float timer)> _notifs = new();

	public override void _Ready()
	{
		Instance = this;

		var worldEnv = GetTree().Root.GetNode<WorldEnvironment>("Root/WorldEnvironment");
		_env     = worldEnv.Environment;
		_baseFog = _env.FogDensity;
		_hourFog = _baseFog;

		_sun             = GetTree().Root.GetNode<DirectionalLight3D>("Root/DirectionalLight3D");
		_baseSunRotation = _sun.RotationDegrees;
		_baseSunColor    = _sun.LightColor;
		_baseSunEnergy   = _sun.LightEnergy;
		_baseFogColor    = _env.FogLightColor;

		BuildHUD();
		UpdateHUD();
		UpdateAmbience();
		CallDeferred(MethodName.CountTotalNPCs);
		CallDeferred(MethodName.ShuffleNPCPositions);
	}

	private void CountTotalNPCs()
	{
		foreach (var node in GetTree().GetNodesInGroup("npc"))
			if (node is NPC) _totalNPCs++;
	}

	private void ShuffleNPCPositions()
	{
		var npcs = new System.Collections.Generic.List<NPC>();
		foreach (var node in GetTree().GetNodesInGroup("npc"))
			if (node is NPC npc) npcs.Add(npc);
		if (npcs.Count == 0) return;

		var rng = new RandomNumberGenerator();
		rng.Randomize();

		var positions = new System.Collections.Generic.List<Vector3>();
		foreach (var npc in npcs) positions.Add(npc.GlobalPosition);

		for (int i = positions.Count - 1; i > 0; i--)
		{
			int j = rng.RandiRange(0, i);
			(positions[i], positions[j]) = (positions[j], positions[i]);
		}

		for (int i = 0; i < npcs.Count; i++)
			npcs[i].GlobalPosition = new Vector3(positions[i].X, npcs[i].GlobalPosition.Y, positions[i].Z);
	}

	public override void _Process(double delta)
	{
		// Fog density = hour-driven base + a decaying surge left by each loss
		if (_fogPulse > 0f)
			_fogPulse = Mathf.MoveToward(_fogPulse, 0f, (float)delta * 0.1f);
		if (_env != null)
			_env.FogDensity = _hourFog + _fogPulse;

		if (_badEndingActive)
		{
			_badEndingTimer += (float)delta;
			float t = Mathf.Clamp(_badEndingTimer / 60f, 0f, 1f);
			_badFadeRect.Color = new Color(0f, 0f, 0f, t);
			if (_badEndingTimer >= 60f)
				_badEndingLabel.Visible = true;
		}

		for (int i = _notifs.Count - 1; i >= 0; i--)
		{
			var (label, timer) = _notifs[i];
			float newTimer = timer - (float)delta;
			if (newTimer <= 0f)
			{
				label.QueueFree();
				_notifs.RemoveAt(i);
			}
			else
			{
				float alpha = Mathf.Clamp(newTimer / 1.5f, 0f, 1f);
				label.Modulate = new Color(1f, 1f, 1f, alpha);
				_notifs[i] = (label, newTimer);
			}
		}
	}

	private void ShowNotification(string text)
	{
		var label = new Label();
		label.Text = text;
		label.AddThemeFontSizeOverride("font_size", 16);
		label.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f, 1f));
		label.HorizontalAlignment = HorizontalAlignment.Right;
		_notifContainer.AddChild(label);
		_notifs.Add((label, 4f));
	}

	public void OnInteractionComplete()
	{
		if (_hours >= MaxHours) return;

		_hours++;
		UpdateFog();
		UpdateHUD();

		if (_copDrainActive)
		{
			foreach (var node in GetTree().GetNodesInGroup("npc"))
				if (node is NPC npc && npc.IsFragile)
					npc.ApplyPassiveDrain();
		}

		if (_hours >= MaxHours)
			ShowGameOver();
	}

	public void ActivateCopDrain()
	{
		_copDrainActive = true;
	}

	public void DespawnByDialogue(string keyword)
	{
		foreach (var node in GetTree().GetNodesInGroup("npc"))
		{
			if (node is NPC npc && npc.DialogueFile.Contains(keyword))
			{
				npc.TriggerDespawn();
				return;
			}
		}
	}

	public void OnNPCDespawned(NPC dying)
	{
		ShowNotification($"{dying.GetDisplayName()} has accepted the fog.");

		_flags.Add($"lost_{dying.NpcKey}");

		// Each loss leaves a mark: a surge of fog, and the world drains a shade of colour for good
		_fogPulse = 0.18f;
		_saturation = Mathf.Max(0.25f, _saturation - 0.12f);
		_env.AdjustmentEnabled    = true;
		_env.AdjustmentSaturation = _saturation;

		var active = new System.Collections.Generic.List<NPC>();
		foreach (var node in GetTree().GetNodesInGroup("npc"))
			if (node is NPC npc && npc != dying)
				active.Add(npc);

		ShrinkNPCCircle(active);

		if (active.Count <= 2 && _hours >= 23)
			StartBadEnding();
	}

	private void ShrinkNPCCircle(System.Collections.Generic.List<NPC> active)
	{
		if (_totalNPCs == 0 || active.Count == 0) return;

		float t      = (float)active.Count / _totalNPCs;
		float radius = Mathf.Lerp(5f, 22f, t);

		float angleStep = Mathf.Tau / active.Count;
		for (int i = 0; i < active.Count; i++)
		{
			float angle = i * angleStep;
			active[i].GlobalPosition = new Vector3(
				Mathf.Sin(angle) * radius,
				active[i].GlobalPosition.Y,
				Mathf.Cos(angle) * radius
			);
		}
	}

	public void OnSurgeryChoice(bool accepted)
	{
		if (_surgeryActive) return;
		_surgeryActive      = true;
		Player.IsInDialogue = true;
		Input.MouseMode     = Input.MouseModeEnum.Visible;

		var rng     = new RandomNumberGenerator();
		rng.Randomize();
		bool survived = accepted && rng.RandiRange(1, 10) == 1;

		_surgeryLabel.Text = accepted
			? (survived
				? "The surgery worked.\n\nAgainst all odds, you have more time.\n\nThe fog lifts — just a little."
				: "The surgery failed.\n\nBut you fought for more time.\nThat mattered.")
			: "You chose to let the fog come on your terms.\n\nEveryone you visited today felt it.\nThat was enough.";

		_surgeryPanel.Visible = true;
	}

	private void StartBadEnding()
	{
		if (_badEndingActive) return;
		_badEndingActive    = true;
		Player.IsInDialogue = true;
		Input.MouseMode     = Input.MouseModeEnum.Visible;
	}

	private void UpdateFog()
	{
		// Quadratic curve — fog stays mild early, surges toward the end.
		// Applied each frame in _Process so a loss-pulse can ride on top.
		float t  = (float)_hours / MaxHours;
		_hourFog = _baseFog + (MaxFog - _baseFog) * t * t;
		UpdateAmbience();
	}

	// The only honest clock the player gets without the Doctor: the light itself.
	// The sun sinks and warms toward dusk, then the fog dims toward night.
	private void UpdateAmbience()
	{
		if (_sun == null || _env == null) return;

		float t = (float)_hours / MaxHours;

		_sun.RotationDegrees = new Vector3(
			Mathf.Lerp(_baseSunRotation.X, -3f, t),
			_baseSunRotation.Y,
			_baseSunRotation.Z);
		_sun.LightColor  = _baseSunColor.Lerp(new Color(1f, 0.5f, 0.28f), t);
		_sun.LightEnergy = Mathf.Lerp(_baseSunEnergy, 0.25f, t);

		Color dusk  = new Color(0.5f, 0.34f, 0.32f);
		Color night = new Color(0.06f, 0.06f, 0.09f);
		_env.FogLightColor = t < 0.6f
			? _baseFogColor.Lerp(dusk, t / 0.6f)
			: dusk.Lerp(night, (t - 0.6f) / 0.4f);
	}

	// Called by the Doctor — from here on the player can see time draining (feature 6).
	public void RevealTruth()
	{
		if (_truthKnown) return;
		_truthKnown = true;
		if (_timeBar != null) _timeBar.Visible = true;
		ShowNotification("You understand now.");
	}

	public void SetFlag(string flag) => _flags.Add(flag);
	public bool HasFlag(string flag) => _flags.Contains(flag);

	private void BuildHUD()
	{
		var layer = new CanvasLayer { Layer = 129 };
		AddChild(layer);

		// No hour counter — the player is never told time exists.
		// Balance counter — top left (money is the one cost the player is allowed to see)
		_balanceLabel = new Label();
		_balanceLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
		_balanceLabel.OffsetLeft   =  16f;
		_balanceLabel.OffsetTop    =  16f;
		_balanceLabel.OffsetRight  =  300f;
		_balanceLabel.OffsetBottom =  48f;
		_balanceLabel.AddThemeFontSizeOverride("font_size", 20);
		layer.AddChild(_balanceLabel);

		// Draining time bar — hidden until the Doctor reveals the truth (features 2 + 6).
		// No numbers; just a thin line at the top edge that empties as the day ends.
		_timeBar = new Control();
		_timeBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
		_timeBar.OffsetTop    = 0f;
		_timeBar.OffsetBottom = 5f;
		_timeBar.Visible      = false;
		layer.AddChild(_timeBar);

		var timeBarBg = new ColorRect();
		timeBarBg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		timeBarBg.Color = new Color(0f, 0f, 0f, 0.5f);
		_timeBar.AddChild(timeBarBg);

		_timeBarFill = new ColorRect();
		_timeBarFill.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_timeBarFill.Color = new Color(0.85f, 0.85f, 0.7f, 0.9f);
		_timeBar.AddChild(_timeBarFill);

		// Bad ending — slow fade + message
		_badFadeRect = new ColorRect();
		_badFadeRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_badFadeRect.Color = new Color(0f, 0f, 0f, 0f);
		layer.AddChild(_badFadeRect);

		_badEndingLabel = new Label();
		_badEndingLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_badEndingLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_badEndingLabel.VerticalAlignment   = VerticalAlignment.Center;
		_badEndingLabel.AddThemeFontSizeOverride("font_size", 36);
		_badEndingLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
		_badEndingLabel.Text    = "The fog takes you.\n\nYou were always alone.";
		_badEndingLabel.Visible = false;
		layer.AddChild(_badEndingLabel);

		// Surgery ending overlay
		_surgeryPanel = new Panel();
		_surgeryPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_surgeryPanel.Visible = false;
		var surgeryStyle = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.92f) };
		_surgeryPanel.AddThemeStyleboxOverride("panel", surgeryStyle);
		layer.AddChild(_surgeryPanel);

		_surgeryLabel = new Label();
		_surgeryLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_surgeryLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_surgeryLabel.VerticalAlignment   = VerticalAlignment.Center;
		_surgeryLabel.AddThemeFontSizeOverride("font_size", 32);
		_surgeryLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
		_surgeryPanel.AddChild(_surgeryLabel);

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
		_gameOverLabel.Text = "The fog settles.\n\nYou saw everyone you could.\nThat was enough.";
		_gameOverPanel.AddChild(_gameOverLabel);

		// Despawn notification log — bottom-right
		_notifContainer = new VBoxContainer();
		_notifContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
		_notifContainer.OffsetLeft   = -360f;
		_notifContainer.OffsetTop    = -240f;
		_notifContainer.OffsetRight  = -16f;
		_notifContainer.OffsetBottom = -16f;
		_notifContainer.Alignment    = BoxContainer.AlignmentMode.End;
		layer.AddChild(_notifContainer);
	}

	public void OnMoneyChanged(int delta)
	{
		_balance += delta;
		UpdateHUD();
	}

	public void OnTimeTrade(int hours)
	{
		_balance = Mathf.Max(0, _balance);
		_hours   = Mathf.Min(_hours + hours, MaxHours);
		UpdateFog();
		UpdateHUD();
		if (_hours >= MaxHours)
			ShowGameOver();
	}

	private void UpdateHUD()
	{
		bool inDebt = _balance < 0;
		_balanceLabel.AddThemeColorOverride("font_color",
			inDebt ? new Color(1f, 0.3f, 0.3f, 1f) : new Color(0.4f, 1f, 0.4f, 1f));
		_balanceLabel.Text = $"Balance: ${_balance}";

		// Drain the revealed time bar — green-ish full, reddening as it empties
		if (_timeBarFill != null)
		{
			float frac = (float)(MaxHours - _hours) / MaxHours;
			_timeBarFill.AnchorRight = Mathf.Clamp(frac, 0f, 1f);
			_timeBarFill.OffsetRight = 0f;
			_timeBarFill.Color = new Color(0.85f, 0.85f, 0.7f, 0.9f)
				.Lerp(new Color(0.85f, 0.2f, 0.2f, 0.9f), 1f - frac);
		}
	}

	private void ShowGameOver()
	{
		_gameOverPanel.Visible  = true;
		Player.IsInDialogue     = true; // freeze player
		Input.MouseMode         = Input.MouseModeEnum.Visible;
	}
}
