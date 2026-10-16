using System.Collections;
using System.Runtime.CompilerServices;

namespace Archy;

public readonly struct ComponentChunk<T> : IReadOnlyList<T> where T : struct, IComponent {
  private readonly QueryLease _lease;
  private readonly int[]? _rows;
  private readonly ComponentDescriptor<T> _descriptor;
  private readonly T[] _values;
  private readonly int _count;

  internal ComponentChunk(QueryBatch batch) {
    this._lease = batch.Lease;
    this._rows = batch.Rows;
    this._count = batch.Count;
    this._descriptor = TypeCatalog.Component<T>();
    this._values = ((ComponentColumn<T>)batch.Chunk.Columns[batch.Archetype.ColumnIndex(this._descriptor.Id)]).Values;
  }
  public int Count { get { this._lease.Check(); return this._count; } }
  public T this[int index] {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get {
      this._lease.Check();
      ArgumentOutOfRangeException.ThrowIfNegative(index);
      ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this._count);
      var row = this._rows?[index] ?? index;

      if (!this._descriptor.HasCopyPolicy) return this._values[row];

      return this.ReadCopy(row);
    }
  }
  private T ReadCopy(int row) {
    using var preparation = this._lease.Store.Prepare();

    return this._descriptor.Copy(this._values[row]);
  }
  public IEnumerator<T> GetEnumerator() {
    for (var i = 0; i < this.Count; i++) yield return this[i];
  }
  IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}

public readonly struct EntityChunk : IReadOnlyList<Entity> {
  private readonly QueryBatch _batch;
  internal EntityChunk(QueryBatch batch) => this._batch = batch;
  public int Count { get { this._batch.Check(); return this._batch.Count; } }
  public Entity this[int index] => this._batch.EntityAt(index);
  public IEnumerator<Entity> GetEnumerator() {
    for (var i = 0; i < this.Count; i++) yield return this[i];
  }
  IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}

internal readonly struct BoundColumn<T> where T : struct, IComponent {
  private readonly EntityStore _store;
  private readonly ComponentDescriptor<T> _descriptor;
  private readonly T[] _values;

  public BoundColumn(EntityStore store, Archetype archetype, StorageChunk chunk) {
    this._store = store;
    this._descriptor = TypeCatalog.Component<T>();
    this._values = ((ComponentColumn<T>)chunk.Columns[archetype.ColumnIndex(this._descriptor.Id)]).Values;
  }

  public T Read(int row) {
    if (!this._descriptor.HasCopyPolicy) return this._values[row];

    using var preparation = this._store.Prepare();

    return this._descriptor.Copy(this._values[row]);
  }
}
