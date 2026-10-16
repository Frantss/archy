# Archy integration and storage

`Archy.csproj` is the single ECS runtime, targeting .NET 10 with the
[Arch 2.1.0 NuGet package](https://www.nuget.org/packages/Arch/2.1.0).
Its public namespace is `Archy`. The runtime, generator and their checks run
without gameplay or Godot dependencies.

## Consumer setup

Reference the runtime and source generator from the consuming project. Adjust
these paths to the consuming project:

```xml
<ProjectReference Include="path/to/archy/src/ecs/Archy.csproj" />
<ProjectReference Include="path/to/archy/tools/ecs-generator/Archy.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

The analyzer must run in the project declaring indexed or persistent components.
Declare indexed components as partial structs and mark the key member with
`[Indexed]`; generated module initialization registers their metadata. Runtime
and generator consumer tests demonstrate compiled registration, diagnostics and
persistence. Consumers must reference `Archy.csproj` and recompile for the
`Archy` assembly identity.

Stores own their entities, deferred events and command buffers. Dispose owned
stores and subscriptions. Apply component writes through mutation methods;
query values are owned copies. Dispatch deferred events explicitly or through
system-pipeline checkpoints. See the [specification](../../docs/ecs-replacement-spec.md)
for mutation, event, reference, query and persistence contracts.

## Storage boundary

Arch owns native entities, locations, component arrays, chunks and structural
changes. Archy owns public identities, generations, command reservations, indexes,
lifecycle/custom events and owned values. A private identity component maps native
rows to public IDs and full unsigned generations. Reservations allocate public
handles without creating native entities.

`ArchStorage` keeps native worlds private and uses Arch's public creation,
capacity, location, typed add/remove and deletion APIs. Typed traversal binds public
chunk arrays. Archy tracks public-handle locations and clears vacated component
rows after native swap removal to release managed references. Public query
chunks enforce traversal lifetimes; the API exposes no native world or writable
component references.

Arch 2.1.0 identifies archetypes by signature hash. Archy keeps exact composition
identity in its canonical map and validates native signatures before reuse. A
colliding composition receives a separate native world. Moving across worlds
creates the destination row, copies retained component columns and deletes the
source entity through public APIs. Query matching checks exact native component
membership and Archy's filters, avoiding Arch's hash-based query cache.

There is no vendored dependency, custom Arch partial class, friend access or
reflection into Arch internals. Debug, Release and Godot export configurations
all consume the same NuGet package.

## Verification

```sh
dotnet run --project tests/ecs/Archy.Tests.csproj
dotnet run --project tests/ecs/Archy.Tests.csproj -c Release
dotnet run --project tests/ecs-generator/Archy.Generator.Tests.csproj
dotnet run --project tests/ecs-generator/Archy.Generator.Tests.csproj -c Release
dotnet build src/ecs/Archy.csproj -c ExportRelease
```

The runtime suite includes native signature collisions, generation saturation,
reservation/reuse, managed-payload cleanup and allocation bounds alongside the
public ECS contracts. Generator checks compile and run consumers of indexed and
persistent components. Archy has no Godot project or game-facing checks.
