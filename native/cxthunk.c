// Wrappers for engine functions that throw C++ exceptions (DirectPInvoke "cxthunk", linked into cx_managed on both
// OSes). A managed hook may never call such a function itself: the unwind would cross a NativeAOT frame. So funchook
// points the engine here instead, the managed callbacks run and return on either side, and the original is called
// from this C frame, which the unwinder passes through (it has unwind tables and no handler).

/// kiwi::impl::SolverImpl::optimize(Row const&) throws InternalSolverError("The objective is unbounded.").
/// before: NuiSolver on Windows, where optimize inlines substitute. after: NuiLayoutErrors, which reads a failed
/// artificial objective. Either may be null.
typedef void (*cx_optimize_fn)(void *solver, const void *objective);
static cx_optimize_fn cx_optimize_before, cx_optimize_after, cx_optimize_orig;

static void cx_optimize(void *solver, const void *objective)
{
    if (cx_optimize_before) cx_optimize_before(solver, objective);
    cx_optimize_orig(solver, objective);
    if (cx_optimize_after) cx_optimize_after(solver, objective);
}

/// Stores the callbacks and returns the function to hook optimize with.
void *cx_optimize_thunk(cx_optimize_fn before, cx_optimize_fn after)
{
    cx_optimize_before = before;
    cx_optimize_after = after;
    return (void *)cx_optimize;
}

/// Stores the trampoline funchook returned; set before the hooks commit.
void cx_optimize_set_orig(cx_optimize_fn orig)
{
    cx_optimize_orig = orig;
}
