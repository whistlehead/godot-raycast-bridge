using System;
using System.Runtime.InteropServices;
using Godot;
using PhysicsQueryBridge;

/// <summary>
/// GC pressure benchmark — attach to a Node3D in a scene that has collidable geometry below it.
///
/// Cycles through four modes, each running for RUN_SECONDS then pausing for PAUSE_SECONDS
/// while GC.Collect() is called to reset heap state before the next run.
///
/// Modes
///   A  Native Godot IntersectRay  → Dictionary per ray     — 200 Dictionary allocations/tick
///   B  Bridge single-ray          → float[] per ray        — 200 float[] allocations/tick
///   C  Bridge batch of 20         → float[] per batch      — 10  float[] allocations/tick
///   D  Bridge single batch of 200 → float[] per tick       — 1   float[] allocation/tick
///
/// Note: the C++→C# return path always allocates a new managed array; there is no ref-return
/// API available, so Mode D still allocates once per tick — it cannot be made zero-alloc
/// without unsafe pinning outside the scope of this library.
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
    [Export] public int   BatchSize    = 20;      // rays per call in Mode C
    [Export] public float RunSeconds   = 10f;
    [Export] public float PauseSeconds = 2f;

    // ---------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------

    private enum Mode { A_NativeDict, B_BridgeSingle, C_BridgeBatch20, D_BridgeFull }
    private enum Phase { Running, Pausing, Done }

    private Mode  _mode  = Mode.A_NativeDict;
    private Phase _phase = Phase.Running;

    private float _phaseTimer;
    private int   _ticksThisRun;
    private int   _hitsThisRun;
    private int   _gen0Before, _gen1Before, _gen2Before;
    private long  _allocBefore;

    // Pre-allocated input buffers — sized exactly to their respective ray counts.
    // Native side requires in_buffer.size() == rayCount * 7 exactly, so Mode C
    // and Mode D need separate buffers. Both are allocated once in _Ready.
    private float[] _inBufferBatch;   // BatchSize * 7  — used by Mode C
    private float[] _inBufferFull;    // RaysPerTick * 7 — used by Mode D

    // Pre-computed ray origins spread on a flat XZ grid, all firing straight down.
    private Vector3[] _rayOrigins;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    public override void _Ready()
    {
        _inBufferBatch = new float[BatchSize    * 7];
        _inBufferFull  = new float[RaysPerTick  * 7];
        _rayOrigins    = BuildGrid(RaysPerTick, GridSpacing);

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

        // Snapshot GC state immediately after the run ends, before Collect().
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
        // Force a full blocking collection to reset heap state.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        if (_mode == Mode.D_BridgeFull)
        {
            _phase = Phase.Done;
            GD.Print("──────────────────────────────────────────────────");
            GD.Print("[GcBenchmark] All modes complete.");
            return;
        }

        _mode = _mode switch
        {
            Mode.A_NativeDict   => Mode.B_BridgeSingle,
            Mode.B_BridgeSingle => Mode.C_BridgeBatch20,
            Mode.C_BridgeBatch20 => Mode.D_BridgeFull,
            _ => Mode.D_BridgeFull
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
            Mode.A_NativeDict    => TickA(space),
            Mode.B_BridgeSingle  => TickB(space),
            Mode.C_BridgeBatch20 => TickC(space),
            Mode.D_BridgeFull    => TickD(space),
            _                    => 0
        };
    }

    // ---------------------------------------------------------------------------
    // Mode implementations
    // ---------------------------------------------------------------------------

    /// Mode A: native Godot IntersectRay returning Dictionary — one Dictionary per ray.
    private int TickA(PhysicsDirectSpaceState3D space)
    {
        var queryParams = new PhysicsRayQueryParameters3D
        {
            CollisionMask = 0xFFFFFFFF,
            HitBackFaces  = true,
        };

        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            queryParams.From = _rayOrigins[i] + GlobalPosition;
            queryParams.To   = queryParams.From + Vector3.Down * RayLength;

            var result = space.IntersectRay(queryParams);
            if (result.Count > 0)
                hits++;
        }
        return hits;
    }

    /// Mode B: bridge single-ray — one float[] per ray.
    private int TickB(PhysicsDirectSpaceState3D space)
    {
        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            Vector3 from = _rayOrigins[i] + GlobalPosition;
            Vector3 to   = from + Vector3.Down * RayLength;

            var result = RaycastBridge.IntersectRay(space, from, to, collisionMask: 0xFFFFFFFF);
            if (RaycastBridge.GetHit(result, 0))
                hits++;
        }
        return hits;
    }

    /// Mode C: bridge batches of BatchSize — one float[] per batch.
    private int TickC(PhysicsDirectSpaceState3D space)
    {
        int hits       = 0;
        int batchCount = RaysPerTick / BatchSize;   // assumes RaysPerTick divisible by BatchSize

        for (int b = 0; b < batchCount; b++)
        {
            int baseIdx = b * BatchSize;
            for (int i = 0; i < BatchSize; i++)
            {
                Vector3 origin = _rayOrigins[baseIdx + i] + GlobalPosition;
                RaycastBridge.PackRay(_inBufferBatch, i, origin, Vector3.Down, RayLength);
            }

            var results = RaycastBridge.IntersectRaysBatch(_inBufferBatch, space, BatchSize, collisionMask: 0xFFFFFFFF);
            for (int i = 0; i < BatchSize; i++)
            {
                if (RaycastBridge.GetHit(results, i))
                    hits++;
            }
        }
        return hits;
    }

    /// Mode D: single batch of all RaysPerTick rays — one float[] per tick.
    private int TickD(PhysicsDirectSpaceState3D space)
    {
        for (int i = 0; i < RaysPerTick; i++)
        {
            Vector3 origin = _rayOrigins[i] + GlobalPosition;
            RaycastBridge.PackRay(_inBufferFull, i, origin, Vector3.Down, RayLength);
        }

        var results = RaycastBridge.IntersectRaysBatch(_inBufferFull, space, RaysPerTick, collisionMask: 0xFFFFFFFF);

        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            if (RaycastBridge.GetHit(results, i))
                hits++;
        }
        return hits;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// Builds a flat grid of RaysPerTick distinct XZ offsets centred on the origin.
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
