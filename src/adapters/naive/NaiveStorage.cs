using Archy.Storage;

namespace Archy.Adapters.Naive;

public sealed class NaiveStorage : IEntityStorage {
  private readonly List<NaiveArchetype> _archetypes = [];
  private bool _disposed;

  public void Register<T>() where T : struct => this.EnsureActive();

  public IStorageArchetype CreateArchetype(ReadOnlySpan<Type> types) {
    this.EnsureActive();
    var archetype = new NaiveArchetype(types.ToArray());
    this._archetypes.Add(archetype);

    return archetype;
  }

  public void PrepareCapacity(IStorageArchetype archetype, int count) {
    this.EnsureActive();
    ((NaiveArchetype)archetype).PrepareCapacity(count);
  }

  public StorageLocation Create(IStorageArchetype archetype, StorageIdentity identity) {
    this.EnsureActive();

    return ((NaiveArchetype)archetype).Append(identity);
  }

  public StorageLocation Move(StorageLocation source, (IStorageArchetype Target, Type Type, bool Add) change) {
    this.EnsureActive();
    var from = (NaiveArchetype)source.Archetype;
    var target = (NaiveArchetype)change.Target;
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
    this.EnsureActive();
    var archetype = (NaiveArchetype)location.Archetype;
    var lastIndex = archetype.EntityCount - 1;
    var last = archetype.GetChunk(lastIndex / NaiveArchetype.Capacity);
    var row = lastIndex % NaiveArchetype.Capacity;
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
    if (this._disposed) return;

    this._disposed = true;

    foreach (var archetype in this._archetypes) archetype.Clear();

    this._archetypes.Clear();
  }

  private void EnsureActive() => ObjectDisposedException.ThrowIf(this._disposed, this);

  private sealed class NaiveArchetype : IStorageArchetype {
    public const int Capacity = 64;
    private readonly List<NaiveChunk> _chunks = [];
    public Type[] Types { get; }
    public int EntityCount { get; set; }
    public int ChunkCount => (this.EntityCount + Capacity - 1) / Capacity;

    public NaiveArchetype(Type[] types) => this.Types = types;

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

  private sealed class NaiveChunk : IStorageChunk {
    private readonly NaiveArchetype _owner;
    private readonly int _index;
    public Dictionary<Type, Array> Values { get; }
    public StorageIdentity[] Identities { get; } = new StorageIdentity[NaiveArchetype.Capacity];
    public int Count => Math.Clamp(
      this._owner.EntityCount - this._index * NaiveArchetype.Capacity,
      0,
      NaiveArchetype.Capacity
    );

    public NaiveChunk(NaiveArchetype owner, int index) {
      this._owner = owner;
      this._index = index;
      this.Values = owner.Types.ToDictionary(
        type => type,
        type => Array.CreateInstance(type, NaiveArchetype.Capacity)
      );
    }

    public Array GetComponents(Type type) => this.Values[type];
  }
}
