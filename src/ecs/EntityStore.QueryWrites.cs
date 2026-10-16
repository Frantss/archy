using System.Runtime.CompilerServices;

namespace Archy;

public sealed partial class EntityStore {
  private bool _hasBoundAccess;
  private Array? _readValues;
  private Array? _readOtherValues;
  private StorageChunk? _writeChunk;
  private Entity _writeEntity;
  private int _writeRow;
  private int _writeType = -1;
  private Array? _writeValues;
  private QueryLease? _writeLease;

  internal QueryWrites BindWrites(StorageChunk chunk) => new(this, chunk);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal void SelectBoundWrite(StorageChunk chunk, int row, Entity entity) {
    if (!this._hasBoundAccess || !ReferenceEquals(chunk, this._writeChunk)) return;

    this._writeEntity = entity;
    this._writeRow = row;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal void SelectChunkWrite(QueryLease lease, StorageChunk chunk, int row, Entity entity) {
    if (!ReferenceEquals(this._writeLease, lease) || !ReferenceEquals(this._writeChunk, chunk)) {
      this._writeLease = lease;
      this._writeChunk = chunk;
      this._writeType = -1;
      this._writeValues = null;
      this._readValues = null;
      this._readOtherValues = null;
      this._hasBoundAccess = false;
    }

    this._writeEntity = entity;
    this._writeRow = row;
  }

  internal void EndChunkWrites(QueryLease lease) {
    if (ReferenceEquals(this._writeLease, lease)) this.ClearWriteBinding();
  }

  private void ClearWriteBinding() {
    this._writeLease = null;
    this._writeChunk = null;
    this._writeValues = null;
    this._writeEntity = default;
    this._writeRow = 0;
    this._writeType = -1;
    this._readValues = null;
    this._readOtherValues = null;
    this._hasBoundAccess = false;
  }

  private void RememberBoundWrite<T>(Entity entity, ref EntitySlot slot, ComponentDescriptor<T> descriptor, T[] values)
    where T : struct, IComponent {
    if (!ReferenceEquals(slot.Chunk, this._writeChunk)) return;

    this._writeEntity = entity;
    this._writeRow = slot.Row;
    this._writeType = descriptor.Id;
    this._writeValues = values;
    this._hasBoundAccess = true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private bool TryBoundWrite<T>(Entity entity, in T value) where T : struct, IComponent {
    if (this._writeChunk is null || this._writeEntity != entity) return false;

    var descriptor = TypeCatalog.ReadyComponent<T>();

    if (descriptor is not { HasCopyPolicy: false, IndexFactory: null, RequiresReference: false }) return false;

    if (this._writeType != descriptor.Id) {
      var column = this._writeChunk.Owner.ColumnIndex(descriptor.Id);

      if (column < 0) return false;

      this._writeValues = ((ComponentColumn<T>)this._writeChunk.Columns[column]).Values;
      this._writeType = descriptor.Id;
      this._hasBoundAccess = true;
    }

    var values = (T[])this._writeValues!;

    if (this._events is { HasSubscribers: true } hub) WriteObserved(entity, value, values, this._writeRow, hub);
    else values[this._writeRow] = value;

    return true;
  }

  internal readonly struct QueryWrites : IDisposable {
    private readonly EntityStore _store;
    private readonly StorageChunk? _chunk;
    private readonly Entity _entity;
    private readonly int _row;
    private readonly int _type;
    private readonly Array? _values;
    private readonly QueryLease? _lease;
    private readonly Array? _reads;
    private readonly Array? _otherReads;

    public QueryWrites(EntityStore store, StorageChunk chunk) {
      this._store = store;
      this._chunk = store._writeChunk;
      this._entity = store._writeEntity;
      this._row = store._writeRow;
      this._type = store._writeType;
      this._values = store._writeValues;
      this._lease = store._writeLease;
      this._reads = store._readValues;
      this._otherReads = store._readOtherValues;
      store._writeLease = null;
      store._writeChunk = chunk;
      store._writeEntity = default;
      store._writeType = -1;
      store._writeValues = null;
      store._readValues = null;
      store._readOtherValues = null;
      store._hasBoundAccess = false;
    }

    public void Dispose() {
      if (this._lease is { Active: false }) {
        this._store.ClearWriteBinding();

        return;
      }

      this._store._writeLease = this._lease;
      this._store._writeChunk = this._chunk;
      this._store._writeEntity = this._entity;
      this._store._writeRow = this._row;
      this._store._writeType = this._type;
      this._store._writeValues = this._values;
      this._store._readValues = this._reads;
      this._store._readOtherValues = this._otherReads;
      this._store._hasBoundAccess = this._values is not null || this._reads is not null;
    }
  }
}
