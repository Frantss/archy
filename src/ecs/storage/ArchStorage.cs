using Arch.Core;
using NativeArchetype = Arch.Core.Archetype;
using NativeEntity = Arch.Core.Entity;

namespace Archy;

internal readonly record struct NativeIdentity(int Id, uint Generation);

internal sealed class ArchStorage : IDisposable {
  private readonly World _world = World.Create();
  private readonly List<World> _collisions = [];
  private readonly Dictionary<NativeArchetype, World> _owners = [];

  public NativeArchetype CreateArchetype(Signature signature) {
    var world = this._world;

    // Arch 2.1 identifies archetypes by hash; isolate distinct signatures sharing that hash.
    if (world.TryGetArchetype(signature, out var existing) && !Matches(existing, signature)) {
      world = World.Create();
      this._collisions.Add(world);
    }

    var archetype = world.EnsureCapacity(signature, 0);
    this._owners.Add(archetype, world);

    return archetype;
  }

  public void PrepareCapacity(NativeArchetype archetype, int count) =>
    this._owners[archetype].EnsureCapacity(archetype.Signature, count);

  public Slot Create(NativeArchetype archetype, NativeIdentity identity) {
    var world = this._owners[archetype];
    var entity = world.Create(archetype.Signature);
    world.Set(entity, identity);

    return world.GetEntityData(entity).Slot;
  }

  public Slot Move(NativeEntity entity, (NativeArchetype Source, NativeArchetype Target, TypeDescriptor Type, bool Add) change) {
    var world = this._owners[change.Source];
    var destination = this._owners[change.Target];

    if (!ReferenceEquals(world, destination)) {
      var from = world.GetEntityData(entity).Slot;
      var created = destination.Create(change.Target.Signature);
      var to = destination.GetEntityData(created).Slot;
      ref var oldChunk = ref change.Source.GetChunk(from.ChunkIndex);
      ref var newChunk = ref change.Target.GetChunk(to.ChunkIndex);

      foreach (var type in change.Source.Signature.Components) {
        if (change.Target.Has(type)) Array.Copy(oldChunk.GetArray(type), from.Index, newChunk.GetArray(type), to.Index, 1);
      }

      world.Destroy(entity);

      return to;
    }

    change.Type.ChangeNative(world, entity, change.Add);

    return world.GetEntityData(entity).Slot;
  }

  public void Remove(NativeEntity entity, NativeArchetype archetype) => this._owners[archetype].Destroy(entity);

  public void Dispose() {
    this._world.Dispose();

    foreach (var world in this._collisions) world.Dispose();

    this._collisions.Clear();
    this._owners.Clear();
  }

  private static bool Matches(NativeArchetype archetype, Signature signature) {
    if (archetype.Signature.Components.Length != signature.Components.Length) return false;

    foreach (var type in signature.Components) {
      if (!archetype.Has(type)) return false;
    }

    return true;
  }
}
