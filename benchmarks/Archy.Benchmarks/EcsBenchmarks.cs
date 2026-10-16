using Archy.Adapters.Arch;
using Archy.Adapters.Naive;
using BenchmarkDotNet.Attributes;

namespace Archy.Benchmarks;

[MemoryDiagnoser]
public class EcsBenchmarks : IDisposable {
  private ISimulation _simulation = null!;

  [ParamsAllValues]
  public Scenario Scenario { get; set; }

  [Params(1_024, 16_384)]
  public int EntityCount { get; set; }

  [GlobalSetup(Target = nameof(DirectArch))]
  public void SetupDirectArch() => this._simulation = new ArchSimulation((this.Scenario, this.EntityCount));

  [GlobalSetup(Target = nameof(ArchyArch))]
  public void SetupArchyArch() =>
    this._simulation = new ArchySimulation((this.Scenario, this.EntityCount), static () => new ArchStorage());

  [GlobalSetup(Target = nameof(ArchyNaive))]
  public void SetupArchyNaive() =>
    this._simulation = new ArchySimulation((this.Scenario, this.EntityCount), static () => new NaiveStorage());

  [GlobalCleanup]
  public void Dispose() {
    this.Dispose(true);
    GC.SuppressFinalize(this);
  }

  protected virtual void Dispose(bool disposing) {
    if (!disposing) return;

    this._simulation?.Dispose();
    this._simulation = null!;
  }

  [Benchmark(Baseline = true)]
  public long DirectArch() => this._simulation.Step();

  [Benchmark]
  public long ArchyArch() => this._simulation.Step();

  [Benchmark]
  public long ArchyNaive() => this._simulation.Step();
}
