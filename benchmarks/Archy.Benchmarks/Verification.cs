using Archy.Adapters.Arch;
using Archy.Adapters.Naive;

namespace Archy.Benchmarks;

internal static class Verification {
  public static void Run() {
    var comparisons = 0;

    foreach (var scenario in Enum.GetValues<Scenario>()) {
      foreach (var count in new[] { 0, 1, 257, 1_024, 16_384 }) {
        using var reference = new ReferenceSimulation((scenario, count));
        using var direct = new ArchSimulation((scenario, count));
        using var archy = new ArchySimulation((scenario, count), static () => new ArchStorage());
        using var naive = new ArchySimulation((scenario, count), static () => new NaiveStorage());
        (string Name, ISimulation Simulation)[] engines = [("Arch", direct), ("Archy.Arch", archy), ("Archy.Naive", naive)];

        foreach (var (name, engine) in engines) {
          Compare(reference.Capture(), engine.Capture(), $"{scenario}/{count}/{name}/initial");
          comparisons++;
        }

        var steps = count >= 16_384 ? 17 : 129;

        for (var step = 0; step < steps; step++) {
          var expectedResult = reference.Step();
          var expectedState = reference.Capture();

          foreach (var (name, engine) in engines) {
            var result = engine.Step();
            var context = $"{scenario}/{count}/{name}/step {step + 1}";

            if (result != expectedResult) throw new InvalidOperationException($"{context}: checksum mismatch.");

            Compare(expectedState, engine.Capture(), context);
            comparisons++;
          }
        }
      }

      Console.WriteLine($"Verified {scenario}: Arch, Archy.Arch and Archy.Naive match the reference model.");
    }

    Console.WriteLine($"Benchmark verification passed: {comparisons} state comparisons.");
  }

  private static void Compare(Snapshot expected, Snapshot actual, string context) {
    if (!expected.Actors.SequenceEqual(actual.Actors))
      throw new InvalidOperationException($"{context}: actor state mismatch.");
    if (expected.Population != actual.Population || expected.Spawned != actual.Spawned || expected.Retired != actual.Retired
      || expected.Notifications != actual.Notifications || expected.NotificationChecksum != actual.NotificationChecksum
      || expected.LastResult != actual.LastResult)
      throw new InvalidOperationException($"{context}: population, lifecycle or notification mismatch.");
  }
}
