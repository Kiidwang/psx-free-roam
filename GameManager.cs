using Godot;

public partial class GameManager : Node
{
	public static GameManager Instance { get; private set; }

	private const int   MaxHours     = 24;
	private const float MaxFog       = 0.8f;
	private const int   DayStartHour = 6;   // the day opens at 06:00 on the clock
	private const float NpcSpacing   = 0.85f; // pulls the ring of NPCs a little tighter

	private int                  _hours          = 0;
	private int                  _balance        = 100;
	private int                  _totalNPCs      = 0;
	private bool                 _copDrainActive   = false;
	private bool                 _badEndingActive  = false;
	private float                _badEndingTimer   = 0f;
	private bool                 _surgeryActive    = false;
	private Godot.Environment    _env;
	private float                _baseFog;

	// The WorldEnvironment's Environment is an embedded resource Godot keeps
	// cached across scene reloads, so its mutated fog/saturation would leak into
	// the next run. Capture the authored values once and restore them each _Ready.
	private static bool  _envBaseCaptured = false;
	private static float _envBaseFog;
	private static Color _envBaseFogColor;

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
	private Label                _clockLabel;

	// HUD is hidden behind the front-end menu until the intro completes.
	private CanvasLayer          _hudLayer;

	// World changes (NPC repositioning) never happen mid-conversation or in
	// plain view — they're queued and applied under a brief fade transition.
	private enum WorldFade { None, Out, In }
	private bool      _inConversation    = false;
	private bool      _repositionPending = false;
	private WorldFade _worldFade         = WorldFade.None;
	private float     _worldFadeAlpha    = 0f;
	private ColorRect _worldFadeRect;

	// Cross-NPC memory (feature 3 — NPCs react to who you've visited)
	private readonly System.Collections.Generic.HashSet<string> _flags = new();

	private Label                _balanceLabel;
	private ColorRect            _badFadeRect;
	private Label                _badEndingLabel;
	private Panel                _surgeryPanel;
	private Label                _surgeryLabel;
	private CanvasLayer          _gameOverLayer;
	private Panel                _gameOverPanel;
	private Label                _gameOverLabel;
	private bool                 _gameOver = false;

	private VBoxContainer        _notifContainer;
	private readonly System.Collections.Generic.List<(Label label, float timer)> _notifs = new();

	public override void _Ready()
	{
		Instance = this;

		var worldEnv = GetTree().Root.GetNode<WorldEnvironment>("Root/WorldEnvironment");
		_env = worldEnv.Environment;

		if (!_envBaseCaptured)
		{
			_envBaseFog      = _env.FogDensity;
			_envBaseFogColor = _env.FogLightColor;
			_envBaseCaptured = true;
		}
		// Restore the authored environment so a restart starts clear, not foggy.
		_env.FogDensity           = _envBaseFog;
		_env.FogLightColor        = _envBaseFogColor;
		_env.AdjustmentEnabled    = false;
		_env.AdjustmentSaturation = 1f;

		_baseFog      = _envBaseFog;
		_hourFog      = _baseFog;
		_baseFogColor = _envBaseFogColor;

		_sun             = GetTree().Root.GetNode<DirectionalLight3D>("Root/DirectionalLight3D");
		_baseSunRotation = _sun.RotationDegrees;
		_baseSunColor    = _sun.LightColor;
		_baseSunEnergy   = _sun.LightEnergy;

		BuildHUD();
		BuildTransitionOverlay();
		UpdateHUD();
		UpdateAmbience();
		CallDeferred(MethodName.CountTotalNPCs);
		CallDeferred(MethodName.ShuffleNPCPositions);

		// Front-end: menu -> fade -> tutorial -> (optional) Doctor intro.
		// Freezes the world and hands control back via OnIntroComplete.
		AddChild(new MainMenu());
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
			npcs[i].GlobalPosition = new Vector3(
				positions[i].X * NpcSpacing,
				npcs[i].GlobalPosition.Y,
				positions[i].Z * NpcSpacing);
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

		// Faded world-shift: darken, reposition the survivors while hidden, lift.
		switch (_worldFade)
		{
			case WorldFade.Out:
				_worldFadeAlpha = Mathf.MoveToward(_worldFadeAlpha, 1f, (float)delta / 0.4f);
				_worldFadeRect.Color = new Color(0f, 0f, 0f, _worldFadeAlpha);
				if (_worldFadeAlpha >= 1f)
				{
					ApplyReposition();
					_worldFade = WorldFade.In;
				}
				break;

			case WorldFade.In:
				_worldFadeAlpha = Mathf.MoveToward(_worldFadeAlpha, 0f, (float)delta / 0.4f);
				_worldFadeRect.Color = new Color(0f, 0f, 0f, _worldFadeAlpha);
				if (_worldFadeAlpha <= 0f)
				{
					_worldFade = WorldFade.None;
					if (!_badEndingActive && !_surgeryActive && _hours < MaxHours)
					{
						Player.IsInDialogue = false;
						Input.MouseMode     = Input.MouseModeEnum.Captured;
					}
					if (_repositionPending) StartWorldTransition();
				}
				break;
		}
	}

	private void ShowNotification(string text)
	{
		var label = new Label();
		label.Text = text;
		label.AddThemeFontSizeOverride("font_size", 18);
		label.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f, 1f));
		label.HorizontalAlignment = HorizontalAlignment.Right;
		_notifContainer.AddChild(label);
		_notifs.Add((label, 4f));
	}

	public void OnInteractionComplete()
	{
		_inConversation = false;

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
		{
			ShowGameOver();
			return;
		}

		// Now that the conversation is over, fold in any world change it caused.
		if (_repositionPending && _worldFade == WorldFade.None)
			StartWorldTransition();
	}

	public void OnConversationStarted() => _inConversation = true;

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

		// Don't move the survivors now — queue it for a faded transition so the
		// world never visibly rearranges mid-conversation or in plain sight.
		RequestReposition();

		int remaining = 0;
		foreach (var node in GetTree().GetNodesInGroup("npc"))
			if (node is NPC npc && !npc.IsDespawning) remaining++;

		if (remaining <= 2 && _hours >= 23)
			StartBadEnding();
	}

	// Queue a reposition; run it immediately only if the player is free-roaming
	// and no transition is already playing. Otherwise it waits for a clean beat.
	private void RequestReposition()
	{
		if (_inConversation || _worldFade != WorldFade.None)
		{
			_repositionPending = true;
			return;
		}
		StartWorldTransition();
	}

	private void StartWorldTransition()
	{
		_repositionPending  = false;
		_worldFadeAlpha     = 0f;
		_worldFade          = WorldFade.Out;
		Player.IsInDialogue = true; // hold the player still while the world shifts
	}

	// Settle the remaining NPCs into a tighter ring as their number dwindles.
	// Called only at the dark peak of the transition, so the move is unseen.
	private void ApplyReposition()
	{
		var active = new System.Collections.Generic.List<NPC>();
		foreach (var node in GetTree().GetNodesInGroup("npc"))
			if (node is NPC npc && !npc.IsDespawning) active.Add(npc);
		if (_totalNPCs == 0 || active.Count == 0) return;

		float t      = (float)active.Count / _totalNPCs;
		float radius = Mathf.Lerp(5f, 22f * NpcSpacing, t);

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
		if (_timeBar != null)    _timeBar.Visible    = true;
		if (_clockLabel != null) _clockLabel.Visible = true;
		ShowNotification("You understand now.");
	}

	public void SetFlag(string flag) => _flags.Add(flag);
	public bool HasFlag(string flag) => _flags.Contains(flag);

	private void BuildHUD()
	{
		_hudLayer = new CanvasLayer { Layer = 129 };
		var layer = _hudLayer;
		AddChild(layer);

		// No hour counter — the player is never told time exists.
		// Balance counter — top left (money is the one cost the player is allowed to see)
		_balanceLabel = new Label();
		_balanceLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
		_balanceLabel.OffsetLeft   =  16f;
		_balanceLabel.OffsetTop    =  16f;
		_balanceLabel.OffsetRight  =  300f;
		_balanceLabel.OffsetBottom =  52f;
		_balanceLabel.AddThemeFontSizeOverride("font_size", 22);
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

		// Military-time readout under the bar — shares the bar's visibility,
		// so it only appears once the truth (and the clock) is known.
		_clockLabel = new Label();
		_clockLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
		_clockLabel.OffsetTop    = 10f;
		_clockLabel.OffsetBottom = 40f;
		_clockLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_clockLabel.AddThemeFontSizeOverride("font_size", 24);
		_clockLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.78f, 0.95f));
		_clockLabel.Visible = false;
		layer.AddChild(_clockLabel);

		// Bad ending — slow fade + message
		_badFadeRect = new ColorRect();
		_badFadeRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_badFadeRect.Color = new Color(0f, 0f, 0f, 0f);
		layer.AddChild(_badFadeRect);

		_badEndingLabel = new Label();
		_badEndingLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_badEndingLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_badEndingLabel.VerticalAlignment   = VerticalAlignment.Center;
		_badEndingLabel.AddThemeFontSizeOverride("font_size", 40);
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
		_surgeryLabel.AddThemeFontSizeOverride("font_size", 35);
		_surgeryLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
		_surgeryPanel.AddChild(_surgeryLabel);

		// Game over overlay
		// Game over lives on its own layer, above the HUD, with a fully opaque
		// background — when shown it blanks the entire screen behind it.
		_gameOverLayer = new CanvasLayer { Layer = 140 };
		_gameOverLayer.Visible = false;
		AddChild(_gameOverLayer);

		_gameOverPanel = new Panel();
		_gameOverPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var style = new StyleBoxFlat();
		style.BgColor = new Color(0f, 0f, 0f, 1f);
		_gameOverPanel.AddThemeStyleboxOverride("panel", style);
		_gameOverLayer.AddChild(_gameOverPanel);

		// "GAME OVER" headline, sitting above the parting message.
		var gameOverTitle = new Label();
		gameOverTitle.SetAnchorsPreset(Control.LayoutPreset.Center);
		gameOverTitle.OffsetLeft   = -400f;
		gameOverTitle.OffsetRight  =  400f;
		gameOverTitle.OffsetTop    = -150f;
		gameOverTitle.OffsetBottom =  -60f;
		gameOverTitle.HorizontalAlignment = HorizontalAlignment.Center;
		gameOverTitle.VerticalAlignment   = VerticalAlignment.Center;
		gameOverTitle.AddThemeFontSizeOverride("font_size", 72);
		gameOverTitle.AddThemeColorOverride("font_color", new Color(1f, 0.3f, 0.3f, 1f));
		gameOverTitle.Text = "GAME OVER";
		_gameOverPanel.AddChild(gameOverTitle);

		_gameOverLabel = new Label();
		_gameOverLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
		_gameOverLabel.OffsetLeft   = -400f;
		_gameOverLabel.OffsetRight  =  400f;
		_gameOverLabel.OffsetTop    = -20f;
		_gameOverLabel.OffsetBottom = 140f;
		_gameOverLabel.HorizontalAlignment = HorizontalAlignment.Center;
		_gameOverLabel.VerticalAlignment   = VerticalAlignment.Center;
		_gameOverLabel.AddThemeFontSizeOverride("font_size", 26);
		_gameOverLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f, 1f));
		_gameOverLabel.Text = "The fog settles.\n\nYou saw everyone you could.\nThat was enough.";
		_gameOverPanel.AddChild(_gameOverLabel);

		var restartLabel = new Label();
		restartLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
		restartLabel.OffsetLeft   = -400f;
		restartLabel.OffsetRight  =  400f;
		restartLabel.OffsetTop    =  170f;
		restartLabel.OffsetBottom =  220f;
		restartLabel.HorizontalAlignment = HorizontalAlignment.Center;
		restartLabel.VerticalAlignment   = VerticalAlignment.Center;
		restartLabel.AddThemeFontSizeOverride("font_size", 24);
		restartLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 1f));
		restartLabel.Text = "Press  [ R ]  to begin again";
		_gameOverPanel.AddChild(restartLabel);

		// Despawn notification log — bottom-right
		_notifContainer = new VBoxContainer();
		_notifContainer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
		_notifContainer.OffsetLeft   = -360f;
		_notifContainer.OffsetTop    = -240f;
		_notifContainer.OffsetRight  = -16f;
		_notifContainer.OffsetBottom = -16f;
		_notifContainer.Alignment    = BoxContainer.AlignmentMode.End;
		layer.AddChild(_notifContainer);

		_hudLayer.Visible = false; // revealed by OnIntroComplete once the menu/intro ends
	}

	// Called by MainMenu when the player leaves the front-end and the day begins.
	public void OnIntroComplete(bool revealTruth)
	{
		if (_hudLayer != null) _hudLayer.Visible = true;
		if (revealTruth) RevealTruth();
	}

	// Full-screen black used for the world-shift fade. Its own layer (above the
	// HUD) so it stays independent of the HUD's menu-time visibility toggle.
	private void BuildTransitionOverlay()
	{
		var fadeLayer = new CanvasLayer { Layer = 130 };
		AddChild(fadeLayer);

		_worldFadeRect = new ColorRect();
		_worldFadeRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_worldFadeRect.Color       = new Color(0f, 0f, 0f, 0f);
		_worldFadeRect.MouseFilter = Control.MouseFilterEnum.Ignore;
		fadeLayer.AddChild(_worldFadeRect);
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

		// Clock as 24-hour military time: the day opens at 06:00 and each
		// elapsed hour advances it, wrapping past midnight.
		if (_clockLabel != null)
		{
			int hour = (DayStartHour + _hours) % 24;
			_clockLabel.Text = $"{hour:00}:00";
		}
	}

	private void ShowGameOver()
	{
		if (_gameOver) return;
		_gameOver = true;

		// Blank everything else, then raise the opaque game-over layer.
		if (_hudLayer != null)       _hudLayer.Visible      = false;
		if (_worldFadeRect != null)  _worldFadeRect.Visible = false;
		_gameOverLayer.Visible = true;

		Player.IsInDialogue = true; // no further gameplay input
		Input.MouseMode     = Input.MouseModeEnum.Visible;
	}

	// While the game is over, the only input that does anything is R to restart.
	public override void _Input(InputEvent @event)
	{
		if (!_gameOver) return;
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.R)
			RestartGame();
		GetViewport().SetInputAsHandled();
	}

	private void RestartGame()
	{
		_gameOver           = false;
		Player.IsInDialogue = false;
		Input.MouseMode     = Input.MouseModeEnum.Captured;
		// Defer the scene swap — reloading mid-input is unreliable.
		CallDeferred(MethodName.DoReload);
	}

	private void DoReload()
	{
		var tree = GetTree();
		if (tree.ReloadCurrentScene() != Error.Ok)
			tree.ChangeSceneToFile("res://World.tscn");
	}
}
