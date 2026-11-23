global using Xunit;

// xunit v3 4.x replacement for CollectionBehavior(DisableTestParallelization = true): one shared
// Service Bus emulator, so no two tests in this assembly may run at the same time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
