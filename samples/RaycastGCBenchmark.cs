using System;
using Godot;
using PhysicsQueryBridge;

/// <summary>
/// GC pressure benchmark — attach to a Node3D in a scene that has collidable geometry below it.
///
/// Cycles through six modes, each running for RunSeconds then pausing for PauseSeconds
/// while GC.Collect() is called to reset heap state before the next run.
///
/// Modes
///   A  Native Godot — naive        new PhysicsRayQueryParameters3D per ray, per tick
///   B  Native Godot — optimised    single params object cached in _Ready, mutated per ray
///   C  Bridge batch of 1           → float[] per ray            — 200 float[] allocations/tick
///   D  Bridge batch of 5           → float[] per batch          —  40 float[] allocations/tick
///   E  Bridge batch of 10          → float[] per batch          —  20 float[] allocations/tick
///   F  Bridge batch of 20          → float[] per batch          —  10 float[] allocations/tick
///   G  Bridge single batch of 200  → float[] per tick           —   1 float[] allocation/tick
///
/// Modes A and B bracket the native baseline: A shows what typical unoptimised code
/// produces; B shows the best achievable without leaving the native API. The gap between
/// them is often larger than the gap between B and the bridge at small ray counts.
///
/// Mode C uses IntersectRaysBatch with rayCount=1 on each ray. Note it is worse than a
/// dedicated single-ray method would be (~584 bytes/ray vs ~472 bytes/ray measured with
/// the old IntersectRay wrapper) because it also packs a 7-float input buffer on every
/// call. The extra cost is the PackRay overhead, not the Call() dispatch itself.
///
/// Note: the C++→C# return path always allocates a new managed array; there is no
/// ref-return API in GDExtension, so Mode F still allocates once per tick.
///
/// Rays are fired straight down from the node's position, spread across a horizontal grid
/// so they are distinct queries and the broadphase cannot trivially deduplicate them.
/// All modes fire identical ray geometry so results are directly comparable.
///
/// 12 000 rays/s at 60 Hz physics = 200 rays per _PhysicsProcess call.
/// </summary>
public partial class RaycastGCBenchmark : Node3D
{
    // ---------------------------------------------------------------------------
    // Tunables
    // ---------------------------------------------------------------------------

    [Export] public float RayLength    = 100f;
    [Export] public float GridSpacing  = 0.5f;   // metres between ray origins in the XZ grid
    [Export] public int   RaysPerTick  = 200;
    [Export] public float RunSeconds   = 10f;
    [Export] public float PauseSeconds = 2f;

    // ---------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------

    private enum Mode { A_NativeNaive, B_NativeOptimised, C_Batch1, D_Batch5, E_Batch10, F_Batch20, G_Batch200 }
    private enum Phase { Running, Pausing, Done }

    private Mode  _mode  = Mode.A_NativeNaive;
    private Phase _phase = Phase.Running;

    private float _phaseTimer;
    private int   _ticksThisRun;
    private int   _hitsThisRun;
    private int   _gen0Before, _gen1Before, _gen2Before;
    private long  _allocBefore;

    // Pre-allocated input buffers for each batch size, allocated once in _Ready.
    // The native side requires in_buffer.size() == rayCount * 7 exactly.
    private float[] _inBuffer1;    //   1 * 7
    private float[] _inBuffer5;    //   5 * 7
    private float[] _inBuffer10;   //  10 * 7
    private float[] _inBuffer20;   //  20 * 7
    private float[] _inBuffer200;  // 200 * 7

    // Cached query params for Mode B — allocated once in _Ready, mutated per ray.
    private PhysicsRayQueryParameters3D _queryParams;

    // Pre-computed ray origins spread on a flat XZ grid, all firing straight down.
    private Vector3[] _rayOrigins;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    public override void _Ready()
    {
        _inBuffer1   = new float[  1 * 7];
        _inBuffer5   = new float[  5 * 7];
        _inBuffer10  = new float[ 10 * 7];
        _inBuffer20  = new float[ 20 * 7];
        _inBuffer200 = new float[200 * 7];
        _rayOrigins  = BuildGrid(RaysPerTick, GridSpacing);
        _queryParams = new PhysicsRayQueryParameters3D
        {
            CollisionMask = 0xFFFFFFFF,
            HitBackFaces  = true,
        };

        BeginRun();
    }

    public override void _PhysicsProcess(double delta)
    {
        _phaseTimer += (float)delta;

        switch (_phase)
        {
            case Phase.Running:
                RunTick();
                if (_phaseTimer >= RunSeconds)
                    BeginPause();
                break;

            case Phase.Pausing:
                if (_phaseTimer >= PauseSeconds)
                    FinishPause();
                break;

            case Phase.Done:
                break;
        }
    }

    // ---------------------------------------------------------------------------
    // Run / pause transitions
    // ---------------------------------------------------------------------------

    private void BeginRun()
    {
        _phaseTimer   = 0f;
        _ticksThisRun = 0;
        _hitsThisRun  = 0;

        _gen0Before  = GC.CollectionCount(0);
        _gen1Before  = GC.CollectionCount(1);
        _gen2Before  = GC.CollectionCount(2);
        _allocBefore = GC.GetTotalAllocatedBytes(precise: false);

        _phase = Phase.Running;
    }

    private void BeginPause()
    {
        _phase      = Phase.Pausing;
        _phaseTimer = 0f;

        int  gen0Delta  = GC.CollectionCount(0) - _gen0Before;
        int  gen1Delta  = GC.CollectionCount(1) - _gen1Before;
        int  gen2Delta  = GC.CollectionCount(2) - _gen2Before;
        long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - _allocBefore;

        GD.Print("──────────────────────────────────────────────────");
        GD.Print($"[GcBenchmark] Mode {_mode} complete");
        GD.Print($"  Ticks       : {_ticksThisRun}  ({_ticksThisRun * RaysPerTick} rays cast)");
        GD.Print($"  Hits        : {_hitsThisRun}");
        GD.Print($"  GC gen0/1/2 : {gen0Delta}/{gen1Delta}/{gen2Delta}");
        GD.Print($"  Bytes alloc : {allocDelta:N0}");
        GD.Print("  (pausing — collecting GC before next run)");
    }

    private void FinishPause()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        if (_mode == Mode.G_Batch200)
        {
            _phase = Phase.Done;
            GD.Print("──────────────────────────────────────────────────");
            GD.Print("[GcBenchmark] All modes complete.");
            return;
        }

        _mode = _mode switch
        {
            Mode.A_NativeNaive     => Mode.B_NativeOptimised,
            Mode.B_NativeOptimised => Mode.C_Batch1,
            Mode.C_Batch1          => Mode.D_Batch5,
            Mode.D_Batch5          => Mode.E_Batch10,
            Mode.E_Batch10         => Mode.F_Batch20,
            Mode.F_Batch20         => Mode.G_Batch200,
            _                      => Mode.G_Batch200
        };

        GD.Print($"[GcBenchmark] Starting Mode {_mode}");
        BeginRun();
    }

    // ---------------------------------------------------------------------------
    // Per-tick dispatch
    // ---------------------------------------------------------------------------

    private void RunTick()
    {
        _ticksThisRun++;
        var space = GetWorld3D().DirectSpaceState;

        _hitsThisRun += _mode switch
        {
            Mode.A_NativeNaive     => TickNativeNaive(space),
            Mode.B_NativeOptimised => TickNativeOptimised(space),
            Mode.C_Batch1          => TickBatch(space, 1,   _inBuffer1),
            Mode.D_Batch5          => TickBatch(space, 5,   _inBuffer5),
            Mode.E_Batch10         => TickBatch(space, 10,  _inBuffer10),
            Mode.F_Batch20         => TickBatch(space, 20,  _inBuffer20),
            Mode.G_Batch200        => TickBatch(space, 200, _inBuffer200),
            _                      => 0
        };
    }

    // ---------------------------------------------------------------------------
    // Mode implementations
    // ---------------------------------------------------------------------------

    /// Mode A: naive native — new PhysicsRayQueryParameters3D allocated on every tick.
    /// Represents typical unoptimised Godot C# raycast code.
    private int TickNativeNaive(PhysicsDirectSpaceState3D space)
    {
        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            Vector3 from = _rayOrigins[i] + GlobalPosition;
            var queryParams = new PhysicsRayQueryParameters3D
            {
                From          = from,
                To            = from + Vector3.Down * RayLength,
                CollisionMask = 0xFFFFFFFF,
                HitBackFaces  = true,
            };
            var result = space.IntersectRay(queryParams);
            if (result.Count > 0)
                hits++;
        }
        return hits;
    }

    /// Mode B: optimised native — single PhysicsRayQueryParameters3D cached in _Ready,
    /// From/To mutated per ray. Best achievable without leaving the native API.
    private int TickNativeOptimised(PhysicsDirectSpaceState3D space)
    {
        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            _queryParams.From = _rayOrigins[i] + GlobalPosition;
            _queryParams.To   = _queryParams.From + Vector3.Down * RayLength;

            var result = space.IntersectRay(_queryParams);
            if (result.Count > 0)
                hits++;
        }
        return hits;
    }

    /// Modes B–F: bridge batch dispatch with a given batch size, using a pre-allocated input buffer.
    /// RaysPerTick must be divisible by batchSize.
    private int TickBatch(PhysicsDirectSpaceState3D space, int batchSize, float[] inBuffer)
    {
        int hits       = 0;
        int batchCount = RaysPerTick / batchSize;

        for (int b = 0; b < batchCount; b++)
        {
            int baseIdx = b * batchSize;
            for (int i = 0; i < batchSize; i++)
            {
                Vector3 origin = _rayOrigins[baseIdx + i] + GlobalPosition;
                RaycastBridge.PackRay(inBuffer, i, origin, Vector3.Down, RayLength);
            }

            var results = RaycastBridge.IntersectRaysBatch(inBuffer, space, batchSize, collisionMask: 0xFFFFFFFF);
            for (int i = 0; i < batchSize; i++)
            {
                if (RaycastBridge.GetHit(results, i))
                    hits++;
            }
        }
        return hits;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// Builds a flat grid of count distinct XZ offsets centred on the origin.
    private static Vector3[] BuildGrid(int count, float spacing)
    {
        var origins = new Vector3[count];
        int side    = (int)Math.Ceiling(Math.Sqrt(count));

        float halfW = (side - 1) * spacing * 0.5f;
        float halfH = ((count + side - 1) / side - 1) * spacing * 0.5f;

        for (int i = 0; i < count; i++)
        {
            int row = i / side;
            int col = i % side;
            origins[i] = new Vector3(col * spacing - halfW, 0f, row * spacing - halfH);
        }
        return origins;
    }
}
