using Xunit;

// The Host is single-operator (one shared session/sidecar/approval gate). Two tests driving it at
// once would collide, so never run these in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
