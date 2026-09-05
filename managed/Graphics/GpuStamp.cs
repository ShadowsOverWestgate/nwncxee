namespace Sowx.Graphics;

/// <summary>#324: a GPU timestamp pair per frame, ring-buffered so the read never stalls. Unlike
/// <see cref="GpuQuery"/> (TIME_ELAPSED, which cannot nest) two stamps can bracket a span that already
/// holds elapsed queries. The span is WALL time on the GPU timeline between the two stamps: it includes
/// any idle gap where the GPU waited on the CPU and excludes everything outside the stamps, so it is a
/// footprint, not a busy figure. Query names live in whichever context issued them; the 4.6 context is
/// created once per session (a failure disables the bracket for good), so like <see cref="GpuQuery"/>
/// this has no context-loss edge.</summary>
public unsafe struct GpuStamp
{
    private const int Depth = 4;
    private fixed uint _q[Depth * 2];
    private fixed byte _pending[Depth];
    private int _at;
    private bool _open;
    private ulong _sum, _max;
    private int _n;

    /// <summary>Stamp t0 on this frame's slot. Harvests that slot's previous pair first, and declines the
    /// frame (false) if it is not ready yet rather than stall -- same rule as <see cref="GpuQuery"/>.</summary>
    public bool Begin()
    {
        if (Gl.QueryCounter == null || Gl.GenQueries == null || Gl.GetQueryObjectui64v == null || Gl.GetQueryObjectiv == null) return false;
        if (_q[0] == 0) { fixed (uint* q = _q) Gl.GenQueries(Depth * 2, q); if (_q[0] == 0) return false; }
        if (_pending[_at] != 0)
        {
            int ready; Gl.GetQueryObjectiv(_q[_at * 2 + 1], Gl.QUERY_RESULT_AVAILABLE, &ready);
            if (ready == 0) return false;
            ulong t0, t1;
            Gl.GetQueryObjectui64v(_q[_at * 2], Gl.QUERY_RESULT, &t0);
            Gl.GetQueryObjectui64v(_q[_at * 2 + 1], Gl.QUERY_RESULT, &t1);
            _pending[_at] = 0;
            // a slot whose End never ran carries a stale t1 (or none): the order test is what keeps it out
            if (t1 >= t0) { ulong d = t1 - t0; _sum += d; _n++; if (d > _max) _max = d; LastMs = d / 1e6; }
            else LastMs = -1;
        }
        Gl.QueryCounter(_q[_at * 2], Gl.TIMESTAMP);
        _open = true;
        return true;
    }

    /// <summary>Stamp t1 on the open slot. May be called more than once a frame; the last call wins.</summary>
    public void End() { if (_open) Gl.QueryCounter(_q[_at * 2 + 1], Gl.TIMESTAMP); }

    /// <summary>Frame boundary; no GL. A slot Begin never opened is left alone.</summary>
    public void Rotate()
    {
        if (!_open) return;
        _pending[_at] = 1; _at = (_at + 1) % Depth; _open = false;
    }

    /// <summary>The span harvested by the last <see cref="Begin"/>, which belongs to the frame <see cref="Depth"/>
    /// frames ago (one Rotate per frame); -1 when that Begin harvested nothing.</summary>
    public double LastMs;
    public const int Age = Depth;
    public double AvgMs => _n == 0 ? 0.0 : _sum / 1e6 / _n;
    public double MaxMs => _max / 1e6;
    public bool Measured => _n > 0;
    public void Reset() { _sum = 0; _max = 0; _n = 0; }
}
