using System.Runtime.CompilerServices;

// Lets the EditMode test assembly exercise internal-only pieces (Json, DeliveryQueue.Snapshot)
// directly rather than only through the public API surface, the same "test the actual unit, not
// just its public wrapper" approach the internal helper types in this repo's other SDKs get
// tested with (e.g. sdks/go's own lowercase-named internal helpers, tested from within the same
// package rather than needing everything exported).
[assembly: InternalsVisibleTo("ForgeOpsTracker.Tests.Editor")]
