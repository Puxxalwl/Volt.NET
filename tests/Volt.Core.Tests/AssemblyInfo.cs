using Xunit;

// Engine state (SSG cache, route registry, writer pool) is process-global:
// engine-touching test classes must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
