# Adding a storage adapter

`Archy` owns the ECS behavior. Adapters implement the storage contract in
[`Archy.Storage`](../src/ecs/storage/IEntityStorage.cs) and reference
[`Archy.csproj`](../src/ecs/Archy.csproj). The API layer must not reference an
adapter or its native library. No global adapter registration or assembly scanning
is required.

## Selecting storage

Applications create a fresh adapter for each store:

```csharp
using Archy;
using Archy.Adapters.Arch;

using var store = new EntityStore(new ArchStorage());
```

`EntityStore` owns the supplied adapter after successful construction. Do not
share an adapter between stores or dispose it separately while its store is live.
Store operations run on the creating thread. The adapter must not invoke
application callbacks during structural mutations.

Restoration also requires fresh storage:
`serializer.ReadStore(json, static () => new ArchStorage())`. The factory is called
after document-header validation. The restored store disposes the adapter if
component decoding or construction fails. Serialized data contains logical values
and references, so selecting an adapter does not change the persistence format.

## Contract

| Member | Required behavior |
| --- | --- |
| `Register<T>()` | Prepare typed add/remove operations for a component or tag. Repeated registration must be harmless. The API registers types before requesting a composition containing them. |
| `CreateArchetype(types)` | Return a fresh, empty storage archetype with exactly those component/tag types. Private identity storage may be added internally. Distinct compositions must remain distinct even when native signature hashes collide. |
| `PrepareCapacity(archetype, count)` | Prepare space for `count` additional entities without publishing rows or changing identity, values or membership. |
| `Create(archetype, identity)` | Append a row containing the supplied full `int` ID and `uint` generation. Return its chunk and row. Component values start at their defaults. |
| `Move(source, change)` | Append to the target composition, preserving identity and retained component values. New components start at their defaults. Remove the source row using the swap rule below. Return the destination location. |
| `Remove(location)` | Remove the row using the swap rule below. |
| `Dispose()` | Release native resources and clear payload arrays, including arrays retained by old chunk wrappers, so they do not retain application references. |

The API requests moves between compositions differing by the specified component
or tag. It supplies locations belonging to the adapter and owns validation,
copy policies, indexes, references and event capture. Adapters do not duplicate
those behaviors.

Each archetype exposes a dense sequence of active chunks. `ChunkCount` is zero
for an empty archetype and excludes retained spare chunks. Its final active chunk
contains the final live row. Removal copies that final row into the removed row,
unless they are the same row, then clears the vacated identity and every component
value. Moves use the same rule on the source archetype. These rules let Archy
repair the moved public handle without backend-specific metadata.

`IStorageChunk.Identities` and `GetComponents(type)` expose stable backing arrays
for the chunk's lifetime; component arrays must have runtime type `T[]` for the
requested component type. Their row positions must stay aligned. Chunk objects
and backing arrays must remain stable across capacity growth, shrinkage and reuse.
Report live counts immediately after every mutation. Archy caches component-array
bindings for traversal and mirrors row counts after structural writes.

Native worlds, native entity handles, signature types and scheduler types stay
inside the adapter project. A backend with different native layout must provide
the dense chunk contract at this seam.

## Implementation and checks

1. Create an adapter project referencing the API layer and its own native library.
2. Implement `IEntityStorage` and its archetype/chunk views. Follow the contract
   above for identity projection, stable arrays, swap removal and disposal.
3. Add a test runner that shares the public behavior suites from `tests/ecs/` and
   supplies its adapter through `StoreFactory`. Keep native collision or allocation
   checks specific to that adapter.
4. Add the project to `Archy.slnx`, document how applications construct it, and run
   the README checks in Debug and Release.

[`tests/api/`](../tests/api/Archy.Api.Tests.csproj) demonstrates this without Arch:
its small test adapter uses 64-row managed chunks and runs the shared component,
query, index, event, command, relationship, system, persistence and prefab suites.
It also checks adapter disposal and failed restoration cleanup. It is a test
fixture, not an additional supported runtime adapter.

[`ArchStorage`](../src/adapters/arch/ArchStorage.cs) implements the production
adapter. Its existing checks cover native signature collisions, managed-payload
cleanup, chunk growth/reuse, full unsigned generations and allocation budgets.
