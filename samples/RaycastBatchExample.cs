using Godot;
using PhysicsQueryBridge;

/// <summary>
/// Attach to a Node3D in your scene. Press Space to fire a cone of 16 rays downward,
/// each angled 30 degrees from vertical, evenly spaced around the circle.
/// </summary>
public partial class RaycastConeTest : Node3D
{
    [Export] public float RayLength   = 100f;
    [Export] public float ConeAngleDeg = 30f;
    [Export] public int   RayCount    = 16;

    private PackedFloat32Array _inBuffer;

    public override void _Ready()
    {
        _inBuffer = new PackedFloat32Array();
        _inBuffer.Resize(RayCount * 7);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo
            && key.Keycode == Key.Space)
        {
            FireConeDown();
        }
    }

    private void FireConeDown()
    {
        float angleRad   = Mathf.DegToRad(ConeAngleDeg);
        float sinAngle   = Mathf.Sin(angleRad);
        float cosAngle   = Mathf.Cos(angleRad);
        float stepRad    = Mathf.Tau / RayCount;
        Vector3 origin   = GlobalPosition;

        for (int i = 0; i < RayCount; i++)
        {
            float azimuth  = stepRad * i;
            // Direction: tilt from straight-down by ConeAngleDeg around the cone
            var direction  = new Vector3(
                sinAngle * Mathf.Cos(azimuth),
                -cosAngle,
                sinAngle * Mathf.Sin(azimuth)
            );
            RaycastBridge.PackRay(_inBuffer, i, origin, direction, RayLength);
        }

        var space   = GetWorld3D().DirectSpaceState;
        var results = RaycastBridge.IntersectRaysBatch(_inBuffer, space, RayCount, collisionMask: 0xFFFFFFFF);

        int hitCount = 0;
        for (int i = 0; i < RayCount; i++)
        {
            if (!RaycastBridge.GetHit(results, i))
                continue;

            hitCount++;
            Vector3     pos    = RaycastBridge.GetPosition(results, i);
            Vector3     normal = RaycastBridge.GetNormal(results, i);
            ulong       id     = RaycastBridge.GetColliderId(results, i);
            GodotObject obj    = InstanceFromId(id);

            GD.Print($"[ConeTest] Ray {i,2} HIT  pos={pos}  normal={normal}  collider={obj?.GetClass() ?? "unknown"} (id={id})");
        }

        GD.Print($"[ConeTest] {hitCount}/{RayCount} rays hit.");
    }
}
