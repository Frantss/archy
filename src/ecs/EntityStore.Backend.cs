using Arch.Core;

namespace Archy;

public sealed partial class EntityStore {
  private readonly ArchStorage _storage = new();

  internal void PrepareCreateCapacity(int count) => this.PrepareArchCapacity(this._empty, count);

  private void PrepareArchCapacity(Archetype archetype, int count) =>
    this._storage.PrepareCapacity(archetype.Native, count);

  internal Archetype[] MatchArch(TypeDescriptor[] required, QueryFilter filter) {
    var types = required.Select(type => Component.GetComponentType(type.Type)).ToArray();
    var matches = new List<Archetype>();

    foreach (var archetype in this._archetypes.Values) {
      if (types.All(archetype.Native.Has) && filter.Matches(archetype)) matches.Add(archetype);
    }

    return matches.ToArray();
  }

  internal (StorageChunk Chunk, int Row) AppendArch(Archetype archetype, int id) {
    var slot = this._storage.Create(archetype.Native, new(id, this._slots[id].Generation));
    var chunk = archetype.Bind(slot.ChunkIndex);
    chunk.Count = slot.Index + 1;

    return (chunk, slot.Index);
  }

  private void Move(int id, Archetype target, TypeDescriptor type, bool add) {
    ref var slot = ref this._slots[id];
    var source = slot.Archetype!;
    var last = source.Bind(source.Native.Count);
    var lastRow = last.Count - 1;
    var moved = last.EntityId(lastRow);
    var oldChunk = slot.Chunk!;
    var oldRow = slot.Row;
    var native = oldChunk.NativeEntities[oldRow];
    var destination = this._storage.Move(native, (source.Native, target.Native, type, add));
    last.ClearRow(lastRow);
    last.Count = lastRow;

    if (moved != id) {
      this._slots[moved].Chunk = oldChunk;
      this._slots[moved].Row = oldRow;
    }

    slot.Chunk = target.Bind(destination.ChunkIndex);
    slot.Chunk.Count = destination.Index + 1;
    slot.Row = destination.Index;
  }

  private void RemoveRow(EntitySlot slot) {
    var source = slot.Archetype!;
    var last = source.Bind(source.Native.Count);
    var lastRow = last.Count - 1;
    var moved = last.EntityId(lastRow);
    var removed = slot.Chunk!.EntityId(slot.Row);
    this._storage.Remove(slot.Chunk.NativeEntities[slot.Row], source.Native);
    last.ClearRow(lastRow);
    last.Count = lastRow;

    if (moved == 0 || moved == removed) return;

    this._slots[moved].Chunk = slot.Chunk;
    this._slots[moved].Row = slot.Row;
  }
}
