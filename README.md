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
project restores BenchmarkDotNet. The API-only checks use the supported naive
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
Arch, Archy with Arch storage, and Archy with naive storage across six workloads:
world loading, movement, actor activation, projectile churn, spatial lookups and
deferred damage notifications. It includes reference-model verification and
BenchmarkDotNet timing/allocation runs.

```sh
dotnet run --project benchmarks/Archy.Benchmarks -c Release -- --smoke
dotnet run --project benchmarks/Archy.Benchmarks -c Release
```

### Results recorded on 2026-10-16

> Device disclaimer: these results come from one Apple M2 Pro machine with 10
> physical cores, running macOS Tahoe 26.6.2 and .NET 10.0.11 Arm64 RyuJIT
> (SDK 10.0.400). The machine used AC power with low-power mode disabled.
> Hardware, runtime, power settings and other system activity can change the
> results. macOS denied high-priority scheduling, so the processes ran at normal
> priority. Several cases had outliers or broad timing distributions; small
> differences between adapters need another run before drawing conclusions.

All 36 cases passed using BenchmarkDotNet 0.15.8's default measurement job in
about 18 minutes. Preflight reference verification passed 9,684 state comparisons.
These are mean times in microseconds per invocation; lower is faster. Actor
activation measures a complete 16-tick cycle. See the
[scenario definitions](benchmarks/Archy.Benchmarks/README.md#scenarios) for each
invocation's work.

| Scenario | Entities | Direct Arch (µs) | Archy/Arch (µs) | Archy/Naive (µs) |
| --- | ---: | ---: | ---: | ---: |
| World loading | 1,024 | 40.73 | 428.22 | 407.82 |
| Movement | 1,024 | 2.01 | 7.44 | 7.21 |
| Actor activation | 1,024 | 93.01 | 237.53 | 387.63 |
| Projectile churn | 1,024 | 11.41 | 87.46 | 99.72 |
| Spatial lookup | 1,024 | 21.13 | 53.90 | 51.05 |
| Damage notifications | 1,024 | 1.31 | 5.53 | 5.64 |
| World loading | 16,384 | 615.03 | 6,654.77 | 6,882.79 |
| Movement | 16,384 | 34.46 | 139.43 | 131.65 |
| Actor activation | 16,384 | 1,635.06 | 4,087.69 | 6,470.78 |
| Projectile churn | 16,384 | 207.09 | 1,455.16 | 1,629.64 |
| Spatial lookup | 16,384 | 451.75 | 946.56 | 1,047.85 |
| Damage notifications | 16,384 | 26.26 | 97.00 | 102.83 |

Direct Arch had the lowest mean in every case. Naive activation took about 1.6
times the Arch adapter's time; naive projectile churn took 12% to 14% longer.
Other adapter differences were smaller, and some changed direction with
population. Direct Arch uses writable component references, while Archy includes
its entity, mutation, index and event guarantees. These results compare the
documented application workloads across those APIs.

Movement, activation and projectile churn recorded zero managed allocations for
all three implementations after setup. Both Archy adapters allocated 14,317 B
per spatial invocation at 1,024 entities and 267,329 B at 16,384, plus 32 B per
damage-notification invocation at either population; direct Arch recorded zero
for those scenarios. At 16,384 entities, world loading allocated 1.31 MiB for
direct Arch, 1.96 MiB for Archy/Arch and 1.40 MiB for Archy/Naive. These figures
measure managed bytes allocated per invocation, not total memory usage.

## Reference

- [Storage and integration](src/ecs/IMPLEMENTATION.md)
- [Adding an adapter](docs/adapters.md)
- [ECS contracts and original design record](docs/ecs-replacement-spec.md)
- [C# conventions](docs/tools/csharp.md)
- Runtime examples and Arch adapter checks: `tests/ecs/`
- Shared API and naive adapter checks without Arch: `tests/api/`
- Compiled generator consumer checks: `tests/ecs-generator/`
