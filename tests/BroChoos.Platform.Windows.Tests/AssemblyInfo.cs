using Xunit;

// Symmetry with BroChoos.Core.Tests: PathSafetyTests exercises the LIVE volume table and
// the live filesystem (8.3 expansion, Path.GetPathRoot), which is process-wide ambient
// state, and the detector/launcher fakes describe whole machine states. Serializing the
// assembly makes the safe behaviour the default so a future class that touches ambient
// state cannot race even if its author forgets a collection attribute. This suite is
// small and fast, so the lost parallelism costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
