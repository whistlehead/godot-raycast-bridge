using Godot;
using PhysicsQueryBridge;

/// <summary>
/// Attach to a Node3D in your scene. Press Space to fire a ray straight down.
/// </summary>
public partial class RaycastExample : Node3D
{
    [Export] public float RayLength = 100f;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo
            && key.Keycode == Key.Space)
        {
            FireRayDown();
        }
    }

    private void FireRayDown()
    {
        var space = GetWorld3D().DirectSpaceState;
        Vector3 from = GlobalPosition;
        Vector3 to   = from + Vector3.Down * RayLength;

        var result = RaycastBridge.IntersectRay(space, from, to, collisionMask: 0xFFFFFFFF);

        if (RaycastBridge.GetHit(result, 0))
        {
            Vector3 pos      = RaycastBridge.GetPosition(result, 0);
            Vector3 normal   = RaycastBridge.GetNormal(result, 0);
            ulong   id       = RaycastBridge.GetColliderId(result, 0);
            GodotObject obj  = InstanceFromId(id);

            GD.Print($"[RaycastTest] HIT");
            GD.Print($"  Position : {pos}");
            GD.Print($"  Normal   : {normal}");
            GD.Print($"  Collider : {obj?.GetClass() ?? "unknown"} (id={id})");
        }
        else
        {
            GD.Print("[RaycastTest] No hit.");
        }
    }
}
