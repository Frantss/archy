using global::Arch.Core;
using Archy.Storage;
using NativeArchetype = global::Arch.Core.Archetype;
using NativeEntity = global::Arch.Core.Entity;

namespace Archy.Adapters.Arch;

public sealed class ArchStorage : IEntityStorage {
  private readonly World _world = World.Create();
  private readonly List<World> _collisions = [];
  private readonly List<ArchArchetype> _archetypes = [];
  private readonly Dictionary<Type, Action<World, NativeEntity, bool>> _changes = [];

  public void Register<T>() where T : struct => this._changes.TryAdd(typeof(T), static (world, entity, add) => {
    if (add) world.Add<T>(entity);
    else world.Remove<T>(entity);
  });

  public IStorageArchetype CreateArchetype(ReadOnlySpan<Type> types) {
    var components = new ComponentType[types.Length + 1];
    components[0] = Component<StorageIdentity>.ComponentType;

    for (var i = 0; i < types.Length; i++) components[i + 1] = Component.GetComponentType(types[i]);

    var signature = new Signature(components);
    var world = this._world;

    // Arch 2.1 identifies archetypes by hash; isolate distinct signatures sharing that hash.
    if (world.TryGetArchetype(signature, out var existing) && !Matches(existing, signature)) {
      world = World.Create();
      this._collisions.Add(world);
    }

    var archetype = new ArchArchetype(world, world.EnsureCapacity(signature, 0));
    this._archetypes.Add(archetype);

    return archetype;
  }

  public void PrepareCapacity(IStorageArchetype archetype, int count) {
    var storage = (ArchArchetype)archetype;
    storage.World.EnsureCapacity(storage.Native.Signature, count);
  }

  public StorageLocation Create(IStorageArchetype archetype, StorageIdentity identity) {
    var storage = (ArchArchetype)archetype;
    var entity = storage.World.Create(storage.Native.Signature);
    storage.World.Set(entity, identity);

    return storage.Location(entity);
  }

  public StorageLocation Move(StorageLocation source, (IStorageArchetype Target, Type Type, bool Add) change) {
    var from = (ArchArchetype)source.Archetype;
    var target = (ArchArchetype)change.Target;
    var lastChunk = from.Native.Count;
    var lastRow = from.Native.GetChunk(lastChunk).Count - 1;
    var entity = from.Native.GetChunk(source.ChunkIndex).Entities[source.Row];
    StorageLocation destination;

    if (!ReferenceEquals(from.World, target.World)) {
      var created = target.World.Create(target.Native.Signature);
      destination = target.Location(created);
      ref var oldChunk = ref from.Native.GetChunk(source.ChunkIndex);
      ref var newChunk = ref target.Native.GetChunk(destination.ChunkIndex);

      foreach (var type in from.Native.Signature.Components) {
        if (target.Native.Has(type))
          Array.Copy(oldChunk.GetArray(type), source.Row, newChunk.GetArray(type), destination.Row, 1);
      }

      from.World.Destroy(entity);
    } else {
      this._changes[change.Type](from.World, entity, change.Add);
      destination = target.Location(entity);
    }

    ClearRow(from.Native, lastChunk, lastRow);

    return destination;
  }

  public void Remove(StorageLocation location) {
    var storage = (ArchArchetype)location.Archetype;
    var lastChunk = storage.Native.Count;
    var lastRow = storage.Native.GetChunk(lastChunk).Count - 1;
    var entity = storage.Native.GetChunk(location.ChunkIndex).Entities[location.Row];
    storage.World.Destroy(entity);
    ClearRow(storage.Native, lastChunk, lastRow);
  }

  public void Dispose() {
    foreach (var archetype in this._archetypes) {
      // Retained native chunks can outlive active rows and escaped public query wrappers.
      for (var i = 0; i < archetype.Native.ChunkCount; i++) {
        ref var chunk = ref archetype.Native.GetChunk(i);
        Array.Clear(chunk.Entities);

        foreach (var type in archetype.Native.Signature.Components) Array.Clear(chunk.GetArray(type));
      }
    }

    this._world.Dispose();

    foreach (var world in this._collisions) world.Dispose();

    this._collisions.Clear();
    this._archetypes.Clear();
    this._changes.Clear();
  }

  private static void ClearRow(NativeArchetype archetype, int chunkIndex, int row) {
    ref var chunk = ref archetype.GetChunk(chunkIndex);
    chunk.Entities[row] = default;

    foreach (var type in archetype.Signature.Components) Array.Clear(chunk.GetArray(type), row, 1);
  }

  private static bool Matches(NativeArchetype archetype, Signature signature) {
    if (archetype.Signature.Components.Length != signature.Components.Length) return false;

    foreach (var type in signature.Components) {
      if (!archetype.Has(type)) return false;
    }

    return true;
  }

  private sealed class ArchArchetype : IStorageArchetype {
    private readonly List<ArchChunk> _chunks = [];
    public World World { get; }
    public NativeArchetype Native { get; }
    public int EntityCount => this.Native.EntityCount;
    public int ChunkCount => this.EntityCount == 0 ? 0 : this.Native.Count + 1;

    public ArchArchetype(World world, NativeArchetype native) {
      this.World = world;
      this.Native = native;
    }

    public IStorageChunk GetChunk(int index) {
      while (this._chunks.Count <= index) this._chunks.Add(new(this.Native, this._chunks.Count));

      return this._chunks[index];
    }

    public StorageLocation Location(NativeEntity entity) {
      var slot = this.World.GetEntityData(entity).Slot;

      return new() { Archetype = this, ChunkIndex = slot.ChunkIndex, Row = slot.Index };
    }
  }

  private sealed class ArchChunk : IStorageChunk {
    private readonly NativeArchetype _archetype;
    private readonly int _index;
    public int Count => this._archetype.GetChunk(this._index).Count;
    public StorageIdentity[] Identities { get; }

    public ArchChunk(NativeArchetype archetype, int index) {
      this._archetype = archetype;
      this._index = index;
      this.Identities = archetype.GetChunk(index).GetArray<StorageIdentity>();
    }

    public Array GetComponents(Type type) =>
      this._archetype.GetChunk(this._index).GetArray(Component.GetComponentType(type));
  }
}
