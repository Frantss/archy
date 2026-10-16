using System.Collections;
using System.Runtime.CompilerServices;
using Archy.Storage;

namespace Archy;

internal sealed class Archetype {
  private EntityStore _store = null!;
  private readonly List<StorageChunk> _chunks = [];
  private readonly int _firstComponentId;
  private readonly int[]? _columnOffsets;
  public TypeDescriptor[] Components { get; }
  public TypeDescriptor[] Tags { get; }
  public TypeMask ComponentMask { get; }
  public TypeMask TagMask { get; }
  public Dictionary<int, int> ColumnMap { get; } = new();
  public Dictionary<(int Type, bool Add), Archetype> Transitions { get; } = new();
  public StorageChunks Chunks { get; }
  public SnapshotShape SnapshotShape { get; }
  public IStorageArchetype Storage { get; private set; } = null!;
  public int Count => this.Storage.EntityCount;

  public Archetype(TypeDescriptor[] components, TypeDescriptor[] tags) {
    this.Components = components;
    this.Tags = tags;
    this.ComponentMask = new(components);
    this.TagMask = new(tags);
    this.SnapshotShape = new(components, tags);
    this.Chunks = new(this);

    if (components.Length > 0) {
      this._firstComponentId = components[0].Id;
      var width = components[^1].Id - this._firstComponentId + 1;

      if (width <= components.Length * 8) {
        this._columnOffsets = new int[width];
        Array.Fill(this._columnOffsets, -1);
      }
    } else this._columnOffsets = [];

    for (var i = 0; i < components.Length; i++) {
      this.ColumnMap.Add(components[i].Id, i);

      if (this._columnOffsets is not null) this._columnOffsets[components[i].Id - this._firstComponentId] = i;
    }
  }

  public void Initialize(EntityStore store, IEntityStorage storage) {
    this._store = store;
    var types = this.Components.Concat(this.Tags).ToArray();

    foreach (var type in types) type.RegisterStorage(storage);

    this.Storage = storage.CreateArchetype(types.Select(type => type.Type).ToArray());
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int ColumnIndex(int componentId) {
    if (this._columnOffsets is null) return this.ColumnMap.GetValueOrDefault(componentId, -1);

    var offset = componentId - this._firstComponentId;

    return (uint)offset < (uint)this._columnOffsets.Length ? this._columnOffsets[offset] : -1;
  }

  public (StorageChunk Chunk, int Row) Append(int entityId) => this._store.AppendStorage(this, entityId);

  public StorageChunk Bind(int index) {
    while (this._chunks.Count <= index) this._chunks.Add(new(this, this._chunks.Count));

    return this._chunks[index];
  }

  public void Clear() {
    foreach (var chunk in this._chunks) chunk.ClearReferences();

    this.Transitions.Clear();
    this._chunks.Clear();
  }
}

internal sealed class StorageChunks : IReadOnlyList<StorageChunk> {
  private readonly Archetype _owner;
  public StorageChunks(Archetype owner) => this._owner = owner;
  public int Count => this._owner.Storage.ChunkCount;
  public StorageChunk this[int index] => this._owner.Bind(index);
  public int IndexOf(StorageChunk chunk) => ReferenceEquals(chunk.Owner, this._owner) ? chunk.Index : -1;
  public IEnumerator<StorageChunk> GetEnumerator() {
    for (var i = 0; i < this.Count; i++) yield return this[i];
  }
  IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}

internal sealed class StorageChunk {
  private readonly Archetype _owner;
  public Archetype Owner => this._owner;
  public int Index { get; }
  private readonly StorageIdentity[] _identities;
  public ComponentColumn[] Columns { get; }
  // Mirror structural changes so row loops avoid calls through the adapter.
  public int Count;

  public StorageChunk(Archetype owner, int index) {
    this._owner = owner;
    this.Index = index;
    var chunk = owner.Storage.GetChunk(index);
    this.Count = chunk.Count;
    this._identities = chunk.Identities;
    this.Columns = new ComponentColumn[owner.Components.Length];

    for (var i = 0; i < this.Columns.Length; i++) {
      var descriptor = owner.Components[i];
      var column = descriptor.CreateColumn();
      column.Bind(chunk.GetComponents(descriptor.Type));
      this.Columns[i] = column;
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int EntityId(int row) {
    return this._identities[row].Id;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public Entity EntityAt(EntityStore store, int row) {
    var identity = this._identities[row];

    return new(store, identity.Id, identity.Generation);
  }

  public void ClearReferences() {
    foreach (var column in this.Columns) column.ClearReferences();
  }
}

internal abstract class ComponentColumn {
  public abstract void Bind(Array values);
  public abstract void ClearReferences();
}

internal sealed class ComponentColumn<T> : ComponentColumn where T : struct, IComponent {
  public T[] Values { get; private set; } = [];
  public override void Bind(Array values) => this.Values = (T[])values;
  public override void ClearReferences() {
    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(this.Values);
  }
}
