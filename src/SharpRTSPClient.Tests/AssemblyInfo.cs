using Microsoft.VisualStudio.TestTools.UnitTesting;

// Run test classes in parallel, as the suite did under xUnit. Tests that touch process wide state
// opt out individually with [DoNotParallelize]; everything else binds its own ephemeral port or
// works on its own objects.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
