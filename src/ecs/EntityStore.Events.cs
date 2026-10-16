namespace Archy;

public sealed partial class EntityStore {
  private StoreEvents? _events;
  private bool _dispatching;
  public StoreEvents Events { get { this.EnsureAccess(); return this._events ??= new(this); } }

  internal void BeginDispatch() {
    this.EnsureAccess();
    this.EnsureMutation();
    this.EnsureStructural();
    this._dispatching = true;
  }

  internal void EndDispatch() => this._dispatching = false;
  internal EntityEvents EntityEvents(Entity entity) {
    this.EnsureAccess();

    if (!this.IsAlive(entity) && !this.IsReserved(entity)) throw new MutationException("Cannot subscribe to an absent entity.");

    return new(entity);
  }

  internal EntitySnapshot EmptySnapshot(Entity entity) => new(entity, [], this._empty.SnapshotShape);

  private EntitySnapshot Snapshot(Entity entity) {
    ref var slot = ref this._slots[entity.Id];

    var archetype = slot.Archetype!;
    var values = new object[archetype.Components.Length];

    for (var i = 0; i < values.Length; i++) values[i] = archetype.Components[i].SnapshotValue(this, entity);

    return new(entity, values, archetype.SnapshotShape);
  }

  private PreparedEventBatch DeleteEvents(Entity entity) {
    if (this._events is not { HasSubscribers: true } hub) return default;

    var events = new PreparedEventBatch();
    var archetype = this._slots[entity.Id].Archetype!;
    var component = 0;
    var tag = 0;

    while (component < archetype.Components.Length || tag < archetype.Tags.Length) {
      var type = tag == archetype.Tags.Length || component < archetype.Components.Length
        && archetype.Components[component].Id < archetype.Tags[tag].Id
        ? archetype.Components[component++] : archetype.Tags[tag++];

      if (type.RemovalEvent(hub, entity) is {} removal) events.Add(removal);
    }

    if (hub.Has<EntityDeleted>(entity)) {
      var deleted = hub.CaptureImmutable<EntityDeleted, EntitySnapshot>(entity, this.Snapshot(entity),
        static (Entity source, long sequence, in EntitySnapshot snapshot) => new(source, sequence, snapshot));

      if (deleted is {} captured) events.Add(captured);
    }

    if (hub.Cleanup(entity) is {} cleanup) events.Add(cleanup);

    return events;
  }
}

public readonly partial record struct Entity {
  public EntityEvents Events => this.RequireStore().EntityEvents(this);
}
