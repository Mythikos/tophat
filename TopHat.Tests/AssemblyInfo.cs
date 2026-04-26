using Xunit;

// Metrics are recorded on a process-wide Meter; parallel test execution causes cross-test
// contamination of MetricsCapture. Disable parallelism to keep captured recordings deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
