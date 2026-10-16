using Archy.Storage;

namespace Archy;

public sealed partial class EntityStore {
  private readonly IEntityStorage _storage;

  internal void PrepareCreateCapacity(int count) => this.PrepareStorageCapacity(this._empty, count);

  private void PrepareStorageCapacity(Archetype archetype, int count) =>
    this._storage.PrepareCapacity(archetype.Storage, count);

  internal Archetype[] MatchStorage(TypeDescriptor[] required, QueryFilter filter) {
    var mask = new TypeMask(required);
    var matches = new List<Archetype>();

    foreach (var archetype in this._archetypes.Values) {
      if (archetype.ComponentMask.HasAll(mask) && filter.Matches(archetype)) matches.Add(archetype);
    }

    return matches.ToArray();
  }

  internal (StorageChunk Chunk, int Row) AppendStorage(Archetype archetype, int id) {
    var slot = this._storage.Create(archetype.Storage, new() { Id = id, Generation = this._slots[id].Generation });
    var chunk = archetype.Bind(slot.ChunkIndex);
    chunk.Count = slot.Row + 1;

    return (chunk, slot.Row);
  }

  private void Move(int id, Archetype target, TypeDescriptor type, bool add) {
    ref var slot = ref this._slots[id];
    var source = slot.Archetype!;
    var last = source.Bind(source.Storage.ChunkCount - 1);
    var lastRow = last.Count - 1;
    var moved = last.EntityId(lastRow);
    var oldChunk = slot.Chunk!;
    var oldRow = slot.Row;
    var location = new StorageLocation { Archetype = source.Storage, ChunkIndex = oldChunk.Index, Row = oldRow };
    var destination = this._storage.Move(location, (target.Storage, type.Type, add));
    last.Count = lastRow;

    if (moved != id) {
      this._slots[moved].Chunk = oldChunk;
      this._slots[moved].Row = oldRow;
    }

    slot.Chunk = target.Bind(destination.ChunkIndex);
    slot.Chunk.Count = destination.Row + 1;
    slot.Row = destination.Row;
  }

  private void RemoveRow(EntitySlot slot) {
    var source = slot.Archetype!;
    var last = source.Bind(source.Storage.ChunkCount - 1);
    var lastRow = last.Count - 1;
    var moved = last.EntityId(lastRow);
    var removed = slot.Chunk!.EntityId(slot.Row);
    this._storage.Remove(new() { Archetype = source.Storage, ChunkIndex = slot.Chunk.Index, Row = slot.Row });
    last.Count = lastRow;

    if (moved == 0 || moved == removed) return;

    this._slots[moved].Chunk = slot.Chunk;
    this._slots[moved].Row = slot.Row;
  }
}
