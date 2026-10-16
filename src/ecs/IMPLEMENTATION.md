# Archy integration and storage

The runtime is split into two projects targeting .NET 10:

- `src/ecs/Archy.csproj` implements the `Archy` API and defines the adapter contract
  in `Archy.Storage`. It has no package or adapter dependencies.
- `src/adapters/arch/Archy.Adapters.Arch.csproj` implements `ArchStorage` in
  `Archy.Adapters.Arch`, referencing the API layer and the
  [Arch 2.1.0 NuGet package](https://www.nuget.org/packages/Arch/2.1.0).

The generator remains a separate .NET Standard 2.0 analyzer. Runtime, adapters,
generator and checks have no gameplay or Godot dependency.

## Consumer setup

Reference the API layer and selected adapter from the application. Adjust these
paths to the consuming project:

```xml
<ProjectReference Include="path/to/archy/src/ecs/Archy.csproj" />
<ProjectReference Include="path/to/archy/src/adapters/arch/Archy.Adapters.Arch.csproj" />
<ProjectReference Include="path/to/archy/tools/ecs-generator/Archy.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Component-only assemblies can reference the API layer without any adapter. The
analyzer must run in each project declaring indexed or persistent components.
Declare indexed components as partial structs and mark the key member with
`[Indexed]`; generated module initialization registers their metadata.

Create stores explicitly with `new EntityStore(new ArchStorage())`. A store owns
its adapter, entities, deferred events and command buffers. Dispose owned stores
and subscriptions. Apply component writes through mutation methods; query values
are owned copies. Dispatch events explicitly or through system checkpoints.

`StoreSerializer.ReadStore` takes a storage factory, for example
`serializer.ReadStore(json, static () => new ArchStorage())`. The factory must
create fresh storage for every load. Failed loads dispose the unpublished store
and its adapter. Component and prefab serialization need no storage factory.

## Storage seam

Archy owns public identities, generations, command reservations, indexes,
lifecycle/custom events, owned values and query filters. The API layer declares
`IEntityStorage`, `IStorageArchetype` and `IStorageChunk`. Adapters provide typed
component arrays, full public identities and dense chunk populations. Structural
moves and deletion use swap removal and clear vacated rows. Archy repairs the
public slot of the moved entity after each structural operation.

Storage arrays are available to the API implementation and adapter authors only
through this extension contract. The store does not expose its adapter; public
queries return owned values and enforce traversal lifetimes.

## Arch adapter

Arch owns native entities, locations, component arrays, chunks and structural
changes. `ArchStorage` keeps native worlds private and uses Arch's public creation,
capacity, location, typed add/remove and deletion APIs. A storage identity
component maps native rows to public IDs and full unsigned generations.
Reservations allocate public handles without creating native entities.

Arch 2.1.0 identifies archetypes by signature hash. The adapter validates exact
native signatures before reuse. A colliding composition receives a separate
native world. Cross-world moves create the destination row, copy retained
component columns and delete the source entity through public APIs. The API
matches queries against its own exact composition masks, avoiding Arch's
hash-based query cache. The adapter clears vacated component rows and retained
chunk payloads on disposal.

There is no vendored dependency, private Arch access or reflection into Arch
internals. All configurations use the same NuGet package.

## Verification

Run the [README checks](../../README.md#setup-and-verification) in Debug and Release.
The Arch suite includes native signature collisions, generation saturation,
reservation/reuse, managed-payload cleanup and allocation bounds alongside the
public ECS contracts. `tests/api/` runs the shared public behavior suites against
a test adapter without referencing Arch. Generator checks compile and run
consumers of indexed and persistent components.

```sh
dotnet build src/ecs/Archy.csproj -c ExportRelease
dotnet build src/adapters/arch/Archy.Adapters.Arch.csproj -c ExportRelease
```

See [adding an adapter](../../docs/adapters.md) for the storage contract. Godot
import/startup and rendered checks do not apply to these standalone projects.
