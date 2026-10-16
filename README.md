# Archy

Standalone C# ECS runtime built on Arch 2.1.0, with generated indexes,
entity relationships, deferred events and commands, systems, persistence and prefabs.

Extracted from Lia's `custom-ecs` branch at
`3c40e51f2b4ff36a9e3f711d686a28f124184ef5` (`refactor: consolidate ecs on arch nuget`).
The runtime assembly and namespace are `Archy`; the generator assembly is
`Archy.Generator`, with its generators in `Archy.Generators`.
This project runs independently of Godot and Lia gameplay.

## Setup and verification

Install the .NET 10 SDK selected by `global.json`, then run from this directory:

```sh
dotnet build Archy.slnx
dotnet run --no-build --project tests/ecs
dotnet run --no-build --project tests/ecs-generator
dotnet build Archy.slnx -c Release
dotnet run --no-build --project tests/ecs -c Release
dotnet run --no-build --project tests/ecs-generator -c Release
```

NuGet restores Arch 2.1.0 and the Roslyn generator dependencies during the build.

## Consumer setup

Reference `src/ecs/Archy.csproj` from your application. Also reference
`tools/ecs-generator/Archy.Generator.csproj` as an analyzer in each project
that declares indexed or persistent components:

```xml
<ProjectReference Include="path/to/archy/src/ecs/Archy.csproj" />
<ProjectReference Include="path/to/archy/tools/ecs-generator/Archy.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Use the `Archy` namespace. Dispose stores and subscriptions, commit component
changes through mutation methods, and dispatch deferred events explicitly or
through system checkpoints.

## Reference

- [Storage and integration](src/ecs/IMPLEMENTATION.md)
- [ECS contracts and original design record](docs/ecs-replacement-spec.md)
- [C# conventions inherited from Lia](docs/tools/csharp.md)
- Runtime checks: `tests/ecs/`
- Compiled generator consumer checks: `tests/ecs-generator/`
