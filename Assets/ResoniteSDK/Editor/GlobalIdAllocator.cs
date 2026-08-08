// 2026-08-08 (discovered on the live client / per Tanossy's feedback: "pull this out"):
// UniqueSessionId is a value assigned by the server side during ResoniteLink's handshake, and we
// have no full control over it (it was confirmed on the live client that, depending on how you
// reconnect, the same value can be returned again). The official SDK's (SceneConverter.cs)
// original ID-allocation counter was reset per SceneConverter instance, so if a new SceneConverter
// was created while UniqueSessionId stayed the same, ID generation would reproduce the exact same
// sequence of strings as last time (e.g. "Unity_0__0"), colliding with the previous run's
// same-named object still present on the server side, causing a "ID '...' is already in use"
// FATAL ERROR and dropping the converter into an IsCorrupted state - a real production bug.
//
// Switching to this process-wide monotonically increasing static counter guarantees that, no
// matter how many times SceneConverter gets recreated, the same number will never be generated
// twice for the lifetime of this Unity Editor process. The change on the official SDK side is
// just a single line swapping the ID-generation source inside SceneConverter.AllocateId() over to
// this class.
public static class GlobalIdAllocator
{
    static long _pool;

    public static long Next() => System.Threading.Interlocked.Increment(ref _pool) - 1;
}
