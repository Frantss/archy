using Archy.Storage;

namespace Archy.Tests;

internal sealed class MemoryStorage : IEntityStorage {
  private readonly List<MemoryArchetype> _archetypes = [];
  public bool Disposed { get; private set; }

  public void Register<T>() where T : struct { }

  public IStorageArchetype CreateArchetype(ReadOnlySpan<Type> types) {
    var archetype = new MemoryArchetype(types.ToArray());
    this._archetypes.Add(archetype);

    return archetype;
  }

  public void PrepareCapacity(IStorageArchetype archetype, int count) =>
    ((MemoryArchetype)archetype).PrepareCapacity(count);

  public StorageLocation Create(IStorageArchetype archetype, StorageIdentity identity) =>
    ((MemoryArchetype)archetype).Append(identity);

  public StorageLocation Move(StorageLocation source, (IStorageArchetype Target, Type Type, bool Add) change) {
    var from = (MemoryArchetype)source.Archetype;
    var target = (MemoryArchetype)change.Target;
    var chunk = from.GetChunk(source.ChunkIndex);
    var destination = target.Append(chunk.Identities[source.Row]);
    var next = target.GetChunk(destination.ChunkIndex);

    foreach (var type in from.Types) {
      if (target.Types.Contains(type))
        Array.Copy(chunk.GetComponents(type), source.Row, next.GetComponents(type), destination.Row, 1);
    }

    this.Remove(source);

    return destination;
  }

  public void Remove(StorageLocation location) {
    var archetype = (MemoryArchetype)location.Archetype;
    var lastIndex = archetype.EntityCount - 1;
    var last = archetype.GetChunk(lastIndex / MemoryArchetype.Capacity);
    var row = lastIndex % MemoryArchetype.Capacity;
    var target = archetype.GetChunk(location.ChunkIndex);
    target.Identities[location.Row] = last.Identities[row];
    last.Identities[row] = default;

    foreach (var type in archetype.Types) {
      var values = last.GetComponents(type);
      Array.Copy(values, row, target.GetComponents(type), location.Row, 1);
      Array.Clear(values, row, 1);
    }

    archetype.EntityCount--;
  }

  public void Dispose() {
    foreach (var archetype in this._archetypes) archetype.Clear();

    this._archetypes.Clear();
    this.Disposed = true;
  }

  private sealed class MemoryArchetype : IStorageArchetype {
    public const int Capacity = 64;
    private readonly List<MemoryChunk> _chunks = [];
    public Type[] Types { get; }
    public int EntityCount { get; set; }
    public int ChunkCount => (this.EntityCount + Capacity - 1) / Capacity;

    public MemoryArchetype(Type[] types) => this.Types = types;

    public IStorageChunk GetChunk(int index) => this._chunks[index];

    public void PrepareCapacity(int count) {
      var required = (this.EntityCount + count + Capacity - 1) / Capacity;

      while (this._chunks.Count < required) this._chunks.Add(new(this, this._chunks.Count));
    }

    public StorageLocation Append(StorageIdentity identity) {
      this.PrepareCapacity(1);
      var index = this.EntityCount++;
      this._chunks[index / Capacity].Identities[index % Capacity] = identity;

      return new() { Archetype = this, ChunkIndex = index / Capacity, Row = index % Capacity };
    }

    public void Clear() {
      foreach (var chunk in this._chunks) {
        Array.Clear(chunk.Identities);

        foreach (var values in chunk.Values.Values) Array.Clear(values);
      }

      this._chunks.Clear();
      this.EntityCount = 0;
    }
  }

  private sealed class MemoryChunk : IStorageChunk {
    private readonly MemoryArchetype _owner;
    private readonly int _index;
    public Dictionary<Type, Array> Values { get; }
    public StorageIdentity[] Identities { get; } = new StorageIdentity[MemoryArchetype.Capacity];
    public int Count => Math.Clamp(
      this._owner.EntityCount - this._index * MemoryArchetype.Capacity,
      0,
      MemoryArchetype.Capacity
    );

    public MemoryChunk(MemoryArchetype owner, int index) {
      this._owner = owner;
      this._index = index;
      this.Values = owner.Types.ToDictionary(
        type => type,
        type => Array.CreateInstance(type, MemoryArchetype.Capacity)
      );
    }

    public Array GetComponents(Type type) => this.Values[type];
  }
}
