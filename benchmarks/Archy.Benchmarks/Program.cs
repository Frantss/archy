using Archy.Benchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

if (args.Contains("--verify", StringComparer.Ordinal)) {
  if (args.Length != 1) throw new ArgumentException("Use --verify on its own.");

  Verification.Run();

  return;
}

var smoke = args.Contains("--smoke", StringComparer.Ordinal);
var config = ManualConfig.Create(DefaultConfig.Instance);

if (smoke) {
  config.AddJob(Job.Dry.WithId("Smoke"));
  config.AddFilter(new SimpleFilter(item => (int)item.Parameters[nameof(EcsBenchmarks.EntityCount)] == 1_024));
}

var benchmarkArgs = args.Where(arg => arg != "--smoke").ToArray();
var summary = BenchmarkRunner.Run<EcsBenchmarks>(config, benchmarkArgs);

if (summary.Reports.Length == 0 || summary.Reports.Any(report => !report.Success)) Environment.ExitCode = 1;
