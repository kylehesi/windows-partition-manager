using Xunit;

// Some tests create a WPF Application, which is a process-wide singleton; run tests sequentially so
// they cannot interfere with each other across the AppDomain.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
