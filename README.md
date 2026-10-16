# Archy

Archy is an experiment coded by AI agents, built on top of
[Arch](https://github.com/genaray/Arch) through its Arch adapter. It provides a
standalone C# ECS API with
explicit component mutations, generated indexes, relationships, deferred events
and commands, systems, persistence and prefabs.

The `Archy` API layer owns entity handles with generation checks, component value
ownership, indexes, events and the remaining public behavior. It has no dependency
on Arch. The `Archy.Adapters.Arch` adapter uses Arch 2.1.0 for entity storage,
archetypes, chunks and structural changes. Native worlds stay private inside the
adapter, which uses the NuGet package through its public APIs. See
[storage and integration](src/ecs/IMPLEMENTATION.md) for the storage contract. The `Archy.Adapters.Naive` adapter supplies a minimal
managed implementation with no third-party dependencies.

The runtime assembly and namespace are `Archy`; the generator assembly is
`Archy.Generator`, with its generators in `Archy.Generators`.

## Runtime projects

| Project | Responsibility | Dependencies |
| --- | --- | --- |
| [`Archy`](src/ecs/Archy.csproj) | Public ECS behavior and the `Archy.Storage` adapter contracts. | .NET only. |
| [`Archy.Adapters.Arch`](src/adapters/arch/Archy.Adapters.Arch.csproj) | Arch storage implementation. | `Archy` and Arch 2.1.0. |
| [`Archy.Adapters.Naive`](src/adapters/naive/Archy.Adapters.Naive.csproj) | Minimal managed storage implementation. | `Archy` only. |

Each adapter references the API layer. Applications choose an adapter when creating
a store; component libraries can depend on the API layer alone. See
[adding an adapter](docs/adapters.md) for the contract and verification workflow.

## Setup and verification

Install the .NET 10 SDK selected by `global.json`, then run from this directory:

```sh
dotnet build Archy.slnx
dotnet run --no-build --project tests/ecs
dotnet run --no-build --project tests/api
dotnet run --no-build --project tests/ecs-generator
dotnet run --no-build --project benchmarks/Archy.Benchmarks -- --verify
dotnet build Archy.slnx -c Release
dotnet run --no-build --project tests/ecs -c Release
dotnet run --no-build --project tests/api -c Release
dotnet run --no-build --project tests/ecs-generator -c Release
dotnet run --no-build --project benchmarks/Archy.Benchmarks -c Release -- --verify
```

The Arch adapter restores Arch 2.1.0; the generator restores Roslyn; the benchmark
project restores BenchmarkDotNet and Friflo.Engine.ECS. The API-only checks use the supported naive
adapter and run without the Arch package.

## Consumer setup

Reference the API layer and selected adapter from your application. Also reference
`tools/ecs-generator/Archy.Generator.csproj` as an analyzer in each project
that declares indexed or persistent components:

```xml
<ProjectReference Include="path/to/archy/src/ecs/Archy.csproj" />
<ProjectReference Include="path/to/archy/src/adapters/arch/Archy.Adapters.Arch.csproj" />
<ProjectReference Include="path/to/archy/tools/ecs-generator/Archy.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

## Naive adapter

To use the minimal managed storage, select this adapter project instead of the
Arch adapter reference above:

```xml
<ProjectReference Include="path/to/archy/src/adapters/naive/Archy.Adapters.Naive.csproj" />
```

```csharp
using Archy;
using Archy.Adapters.Naive;

using var store = new EntityStore(new NaiveStorage());
```

`NaiveStorage` uses dictionaries of typed arrays and fixed-size chunks. It favors
a small implementation over throughput: structural moves copy retained values,
deletions swap in the final row, and disposal clears retained arrays. All shared
ECS behavior stays in the API layer. Store restoration uses
`serializer.ReadStore(json, static () => new NaiveStorage())`.

## API overview

| Area | Public API |
| --- | --- |
| Entities | [`EntityStore`](src/ecs/EntityStore.cs): `Create`, `Delete`, `TryDelete`, `IsAlive`, `Count`; [`Entity`](src/ecs/Entity.cs): store, ID, generation and liveness. |
| Components | Structs implement `IComponent`. [`Entity` methods](src/ecs/Entity.Components.cs): `Get<T>`, `TryGet<T>`, `Has<T>`, `Add`, `Update`, `Upsert`, `Remove<T>` and `Try` mutation variants. |
| Tags | Structs implement `ITag`. `HasTag<T>`, `AddTag<T>`, `RemoveTag<T>` and `Try` mutation variants. |
| Queries | [`Query()` and `Query<T1, ...>()`](src/ecs/queries/ArchetypeQuery.cs), with up to four component types; `Entities`, `Chunks`, `ForEachEntity`, `Count`. [Filters](src/ecs/queries/QueryBase.cs) select component/tag compositions and indexed values through `HasValue` and `ValueInRange`. |
| Indexes | [`[Indexed]`](src/ecs/indexes/IndexedAttribute.cs) on a partial component's field, property or method. `ComponentIndex<T, TKey>()` provides key lookup, `Values` and unique-key `TryGet`. |
| Relationships | [`[Indexed, EntityReference]`](src/ecs/indexes/EntityReferenceAttribute.cs) on an entity-valued member. `TargetDeletion.Restrict` or `RemoveComponent` controls target deletion. |
| Events | [`store.Events.Subscribe<T>` and `entity.Events.Subscribe<T>`](src/ecs/events/StoreEvents.cs) return disposable subscriptions. `Dispatch()` delivers queued lifecycle notifications. [`Emit`](src/ecs/EntityStore.CustomEvents.cs) queues custom `ICustomEvent` payloads. |
| Commands | [`CreateCommandBuffer()`](src/ecs/commands/CommandBuffer.cs) records creation, component/tag writes, deletion and custom events; `Playback()` applies them in order. Buffered `Try` operations return tickets for playback outcomes. |
| Systems | [`ISystem`, `SystemPipeline`, `SystemContext`, `Tick`](src/ecs/systems/SystemPipeline.cs) provide ordered updates, shared commands and command/event checkpoints. |
| Persistence | [`[ComponentKey]`, `[TagName]`](src/ecs/serialization/PersistenceAttributes.cs) generate persistence metadata. [`StoreSerializer`](src/ecs/serialization/StoreSerializer.cs) reads/writes components, stores and prefabs, with explicit version migrations and entity-reference remapping. |
| Prefabs | [`PrefabBuilder`](src/ecs/prefabs/PrefabBuilder.cs) and [`PrefabGraphBuilder`](src/ecs/prefabs/PrefabGraph.cs) build immutable templates; `store.Instantiate(...)` creates independent entities and remaps graph references. |

Each store takes ownership of its supplied storage adapter and belongs to its
creating thread. Component reads return owned values;
apply changes through mutation methods to maintain indexes and capture events.
`Add` requires an absent component, `Update` requires an existing component, and
`Upsert` handles either case. Components containing mutable reference data need
an explicit [copy policy](src/ecs/Components.cs).

Existing components can be updated during query traversal. Use a command buffer
for structural changes such as adding/removing components, creating entities or
deleting them. Command playback and event delivery are explicit; dispose stores,
command buffers, pipelines and subscriptions when finished.

## Example

This console example uses the runtime and analyzer references above. It creates
an indexed entity, updates a component through a filtered query, defers a tag
removal and dispatches the captured update event.

```csharp
using Archy;
using Archy.Adapters.Arch;

using var store = new EntityStore(new ArchStorage());
var entity = store.Create();

entity.Add(new Position { X = 10, Y = 20 });
entity.Add(new Identity { Value = 42 });
entity.AddTag<Active>();

using var subscription = entity.Events.Subscribe<ComponentUpdated<Position>>(change =>
  Console.WriteLine($"{change.OldValue.X} -> {change.NewValue.X}"));

store.Query<Position>().AllTags(Tags.Get<Active>()).ForEachEntity((position, current) => {
  position.X += 1;
  current.Update(position);
});

var identities = store.ComponentIndex<Identity, int>();

Console.WriteLine(identities[42].Single() == entity);

using var commands = store.CreateCommandBuffer();

foreach (var current in store.Query<Position>().Entities) commands.RemoveTag<Active>(current);

commands.Playback();
store.Events.Dispatch();

public struct Position : IComponent {
  public float X { get; set; }
  public float Y { get; set; }
}

public partial struct Identity : IComponent {

  [Indexed(Unique = true)]
  public int Value { get; init; }
}

public struct Active : ITag { }
```

Output:

```text
True
10 -> 11
```

Store restoration selects an adapter through a factory:
`serializer.ReadStore(json, static () => new ArchStorage())`. Each call must create
fresh storage; the restored store owns it and disposes it if loading fails.

## Benchmarks

[`benchmarks/Archy.Benchmarks`](benchmarks/Archy.Benchmarks/README.md) compares direct
Arch, Friflo, Archy with Arch storage, and Archy with naive storage across six workloads:
world loading, movement, actor activation, projectile churn, spatial lookups and
deferred damage notifications. It includes reference-model verification and
BenchmarkDotNet timing/allocation runs.

```sh
dotnet run --project benchmarks/Archy.Benchmarks -c Release -- --smoke
dotnet run --project benchmarks/Archy.Benchmarks -c Release
```

### Results recorded on 2026-10-16

> Device disclaimer: one Apple M2 Pro machine with 10 physical cores, macOS
> Tahoe 26.6.2, and .NET 10.0.11 Arm64 RyuJIT (SDK 10.0.400), on AC power with
> low-power mode disabled. macOS denied high-priority scheduling. Substantial
> background CPU activity was observed, and BenchmarkDotNet flagged multimodal
> timing distributions. These results describe one uncontrolled run. Small or
> noisy differences require confirmation on an idle host; hardware, runtime and
> power settings can also change the results.

All 48 cases passed using BenchmarkDotNet 0.15.8's default measurement job in
about 41 minutes, with Arch 2.1.0 and Friflo.Engine.ECS 3.6.0. All four
implementations passed 12,912 reference-model state comparisons before timing.
The values below come from this four-way run; they replace the earlier
three-way results.

Times are microseconds per invocation, shown as mean ± BenchmarkDotNet's
reported 99.9% confidence-interval half-width; lower is faster. Those intervals
do not account for differences in background activity between processes.
Actor activation covers a complete 16-tick cycle. See the
[scenario definitions](benchmarks/Archy.Benchmarks/README.md#scenarios) for each
invocation's work.

| Scenario | Entities | Direct Arch (µs) | Friflo (µs) | Archy/Arch (µs) | Archy/Naive (µs) |
| --- | ---: | ---: | ---: | ---: | ---: |
| World loading | 1,024 | 39.723 ± 1.161 | 31.211 ± 1.187 | 437.752 ± 8.718 | 411.876 ± 3.843 |
| Movement | 1,024 | 2.164 ± 0.063 | 2.095 ± 0.013 | 7.069 ± 0.030 | 8.038 ± 0.109 |
| Actor activation | 1,024 | 96.131 ± 1.906 | 100.440 ± 1.843 | 234.091 ± 1.900 | 418.179 ± 8.090 |
| Projectile churn | 1,024 | 11.450 ± 0.103 | 9.710 ± 0.145 | 86.245 ± 0.353 | 97.828 ± 0.241 |
| Spatial lookup | 1,024 | 21.330 ± 0.149 | 12.570 ± 0.063 | 52.024 ± 1.036 | 51.172 ± 0.406 |
| Damage notifications | 1,024 | 1.338 ± 0.013 | 2.096 ± 0.041 | 11.037 ± 1.656 | 5.641 ± 0.110 |
| World loading | 16,384 | 631.372 ± 5.102 | 1,153.430 ± 74.088 | 6,792.210 ± 109.723 | 6,923.484 ± 137.976 |
| Movement | 16,384 | 34.071 ± 0.322 | 35.502 ± 0.661 | 127.003 ± 2.450 | 133.651 ± 2.531 |
| Actor activation | 16,384 | 1,698.094 ± 33.766 | 1,248.792 ± 50.616 | 4,404.948 ± 139.708 | 7,519.503 ± 372.522 |
| Projectile churn | 16,384 | 203.375 ± 4.022 | 166.712 ± 3.098 | 1,461.296 ± 24.381 | 1,701.323 ± 33.862 |
| Spatial lookup | 16,384 | 443.162 ± 6.235 | 237.706 ± 3.070 | 970.471 ± 27.427 | 1,139.503 ± 24.624 |
| Damage notifications | 16,384 | 29.086 ± 0.581 | 43.293 ± 0.861 | 106.257 ± 2.855 | 107.799 ± 3.023 |

Friflo had lower mean projectile-churn and spatial-lookup times than direct
Arch at both populations in this run. Native movement means were close; world
loading changed order with population. The small Archy/Arch notification case
was particularly noisy: 11.037 µs mean and 4.830 µs standard deviation. That
result does not establish a reliable notification-performance ranking between
the Archy adapters.

The implementations have different mutation, ownership and cleanup contracts.
Arch uses manual spatial buckets; Friflo and Archy use their native indexes.
Friflo's managed store has no disposal API, so world loading clears the benchmark's
entity handles and releases the store for garbage collection; Arch and Archy
call `Dispose`. Read the
[measurement details](benchmarks/Archy.Benchmarks/README.md#what-is-included)
before attributing timing differences to a particular API feature.

Managed bytes allocated per invocation are shown below. These measure
allocation after persistent-scenario setup, not total memory usage.

| Scenario | Entities | Direct Arch (B) | Friflo (B) | Archy/Arch (B) | Archy/Naive (B) |
| --- | ---: | ---: | ---: | ---: | ---: |
| World loading | 1,024 | 121,920 | 173,976 | 287,848 | 114,472 |
| Movement | 1,024 | 0 | 0 | 0 | 0 |
| Actor activation | 1,024 | 0 | 0 | 0 | 0 |
| Projectile churn | 1,024 | 0 | 0 | 0 | 0 |
| Spatial lookup | 1,024 | 0 | 0 | 14,317 | 14,317 |
| Damage notifications | 1,024 | 0 | 0 | 32 | 32 |
| World loading | 16,384 | 1,378,401 | 3,124,637 | 2,054,392 | 1,465,960 |
| Movement | 16,384 | 0 | 0 | 0 | 0 |
| Actor activation | 16,384 | 0 | 0 | 0 | 0 |
| Projectile churn | 16,384 | 0 | 0 | 0 | 0 |
| Spatial lookup | 16,384 | 0 | 0 | 267,920 | 268,512 |
| Damage notifications | 16,384 | 0 | 0 | 32 | 32 |

Movement, activation and projectile churn recorded zero managed allocations for
all four implementations. Friflo and direct Arch also recorded zero allocations
for spatial lookups and notifications. Both Archy adapters allocated index
snapshots during spatial lookups and 32 B per notification invocation.

## Reference

- [Storage and integration](src/ecs/IMPLEMENTATION.md)
- [Adding an adapter](docs/adapters.md)
- [ECS contracts and original design record](docs/ecs-replacement-spec.md)
- [C# conventions](docs/tools/csharp.md)
- Runtime examples and Arch adapter checks: `tests/ecs/`
- Shared API and naive adapter checks without Arch: `tests/api/`
- Compiled generator consumer checks: `tests/ecs-generator/`
