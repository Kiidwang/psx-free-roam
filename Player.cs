using Godot;

public partial class Player : CharacterBody3D
{
	public static bool IsInDialogue = false;

	[Export] public float Speed = 2.5f;
	[Export] public float MouseSensitivity = 0.003f;

	private Camera3D _camera;
	private Node3D   _model;
	private AnimationPlayer _animPlayer;

	private string _currentAnim = "";

	// Camera orbits independently from the player body
	private float _cameraYaw    = 0f;
	private float _armRadius;
	private float _defaultElevation;

	// Frozen-camera collision state
	private Vector3 _frozenWorldPos;
	private bool    _isFrozen   = false;
	private float   _clearTimer = 0f;
	private const float ClearDelay = 0.5f;

	// Turn animation state
	private float _mouseDeltaX       = 0f;
	private float _turnDir           = 0f;
	private float _turnHoldTimer     = 0f;
	private bool  _movingForwardBack = false;
	private bool  _isMoving          = false;
	private float _lockedMoveYaw     = 0f;
	private const float TurnPixelThreshold = 2f;   // raw pixels needed to trigger
	private const float TurnHoldDuration   = 0.18f; // seconds to hold anim after mouse stops

	public override void _Ready()
	{
		_camera     = GetNode<Camera3D>("Camera3D");
		_model      = GetNode<Node3D>("Model");
		_animPlayer = GetNode<AnimationPlayer>("Model/AnimationPlayer");

		foreach (StringName name in _animPlayer.GetAnimationList())
			_animPlayer.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;

		float camY        = _camera.Position.Y;
		float camZ        = _camera.Position.Z;
		_armRadius        = Mathf.Sqrt(camY * camY + camZ * camZ);
		_defaultElevation = Mathf.Atan2(camY, camZ);

		// Face model away from the default camera position on startup
		_model.RotationDegrees = new Vector3(0f, 180f, 0f);

		Input.MouseMode = Input.MouseModeEnum.Captured;
		PlayAnim("Idle");
	}

	public override void _Input(InputEvent @event)
	{
		if (IsInDialogue) return;
		if (@event is InputEventMouseMotion mouseMotion)
		{
			// Camera yaw orbits independently — player body never rotates with the mouse
			_cameraYaw -= mouseMotion.Relative.X * MouseSensitivity;
			_mouseDeltaX += mouseMotion.Relative.X;
		}

		if (@event.IsActionPressed("ui_cancel"))
			Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (IsInDialogue)
		{
			Velocity = Vector3.Zero;
			PlayAnim("Idle");
			return;
		}

		Vector3 velocity = Velocity;

		if (!IsOnFloor())
			velocity.Y -= 9.8f * (float)delta;

		float moveX = 0f;
		float moveZ = 0f;

		if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))    moveZ -= 1f;
		if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))  moveZ += 1f;
		if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))  moveX -= 1f;
		if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) moveX += 1f;

		// Forward/backward always takes priority — no diagonal movement
		if (moveZ != 0f) moveX = 0f;

		// Lock movement yaw the moment W/S is first pressed
		bool wasMoving = _isMoving;
		_isMoving = moveX != 0f || moveZ != 0f;
		_movingForwardBack = moveZ != 0f;

		// Lock the yaw the moment any movement starts
		if (_isMoving && !wasMoving)
			_lockedMoveYaw = _cameraYaw;

		// All movement uses the locked yaw — camera can't redirect movement mid-stride
		float yaw = _isMoving ? _lockedMoveYaw : _cameraYaw;
		var forward = new Vector3(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
		var right   = new Vector3( Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
		Vector3 direction = (forward * (-moveZ) + right * moveX).Normalized();

		if (direction != Vector3.Zero)
		{
			velocity.X = direction.X * Speed;
			velocity.Z = direction.Z * Speed;
		}
		else
		{
			velocity.X = Mathf.MoveToward(velocity.X, 0, Speed);
			velocity.Z = Mathf.MoveToward(velocity.Z, 0, Speed);
		}

		Velocity = velocity;
		MoveAndSlide();

		// Latch turn direction when mouse moves enough; hold briefly after it stops
		if (Mathf.Abs(_mouseDeltaX) >= TurnPixelThreshold)
		{
			_turnDir      = Mathf.Sign(_mouseDeltaX);
			_turnHoldTimer = TurnHoldDuration;
		}
		else if (_turnHoldTimer > 0f)
		{
			_turnHoldTimer -= (float)delta;
		}
		else
		{
			_turnDir = 0f;
		}
		_mouseDeltaX = 0f;

		PlayAnim(PickAnimation(moveX, moveZ, _turnDir));
	}

	public override void _Process(double delta)
	{
		if (IsInDialogue) return;
		// Only rotate the model with the camera when not moving forward/backward
		if (!_isMoving)
			_model.Rotation = new Vector3(0f, _cameraYaw + Mathf.Pi, 0f);
		UpdateCameraCollision((float)delta);
	}

	private void UpdateCameraCollision(float delta)
	{
		Vector3 lookAtWorld  = GlobalPosition + Vector3.Up;
		Vector3 desiredWorld = DefaultCameraWorldPos();

		var space = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(lookAtWorld, desiredWorld);
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		bool blocked = space.IntersectRay(query).Count > 0;

		if (blocked)
		{
			if (!_isFrozen)
			{
				_frozenWorldPos = _camera.GlobalPosition;
				_isFrozen = true;
			}
			_clearTimer = 0f;
		}
		else if (_isFrozen)
		{
			_clearTimer += delta;
			if (_clearTimer >= ClearDelay)
			{
				_frozenWorldPos = _frozenWorldPos.Lerp(desiredWorld, 5f * delta);
				if (_frozenWorldPos.DistanceTo(desiredWorld) < 0.05f)
					_isFrozen = false;
			}
		}

		_camera.GlobalPosition = _isFrozen ? _frozenWorldPos : desiredWorld;
		_camera.LookAt(lookAtWorld, Vector3.Up);
	}

	private Vector3 DefaultCameraWorldPos()
	{
		float y  = _armRadius * Mathf.Sin(_defaultElevation);
		float hz = _armRadius * Mathf.Cos(_defaultElevation);
		return GlobalPosition + new Vector3(
			Mathf.Sin(_cameraYaw) * hz,
			y,
			Mathf.Cos(_cameraYaw) * hz);
	}

	private void PlayAnim(string anim)
	{
		if (anim == _currentAnim) return;
		_animPlayer.Play(anim, 0.15);
		_currentAnim = anim;
	}

	private static string PickAnimation(float moveX, float moveZ, float turnDir)
	{
		bool fwd   = moveZ < -0.01f;
		bool back  = moveZ >  0.01f;
		bool left  = moveX < -0.01f;
		bool right = moveX >  0.01f;

		if (fwd)   return "WalkForward";
		if (back)  return "WalkBackward";
		if (left)  return "StrafeLeft";
		if (right) return "StrafeRight";

		if (turnDir >  0.5f) return "TurnRight";
		if (turnDir < -0.5f) return "TurnLeft";

		return "Idle";
	}
}
