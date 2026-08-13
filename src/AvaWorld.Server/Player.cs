using Godot;

namespace AvaWorld.Server;

/// <summary>
/// You, in the world. A plain first-person walker — no jumping, no sprinting, no verbs.
///
/// Deliberately low-affordance, per the design: the moment the world rewards *playing*, it starts
/// competing with the conversation instead of supporting it. Walking somewhere and being there is
/// the whole interaction.
///
/// Input is bound in code rather than through the input map so the project has no editor-authored
/// configuration to keep in step with.
/// </summary>
public partial class Player : CharacterBody3D
{
    private const float WalkSpeed = 4.5f;          // a person's walking pace, not a shooter's
    private const float Gravity = 22f;
    private const float MouseSensitivity = 0.0022f;
    private const float PitchLimitDegrees = 85f;

    /// <summary>
    /// False in the headless smoke test, where the tour drives position directly. Keeps input
    /// handling, physics and mouse capture out of a run that has no window to capture into.
    /// </summary>
    public bool TakesInput { get; init; } = true;

    private Camera3D _camera = null!;
    private float _pitch;
    private bool _mouseCaptured;

    public override void _Ready()
    {
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Height = 1.8f, Radius = 0.35f },
        });

        if (!TakesInput)
            return;

        _camera = new Camera3D { Position = new Vector3(0, 0.7f, 0), Current = true };
        AddChild(_camera);

        CaptureMouse(true);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!TakesInput)
            return;

        if (@event is InputEventMouseMotion motion && _mouseCaptured)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);

            _pitch = Mathf.Clamp(
                _pitch - motion.Relative.Y * MouseSensitivity,
                Mathf.DegToRad(-PitchLimitDegrees),
                Mathf.DegToRad(PitchLimitDegrees));
            _camera.Rotation = new Vector3(_pitch, 0, 0);
            return;
        }

        // Escape gives the mouse back; clicking takes it again. Without this the window is a trap.
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            CaptureMouse(false);
        else if (@event is InputEventMouseButton { Pressed: true } && !_mouseCaptured)
            CaptureMouse(true);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!TakesInput)
            return; // the tour sets Position directly

        var velocity = Velocity;

        if (!IsOnFloor())
            velocity.Y -= Gravity * (float)delta;
        else if (velocity.Y < 0)
            velocity.Y = 0;

        var input = new Vector2(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0),
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0));

        var direction = (Transform.Basis * new Vector3(input.X, 0, input.Y)).Normalized();
        velocity.X = direction.X * WalkSpeed;
        velocity.Z = direction.Z * WalkSpeed;

        Velocity = velocity;
        MoveAndSlide();

        // Fell off the edge. Put them back rather than let them accelerate into nothing — the
        // layout is still changing shape and fencing every edge would be premature.
        if (Position.Y < -15f)
        {
            Position = WorldGeometry.SpawnPoint();
            Velocity = Vector3.Zero;
        }
    }

    private void CaptureMouse(bool capture)
    {
        _mouseCaptured = capture;
        Input.MouseMode = capture ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
    }
}
