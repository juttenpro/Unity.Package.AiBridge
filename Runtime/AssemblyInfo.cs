using System.Runtime.CompilerServices;

// Expose `internal` test-seam APIs to the package's own Editor-mode test assembly so
// fixtures can drive private state (e.g., AudioStreamProcessor.ForceLastEndTimeForTest)
// without exposing it as a public production surface.
[assembly: InternalsVisibleTo("Tsc.AIBridge.Tests.Editor")]
