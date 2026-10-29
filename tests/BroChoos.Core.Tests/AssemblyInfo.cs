using Xunit;

// BootstrapTests and UrlRunnerTests redirect Console.Out/Console.Error, which is
// process-wide state. ConsoleCaptureCollection already serializes the classes that
// opt in, but that protection is easy to forget when adding a class later. Disabling
// assembly-level parallelization makes the safe behaviour the default, so a future
// console-touching class cannot race even if its author omits the collection
// attribute. This suite is small and fast, so the lost parallelism costs nothing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
