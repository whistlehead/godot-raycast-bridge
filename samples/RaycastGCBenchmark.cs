using System;
using System.Diagnostics;
using Godot;
using PhysicsQueryBridge;

/// <summary>
/// GC pressure benchmark — attach to a Node3D in a scene that has collidable geometry below it.
///
/// Each dispatch mode runs four times in sequence: no reads (_N), position only (_P),
/// position + normal (_PN), and all fields (_A). This produces four result tables covering
/// the full range of realistic field-read counts. Each sub-run is separated by a GC pause
/// identical to the pause between dispatch modes.
///
/// Modes
///   Z    No-op baseline              → no raycasts — establishes the GC floor for this scene
///   A_N/P/PN/A  Native naive         new params per ray; reads with plain string keys
///   B_N/P/PN/A  Native optimised     cached params; reads with cached StringName keys
///   C_N/P/PN/A  Bridge batch of 1    → float[] per ray            — 200 float[] allocations/tick
///   H_N/P/PN/A  Bridge batch of 2    → float[] per batch          — 100 float[] allocations/tick
///   D_N/P/PN/A  Bridge batch of 5    → float[] per batch          —  40 float[] allocations/tick
///   E_N/P/PN/A  Bridge batch of 10   → float[] per batch          —  20 float[] allocations/tick
///   F_N/P/PN/A  Bridge batch of 20   → float[] per batch          —  10 float[] allocations/tick
///   G_N/P/PN/A  Bridge batch of 200  → float[] per tick           —   1 float[] allocation/tick
///
/// Modes A and B bracket the native baseline: A shows what typical unoptimised code
/// produces (new params + plain string key reads); B shows the best achievable without
/// leaving the native API (cached params + cached StringName reads). The string key
/// cost is not isolated separately as it is expected to be below measurement noise.
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
    [Export] public int   TicksPerRun  = 600;     // fixed tick count per mode — deterministic sample size
    [Export] public int   PauseTicks   = 120;     // fixed tick count for GC pause between modes

    // ---------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------

    private enum ReadMode { None, Position, PositionNormal, AllFields }

    // Ordering: all _N modes, then all _P modes, then all _PN modes, then all _A modes —
    // so each read-mode group appears as a contiguous block in the output and maps to one table.
    private enum Mode
    {
        Z_Noop,
        A_NativeNaive_N,  B_NativeOpt_N,  C_Batch1_N,  H_Batch2_N,  D_Batch5_N,  E_Batch10_N,  F_Batch20_N,  G_Batch200_N,
        A_NativeNaive_P,  B_NativeOpt_P,  C_Batch1_P,  H_Batch2_P,  D_Batch5_P,  E_Batch10_P,  F_Batch20_P,  G_Batch200_P,
        A_NativeNaive_PN, B_NativeOpt_PN, C_Batch1_PN, H_Batch2_PN, D_Batch5_PN, E_Batch10_PN, F_Batch20_PN, G_Batch200_PN,
        A_NativeNaive_A,  B_NativeOpt_A,  C_Batch1_A,  H_Batch2_A,  D_Batch5_A,  E_Batch10_A,  F_Batch20_A,  G_Batch200_A,
    }
    private enum Phase { WarmupPause, Running, Pausing, Done }

    private Mode  _mode  = Mode.Z_Noop;
    private Phase _phase = Phase.WarmupPause;

    private int _ticksThisRun;
    private int _pauseTicks;
    private int   _hitsThisRun;
    private int   _gen0Before, _gen1Before, _gen2Before;
    private long  _allocBefore;

    private readonly Stopwatch _tickTimer   = new();
    private          double[]  _tickTimesMs;   // allocated once in _Ready to TicksPerRun; no resizing

    // Pre-allocated input buffers for each batch size, allocated once in _Ready.
    // The native side requires in_buffer.size() == rayCount * 7 exactly.
    private float[] _inBuffer1;    //   1 * 7
    private float[] _inBuffer2;    //   2 * 7
    private float[] _inBuffer5;    //   5 * 7
    private float[] _inBuffer10;   //  10 * 7
    private float[] _inBuffer20;   //  20 * 7
    private float[] _inBuffer200;  // 200 * 7

    // Cached query params for Mode B — allocated once in _Ready, mutated per ray.
    private PhysicsRayQueryParameters3D _queryParams;

    // Pre-computed ray origins spread on a flat XZ grid, all firing straight down.
    private Vector3[] _rayOrigins;

    // Sinks for read results — prevents the compiler eliminating reads as dead code.
    private Vector3 _sinkVec;
    private ulong   _sinkId;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    public override void _Ready()
    {
        _inBuffer1   = new float[  1 * 7];
        _inBuffer2   = new float[  2 * 7];
        _inBuffer5   = new float[  5 * 7];
        _inBuffer10  = new float[ 10 * 7];
        _inBuffer20  = new float[ 20 * 7];
        _inBuffer200 = new float[200 * 7];
        _rayOrigins  = BuildGrid(RaysPerTick, GridSpacing);
        _tickTimesMs = new double[TicksPerRun];
        _queryParams = new PhysicsRayQueryParameters3D
        {
            CollisionMask = 0xFFFFFFFF,
            HitBackFaces  = true,
        };

        // Begin warmup pause — allows JIT compilation and startup GC noise to settle
        // before the first measurement run begins.
        _pauseTicks = 0;
        GD.Print($"[GcBenchmark] Warming up for {PauseTicks} ticks before first run...");
    }

    public override void _PhysicsProcess(double _)
    {
        switch (_phase)
        {
            case Phase.WarmupPause:
                if (++_pauseTicks >= PauseTicks)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GD.Print("[GcBenchmark] Warmup complete. Starting Mode Z_Noop");
                    BeginRun();
                }
                break;

            case Phase.Running:
                RunTick();
                if (_ticksThisRun >= TicksPerRun)
                    BeginPause();
                break;

            case Phase.Pausing:
                if (++_pauseTicks >= PauseTicks)
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
        _pauseTicks = 0;

        int  gen0Delta  = GC.CollectionCount(0) - _gen0Before;
        int  gen1Delta  = GC.CollectionCount(1) - _gen1Before;
        int  gen2Delta  = GC.CollectionCount(2) - _gen2Before;
        long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - _allocBefore;

        string readLabel = _mode switch
        {
            var m when m.ToString().EndsWith("_N")  => "no reads",
            var m when m.ToString().EndsWith("_P")  => "read position",
            var m when m.ToString().EndsWith("_PN") => "read position + normal",
            var m when m.ToString().EndsWith("_A")  => "read all fields",
            _                                       => "n/a"
        };

        GD.Print("──────────────────────────────────────────────────");
        GD.Print($"[GcBenchmark] Mode {_mode} ({readLabel}) complete");
        int raysCast = _mode == Mode.Z_Noop ? 0 : _ticksThisRun * RaysPerTick;
        GD.Print($"  Ticks       : {_ticksThisRun}  ({raysCast} rays cast)");
        GD.Print($"  Hits        : {_hitsThisRun}");
        GD.Print($"  GC gen0/1/2 : {gen0Delta}/{gen1Delta}/{gen2Delta}");
        GD.Print($"  Bytes alloc : {allocDelta:N0}");
        TickStats(_tickTimesMs, _ticksThisRun, out double mean, out double median, out double stddev, out double p99);
        GD.Print($"  Tick mean   : {mean:F3} ms");
        GD.Print($"  Tick median : {median:F3} ms");
        GD.Print($"  Tick stddev : {stddev:F3} ms");
        GD.Print($"  Tick p99    : {p99:F3} ms");
        GD.Print("  (pausing — collecting GC before next run)");
    }

    private void FinishPause()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        if (_mode == Mode.G_Batch200_A)
        {
            _phase = Phase.Done;
            GD.Print("──────────────────────────────────────────────────");
            GD.Print("[GcBenchmark] All modes complete.");
            return;
        }

        _mode = _mode switch
        {
            Mode.Z_Noop           => Mode.A_NativeNaive_N,
            // No-reads block
            Mode.A_NativeNaive_N  => Mode.B_NativeOpt_N,
            Mode.B_NativeOpt_N    => Mode.C_Batch1_N,
            Mode.C_Batch1_N       => Mode.H_Batch2_N,
            Mode.H_Batch2_N       => Mode.D_Batch5_N,
            Mode.D_Batch5_N       => Mode.E_Batch10_N,
            Mode.E_Batch10_N      => Mode.F_Batch20_N,
            Mode.F_Batch20_N      => Mode.G_Batch200_N,
            // Position-only block
            Mode.G_Batch200_N     => Mode.A_NativeNaive_P,
            Mode.A_NativeNaive_P  => Mode.B_NativeOpt_P,
            Mode.B_NativeOpt_P    => Mode.C_Batch1_P,
            Mode.C_Batch1_P       => Mode.H_Batch2_P,
            Mode.H_Batch2_P       => Mode.D_Batch5_P,
            Mode.D_Batch5_P       => Mode.E_Batch10_P,
            Mode.E_Batch10_P      => Mode.F_Batch20_P,
            Mode.F_Batch20_P      => Mode.G_Batch200_P,
            // Position + normal block
            Mode.G_Batch200_P     => Mode.A_NativeNaive_PN,
            Mode.A_NativeNaive_PN => Mode.B_NativeOpt_PN,
            Mode.B_NativeOpt_PN   => Mode.C_Batch1_PN,
            Mode.C_Batch1_PN      => Mode.H_Batch2_PN,
            Mode.H_Batch2_PN      => Mode.D_Batch5_PN,
            Mode.D_Batch5_PN      => Mode.E_Batch10_PN,
            Mode.E_Batch10_PN     => Mode.F_Batch20_PN,
            Mode.F_Batch20_PN     => Mode.G_Batch200_PN,
            // All-fields block
            Mode.G_Batch200_PN    => Mode.A_NativeNaive_A,
            Mode.A_NativeNaive_A  => Mode.B_NativeOpt_A,
            Mode.B_NativeOpt_A    => Mode.C_Batch1_A,
            Mode.C_Batch1_A       => Mode.H_Batch2_A,
            Mode.H_Batch2_A       => Mode.D_Batch5_A,
            Mode.D_Batch5_A       => Mode.E_Batch10_A,
            Mode.E_Batch10_A      => Mode.F_Batch20_A,
            Mode.F_Batch20_A      => Mode.G_Batch200_A,
            _                     => Mode.G_Batch200_A
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

        _tickTimer.Restart();
        _hitsThisRun += _mode switch
        {
            Mode.Z_Noop           => 0,

            Mode.A_NativeNaive_N  => TickNativeNaive(space, ReadMode.None),
            Mode.A_NativeNaive_P  => TickNativeNaive(space, ReadMode.Position),
            Mode.A_NativeNaive_PN => TickNativeNaive(space, ReadMode.PositionNormal),
            Mode.A_NativeNaive_A  => TickNativeNaive(space, ReadMode.AllFields),

            Mode.B_NativeOpt_N    => TickNativeOptimised(space, ReadMode.None),
            Mode.B_NativeOpt_P    => TickNativeOptimised(space, ReadMode.Position),
            Mode.B_NativeOpt_PN   => TickNativeOptimised(space, ReadMode.PositionNormal),
            Mode.B_NativeOpt_A    => TickNativeOptimised(space, ReadMode.AllFields),

            Mode.C_Batch1_N       => TickBatch(space, 1,   _inBuffer1,   ReadMode.None),
            Mode.C_Batch1_P       => TickBatch(space, 1,   _inBuffer1,   ReadMode.Position),
            Mode.C_Batch1_PN      => TickBatch(space, 1,   _inBuffer1,   ReadMode.PositionNormal),
            Mode.C_Batch1_A       => TickBatch(space, 1,   _inBuffer1,   ReadMode.AllFields),

            Mode.H_Batch2_N       => TickBatch(space, 2,   _inBuffer2,   ReadMode.None),
            Mode.H_Batch2_P       => TickBatch(space, 2,   _inBuffer2,   ReadMode.Position),
            Mode.H_Batch2_PN      => TickBatch(space, 2,   _inBuffer2,   ReadMode.PositionNormal),
            Mode.H_Batch2_A       => TickBatch(space, 2,   _inBuffer2,   ReadMode.AllFields),

            Mode.D_Batch5_N       => TickBatch(space, 5,   _inBuffer5,   ReadMode.None),
            Mode.D_Batch5_P       => TickBatch(space, 5,   _inBuffer5,   ReadMode.Position),
            Mode.D_Batch5_PN      => TickBatch(space, 5,   _inBuffer5,   ReadMode.PositionNormal),
            Mode.D_Batch5_A       => TickBatch(space, 5,   _inBuffer5,   ReadMode.AllFields),

            Mode.E_Batch10_N      => TickBatch(space, 10,  _inBuffer10,  ReadMode.None),
            Mode.E_Batch10_P      => TickBatch(space, 10,  _inBuffer10,  ReadMode.Position),
            Mode.E_Batch10_PN     => TickBatch(space, 10,  _inBuffer10,  ReadMode.PositionNormal),
            Mode.E_Batch10_A      => TickBatch(space, 10,  _inBuffer10,  ReadMode.AllFields),

            Mode.F_Batch20_N      => TickBatch(space, 20,  _inBuffer20,  ReadMode.None),
            Mode.F_Batch20_P      => TickBatch(space, 20,  _inBuffer20,  ReadMode.Position),
            Mode.F_Batch20_PN     => TickBatch(space, 20,  _inBuffer20,  ReadMode.PositionNormal),
            Mode.F_Batch20_A      => TickBatch(space, 20,  _inBuffer20,  ReadMode.AllFields),

            Mode.G_Batch200_N     => TickBatch(space, 200, _inBuffer200, ReadMode.None),
            Mode.G_Batch200_P     => TickBatch(space, 200, _inBuffer200, ReadMode.Position),
            Mode.G_Batch200_PN    => TickBatch(space, 200, _inBuffer200, ReadMode.PositionNormal),
            Mode.G_Batch200_A     => TickBatch(space, 200, _inBuffer200, ReadMode.AllFields),

            _                     => 0
        };
        _tickTimer.Stop();
        _tickTimesMs[_ticksThisRun - 1] = _tickTimer.Elapsed.TotalMilliseconds;
    }

    // ---------------------------------------------------------------------------
    // Mode implementations
    // ---------------------------------------------------------------------------

    /// Mode Z: no-op — no raycasts, no allocations. Establishes the GC floor for this
    /// scene setup so other modes can be read relative to it.
    /// (Dispatch is handled inline in RunTick via the switch expression returning 0.)

    // Cached StringNames for optimised native read (Mode B) — avoids string allocations on lookup.
    private static readonly StringName _keyPosition = "position";
    private static readonly StringName _keyNormal   = "normal";
    private static readonly StringName _keyColliderId = "collider_id";

    /// Mode A: naive native — new params allocated per ray, plain string keys on read.
    private int TickNativeNaive(PhysicsDirectSpaceState3D space, ReadMode read)
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
            {
                hits++;
                if (read >= ReadMode.Position)
                    _sinkVec = result["position"].AsVector3();
                if (read >= ReadMode.PositionNormal)
                    _sinkVec = result["normal"].AsVector3();
                if (read == ReadMode.AllFields)
                    _sinkId  = result["collider_id"].AsUInt64();
            }
        }
        return hits;
    }

    /// Mode B: optimised native — cached params, cached StringName keys on read.
    private int TickNativeOptimised(PhysicsDirectSpaceState3D space, ReadMode read)
    {
        int hits = 0;
        for (int i = 0; i < RaysPerTick; i++)
        {
            _queryParams.From = _rayOrigins[i] + GlobalPosition;
            _queryParams.To   = _queryParams.From + Vector3.Down * RayLength;

            var result = space.IntersectRay(_queryParams);
            if (result.Count > 0)
            {
                hits++;
                if (read >= ReadMode.Position)
                    _sinkVec = result[_keyPosition].AsVector3();
                if (read >= ReadMode.PositionNormal)
                    _sinkVec = result[_keyNormal].AsVector3();
                if (read == ReadMode.AllFields)
                    _sinkId  = result[_keyColliderId].AsUInt64();
            }
        }
        return hits;
    }

    /// Bridge batch dispatch with a given batch size, using a pre-allocated input buffer.
    /// RaysPerTick must be divisible by batchSize.
    private int TickBatch(PhysicsDirectSpaceState3D space, int batchSize, float[] inBuffer, ReadMode read)
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
                {
                    hits++;
                    if (read >= ReadMode.Position)
                        _sinkVec = RaycastBridge.GetPosition(results, i);
                    if (read >= ReadMode.PositionNormal)
                        _sinkVec = RaycastBridge.GetNormal(results, i);
                    if (read == ReadMode.AllFields)
                        _sinkId  = RaycastBridge.GetColliderId(results, i);
                }
            }
        }
        return hits;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static void TickStats(double[] times, int n, out double mean, out double median, out double stddev, out double p99)
    {
        if (n == 0) { mean = median = stddev = p99 = 0; return; }

        double sum = 0;
        for (int i = 0; i < n; i++) sum += times[i];
        mean = sum / n;

        double variance = 0;
        for (int i = 0; i < n; i++) { double d = times[i] - mean; variance += d * d; }
        stddev = Math.Sqrt(variance / n);

        // Copy only the live slice so we can sort without disturbing the original buffer.
        // This allocation happens after GC.Collect() during the pause, not during a run.
        var sorted = new double[n];
        Array.Copy(times, sorted, n);
        Array.Sort(sorted);
        median = n % 2 == 0
            ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
            : sorted[n / 2];
        p99 = sorted[(int)Math.Ceiling(n * 0.99) - 1];
    }

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
