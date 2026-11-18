using Godot;

internal sealed class DiveCamera
{
    public readonly record struct Frame(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, float Time);
    public const float FlightDuration = 60;
    public const float TanHalfFov = 0.6f;
    public const float Near = 0.1f;
    private Vector3 _position;
    private Vector3 _velocity;
    private Vector2 _look;
    private Vector2 _lookFirst;
    private Vector2 _lookSecond;
    private float _yaw;
    private float _pitch;
    private float _time;

    public Frame Current
    {
        get
        {
            Vector3 right = new(Mathf.Cos(_yaw), 0, Mathf.Sin(_yaw));
            float horizontal = Mathf.Cos(_pitch);
            Vector3 forward = new(horizontal * right.Z, Mathf.Sin(_pitch), -horizontal * right.X);
            return new(_position, right, right.Cross(forward), forward, _time);
        }
    }

    public void Advance(float dt) => _time += dt;

    public void Aim(GardenWorld.View view)
    {
        _position = view.Position;
        Vector3 direction = view.Target - view.Position;
        _yaw = Mathf.Atan2(direction.X, -direction.Z);
        _pitch = Mathf.Atan2(direction.Y, new Vector2(direction.X, direction.Z).Length());
        _velocity = Vector3.Zero;
        _look = _lookFirst = _lookSecond = Vector2.Zero;
    }

    public void Look(Vector2 pixels) => _look += pixels * 0.0022f;

    public void Swim(GardenWorld world, Vector3 input, float dt, bool sprint)
    {
        float smoothing = 1 - Mathf.Exp(-30 * dt);
        _lookFirst += _look;
        _look = Vector2.Zero;
        Vector2 intermediate = _lookFirst * smoothing;
        _lookFirst -= intermediate;
        _lookSecond += intermediate;
        Vector2 rotation = _lookSecond * smoothing;
        _lookSecond -= rotation;
        _yaw += rotation.X;
        _pitch = Mathf.Clamp(_pitch - rotation.Y, -1.55f, 1.55f);

        var frame = Current;
        Vector3 desired = (frame.Right * input.X + Vector3.Up * input.Y + frame.Forward * input.Z)
            * (sprint ? 26 : 7);
        _velocity = _velocity.Lerp(desired, 1 - Mathf.Exp(-4.5f * dt));
        Vector3 target = _position + _velocity * dt;
        var horizontal = new Vector2(target.X, target.Z).LimitLength(150);
        target = new(horizontal.X, Mathf.Clamp(target.Y, -52, -1.2f), horizontal.Y);
        Vector3 movement = target - _position;
        int steps = Mathf.FloorToInt(movement.Length() / 0.4f) + 1;
        Vector3 step = movement / steps;
        for (int i = 0; i < steps; i++)
        {
            _position += step;
            for (int j = 0; j < 2; j++)
            {
                float penetration = world.DistanceToRock(_position) + 0.5f;
                if (penetration <= 0)
                    break;
                Vector3 normal = new(Gradient(world, Vector3.Right), Gradient(world, Vector3.Up),
                    Gradient(world, Vector3.Back));
                float length = normal.Length();
                if (length < 0.075f)
                    break;
                normal /= length;
                _position -= normal * penetration;
                _velocity -= normal * Mathf.Max(0, _velocity.Dot(normal));
            }
        }
    }

    public void Fly(GardenWorld world, float seconds)
    {
        float t = Mathf.Clamp(seconds / FlightDuration, 0, 1);
        t = t * t * (3 - 2 * t) * 0.25f + t * 0.75f;
        var lengths = world.FlightLengths;
        t *= lengths.Length - 1;
        int index = Mathf.Min((int)t, lengths.Length - 2);
        float parameter = Mathf.Lerp(lengths[index], lengths[index + 1], t - index);
        var path = world.Flight;
        int segment = Mathf.Min((int)parameter, path.Length - 2);
        float u = parameter - segment;
        var a = path[Mathf.Max(0, segment - 1)];
        var b = path[segment];
        var c = path[segment + 1];
        var d = path[Mathf.Min(path.Length - 1, segment + 2)];
        Aim(new(b.Position.CubicInterpolate(c.Position, a.Position, d.Position, u),
            b.Target.CubicInterpolate(c.Target, a.Target, d.Target, u)));
    }

    private float Gradient(GardenWorld world, Vector3 axis) =>
        world.DistanceToRock(_position + axis * 0.375f) - world.DistanceToRock(_position - axis * 0.375f);
}
