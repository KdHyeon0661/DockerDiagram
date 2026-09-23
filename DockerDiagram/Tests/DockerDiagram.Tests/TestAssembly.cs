// The WPF regression checks share process-wide UI state and must run serially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
