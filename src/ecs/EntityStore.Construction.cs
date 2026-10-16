namespace Archy;

public sealed partial class EntityStore {
  private HashSet<Entity>? _construction;
  internal bool ValidReference(Entity target) => this.IsAlive(target) || this._construction?.Contains(target) == true;

  internal Entity[] Construct(int count, Func<Entity[], EntityDefinition[]> define) {
    this.EnsurePlayback();
    using var capture = this.CaptureEvents();
    var entities = Enumerable.Range(0, count).Select(_ => this.Allocate(null)).ToArray();
    this._construction = entities.ToHashSet();
    var prepared = new List<(Entity Entity, Archetype Archetype, ComponentPlacement[] Values, List<PendingEvent> Events)>();
    var published = false;

    foreach (var index in this._indexes.Values) index.BeginStaging();

    try {
      using (this.Prepare()) {
        var definitions = define(entities);

        if (definitions.Length != count) throw new InvalidOperationException("Construction node count does not match.");

        for (var i = 0; i < count; i++) {
          var entity = entities[i];
          var definition = definitions[i];
          var values = definition.Components.Values.Select(value => value.Prepare(this, entity)).ToArray();
          var archetype = this._empty;

          foreach (var type in values.Select(value => value.Descriptor).Concat(definition.Tags.Values).OrderBy(type => type.Id))
            archetype = this.Transition(archetype, type, true);

          var events = new List<PendingEvent>();

          if (this._events is { } hub) {
            StoreEvents.Add(events, hub.Capture<EntityCreated>(entity, () => {
              var snapshotValues = new object[values.Length];

              foreach (var value in values) snapshotValues[archetype.ColumnIndex(value.Descriptor.Id)] = value.Snapshot();

              var snapshot = new EntitySnapshot(entity, snapshotValues, archetype.SnapshotShape);

              return sequence => new(entity, sequence, snapshot);
            }));
            var additions = values.Select(value => (value.Descriptor.Id, value.Added))
              .Concat(definition.Tags.Values.Select(type => (type.Id, type.AdditionEvent(hub, entity))));

            foreach (var addition in additions.OrderBy(item => item.Id)) StoreEvents.Add(events, addition.Item2);
          }

          prepared.Add((entity, archetype, values, events));
        }
      }

      if (prepared.Count > 1) {
        var capacities = new Dictionary<Archetype, int>();

        foreach (var node in prepared) capacities[node.Archetype] = capacities.GetValueOrDefault(node.Archetype) + 1;
        foreach (var (archetype, additional) in capacities) this.PrepareStorageCapacity(archetype, additional);
      }

      foreach (var node in prepared) {
        ref var slot = ref this._slots[node.Entity.Id];
        (slot.Chunk, slot.Row) = node.Archetype.Append(node.Entity.Id);

        foreach (var value in node.Values) value.Commit();
      }

      foreach (var entity in entities) this._slots[entity.Id].Alive = true;

      this._count += count;
      published = true;

      foreach (var node in prepared) this._events?.Enqueue(node.Events);

      return entities;
    } finally {
      foreach (var index in this._indexes.Values) index.EndStaging();

      this._construction = null;

      if (!published) {
        foreach (var entity in entities) {
          ref var slot = ref this._slots[entity.Id];

          if (slot.Generation == uint.MaxValue) continue;

          slot.Generation++;
          this._free.Push(entity.Id);
        }
      }
    }
  }

  internal ComponentPlacement PreparePlacement<T>(Entity entity, in T value) where T : struct, IComponent {
    var descriptor = TypeCatalog.Component<T>();
    var owned = descriptor.Copy(value);
    var index = this.GetIndex(descriptor);
    var commitIndex = index?.StageWrite(entity, owned);

    if (index is not null && commitIndex is null) throw new MutationException("Construction index or reference validation failed.");

    var added = this._events?.Capture<ComponentAdded<T>>(entity, () => {
      var snapshot = descriptor.Copy(owned);

      return sequence => new(entity, sequence, descriptor.Copy(snapshot));
    });
    return new(descriptor, () => descriptor.Copy(owned), () => {
      ref var slot = ref this._slots[entity.Id];
      ((ComponentColumn<T>)slot.Chunk!.Columns[slot.Archetype!.ColumnMap[descriptor.Id]]).Values[slot.Row] = owned;
      commitIndex?.Invoke();
    }, added);
  }
}
