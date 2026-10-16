namespace Archy;

public sealed partial class EntityStore {
  private int _activeQueries;
  internal int ArchetypeVersion { get; private set; }
  internal Entity Handle(int id) => new(this, id, this._slots[id].Generation);
  internal void BeginQuery() => this._activeQueries++;
  internal void ExitQuery() => this._activeQueries--;

  internal bool TryQueryLocation(Entity entity, out QueryLocation location) {
    if (!this.IsAlive(entity)) { location = default; return false; }

    ref var slot = ref this._slots[entity.Id];
    location = new(slot.Archetype!, slot.Chunk!, slot.Row);

    return true;
  }

  private void EnsureStructural() {
    if (this._activeQueries > 0) throw new InvalidOperationException("Structural writes require finished query traversals.");
  }
}

internal readonly record struct QueryLocation(Archetype Archetype, StorageChunk Chunk, int Row);
