# ECS workload benchmarks

Six deterministic workloads compare three implementations:

- `DirectArch`: Arch 2.1.0 public APIs, with writable component references.
- `ArchyArch`: Archy's public API backed by `ArchStorage`.
- `ArchyNaive`: the same Archy workload code backed by `NaiveStorage`.

The project uses [BenchmarkDotNet 0.15.8](https://www.nuget.org/packages/BenchmarkDotNet/0.15.8)
for timing, allocation diagnostics and exported reports. Each scenario runs with
1,024 and 16,384 entities, producing 36 benchmark cases. Direct Arch is the
baseline within each scenario/population group.

## Scenarios

Every actor has a logical ID, position, velocity and health. Positions and
velocities use integers so verification can compare exact results across storage
layouts and traversal orders.

| Scenario | Application workload | One measured invocation |
| --- | --- | --- |
| `WorldLoad` | Load and retire a populated scene. | Create a fresh world/store, spawn the population, read all base components into a checksum, then dispose it. Static type metadata is warmed by the harness. |
| `Movement` | Integrate a stable population of moving actors. | One typed query updates every position from its velocity. |
| `ActorActivation` | Put distant actors to sleep and wake them again. | Sixteen ticks: mark one eighth dormant per tick for eight ticks, then wake one eighth per tick for eight ticks. After each transition, move only actors without the dormant tag. The invocation ends with all actors active. |
| `ProjectileChurn` | Advance projectiles, expire them and spawn replacements. | Integrate all positions and decrement lifetimes; after traversal, delete expired projectiles and replace them in stable logical-slot order. Lifetimes are staggered across eight ticks, and replacement projectiles live eight ticks. Live population remains constant. |
| `SpatialLookup` | Move actors between spatial buckets and find nearby actors. | Move one eighth of the population, update its cell keys, then perform 128 exact-bucket lookups over 64 cells. Each result contributes its position and logical ID to a cell-weighted checksum. |
| `DamageNotifications` | Apply damage and deliver changes to an observer. | Update one eighth of actors and deliver deferred notifications carrying origin, old/new health and sequence. Health resets to 100 when the next hit would reduce it to zero. |

All invocations return a checksum consumed by the harness. Persistent worlds keep
their population bounded across repeated calls. Activation measures a complete
cycle to avoid comparing different proportions of sleeping actors. Spatial
lookups visit every bucket twice, keeping the result count fixed while cell
membership changes. Damage updates rotate through eight cohorts.

## What is included

The two Archy implementations share [`ArchySimulation.cs`](ArchySimulation.cs),
so only adapter selection changes. [`ArchSimulation.cs`](ArchSimulation.cs) uses
native typed creation and ref queries. Archy's explicit component writes and its
identity/ownership guarantees remain part of the measured cost.

Arch does not supply Archy's generated value indexes or deferred lifecycle
notifications. The direct implementation therefore maintains its own cell-to-entity
sets and a reusable typed health-event queue. Their maintenance and consumption
are timed. Comparing an Arch scan with an Archy index, or dropping notifications
from the native workload, would measure different application work.

[Targeted global setup and cleanup](https://benchmarkdotnet.org/articles/features/setup-and-cleanup.html)
create only the selected implementation. Fixture creation, query preparation,
index seeding, subscriptions and final disposal are outside timing for persistent
scenarios. `WorldLoad` deliberately measures world/store creation and disposal.
Snapshot capture, sorting, reference-model execution and console output are never
inside benchmark methods.

These are application-workload comparisons, not claims that direct Arch provides
all of Archy's mutation, copy, reference and event guarantees. No adapter is tuned
or changed by this project.

## Run

From the repository root:

```sh
dotnet build Archy.slnx -c Release
dotnet run --no-build --project benchmarks/Archy.Benchmarks -c Release -- --verify
dotnet run --no-build --project benchmarks/Archy.Benchmarks -c Release -- --smoke
dotnet run --no-build --project benchmarks/Archy.Benchmarks -c Release
```

`--verify` compares all three implementations with the plain-array reference
model, including complete actor state, live counts, spawn/retirement totals,
notification totals, notification checksums and each invocation's result.
Populations of 0, 1, 257, 1,024 and 16,384 cover empty, small and uneven chunks.
Small populations run 129 invocations to cover projectile reuse, negative spatial
coordinates and health-reset cycles; the largest runs 17 invocations. Verification
does not run the timing harness.

`--smoke` runs the 18 combinations at 1,024 entities using BenchmarkDotNet's dry
job. It checks process launch and report generation; use the normal job for
performance measurements. Standard BenchmarkDotNet arguments are forwarded, for
example `--filter '*Movement*' --job short`. A full run can take several minutes.

Reports appear in `BenchmarkDotNet.Artifacts/results/` under the working directory.
The harness includes Markdown, CSV, timing/error statistics and managed allocation
diagnostics. Generated artifacts are ignored by Git. Record the runtime, hardware,
power mode and job settings when comparing results from different runs.
