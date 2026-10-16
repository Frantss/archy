namespace Archy;

public sealed partial class StoreEvents {
  internal bool ObservesWrite<T>(Entity entity, bool exists) where T : struct, IComponent =>
    this.Has<EntityModified>(entity) || (exists ? this.Has<ComponentUpdated<T>>(entity) : this.Has<ComponentAdded<T>>(entity));

  internal PendingEvent? Created(Entity entity) => this.CaptureImmutable<EntityCreated, EntitySnapshot>(entity,
    this._store.EmptySnapshot(entity), static (Entity source, long sequence, in EntitySnapshot snapshot) => new(source, sequence, snapshot));

  internal PreparedEvents ImmutableUpdate<T>(Entity entity, in T previous, in T value) where T : struct, IComponent {
    var route = this.UpdateRouteFor<T>(entity);
    PendingEvent? specific = null;
    PendingEvent? modified = null;

    if (route.Global.Length != 0 || route.Local.Length != 0)
      specific = this.Record<ComponentUpdated<T>, (T Old, T Next), ImmutableDelivery>(
        entity, (previous, value),
        static (Entity source, long sequence, in (T Old, T Next) state) => new(source, sequence, state.Old, state.Next),
        route.Global, route.Local
      );

    if (route.ModifiedGlobal.Length != 0 || route.ModifiedLocal.Length != 0)
      modified = this.Record<EntityModified, (Type Type, ChangeOperation Operation), ImmutableDelivery>(
        entity, (typeof(T), ChangeOperation.Updated),
        static (Entity source, long sequence, in (Type Type, ChangeOperation Operation) state) =>
          new(source, sequence, state.Type, state.Operation),
        route.ModifiedGlobal, route.ModifiedLocal
      );

    return new(specific, modified);
  }

  private UpdateRoute<T> UpdateRouteFor<T>(Entity entity) where T : struct, IComponent {
    var slot = ChannelSlot<ComponentUpdated<T>>.Id;

    if (slot >= this._updateRoutes.Length) Array.Resize(ref this._updateRoutes, Math.Max(slot + 1, this._updateRoutes.Length * 2 + 8));

    var route = (UpdateRoute<T>)(this._updateRoutes[slot] ??= new UpdateRoute<T>());

    if (route.Version == this._subscriptionVersion && (!route.HasLocals || route.Entity == entity)) return route;

    var specific = this.FindChannel<ComponentUpdated<T>>();
    var modified = this.FindChannel<EntityModified>();
    route.Global = specific?.Global ?? [];
    route.Local = specific?.Local(entity) ?? [];
    route.ModifiedGlobal = modified?.Global ?? [];
    route.ModifiedLocal = modified?.Local(entity) ?? [];
    route.HasLocals = specific?.HasLocals == true || modified?.HasLocals == true;
    route.Entity = entity;
    route.Version = this._subscriptionVersion;

    return route;
  }

  private sealed class UpdateRoute<T> : IUpdateRoute where T : struct, IComponent {
    public long Version = -1;
    public Entity Entity;
    public bool HasLocals;
    public Subscription<ComponentUpdated<T>>[] Global = [];
    public Subscription<ComponentUpdated<T>>[] Local = [];
    public Subscription<EntityModified>[] ModifiedGlobal = [];
    public Subscription<EntityModified>[] ModifiedLocal = [];

    public void Invalidate() {
      this.Version = -1;
      this.Entity = default;
      this.Global = [];
      this.Local = [];
      this.ModifiedGlobal = [];
      this.ModifiedLocal = [];
    }
  }

  internal PreparedEvents Write<T>(Entity entity, bool exists, T value, ComponentDescriptor<T> descriptor) where T : struct, IComponent {
    if (!descriptor.HasCopyPolicy) {
      if (exists) return this.Has<ComponentUpdated<T>>(entity)
        ? this.ImmutableUpdate(entity, this._store.Get<T>(entity), value)
        : new(null, this.ModifiedEvent(entity, typeof(T), ChangeOperation.Updated));

      var immutable = this.CaptureImmutable<ComponentAdded<T>, T>(entity, value,
        static (Entity source, long sequence, in T data) => new(source, sequence, data));

      return new(immutable, this.ModifiedEvent(entity, typeof(T), exists ? ChangeOperation.Updated : ChangeOperation.Added));
    }

    PendingEvent? specific = null;

    if (exists && this.Has<ComponentUpdated<T>>(entity)) {
      var state = (Old: this._store.Get<T>(entity), Next: descriptor.Copy(value), Descriptor: descriptor);
      specific = this.CaptureValue<ComponentUpdated<T>, (T Old, T Next, ComponentDescriptor<T> Descriptor)>(entity, state,
        static (Entity source, long sequence, in (T Old, T Next, ComponentDescriptor<T> Descriptor) data) =>
          new(source, sequence, data.Descriptor.Copy(data.Old), data.Descriptor.Copy(data.Next)));
    } else if (!exists && this.Has<ComponentAdded<T>>(entity)) {
      var state = (Value: descriptor.Copy(value), Descriptor: descriptor);
      specific = this.CaptureValue<ComponentAdded<T>, (T Value, ComponentDescriptor<T> Descriptor)>(entity, state,
        static (Entity source, long sequence, in (T Value, ComponentDescriptor<T> Descriptor) data) =>
          new(source, sequence, data.Descriptor.Copy(data.Value)));
    }

    return new(specific, this.ModifiedEvent(entity, typeof(T), exists ? ChangeOperation.Updated : ChangeOperation.Added));
  }

  internal PendingEvent? Removed<T>(Entity entity) where T : struct, IComponent {
    if (!this.Has<ComponentRemoved<T>>(entity)) return null;

    var state = (Value: this._store.Get<T>(entity), Descriptor: TypeCatalog.Component<T>());

    if (!state.Descriptor.HasCopyPolicy)
      return this.CaptureImmutable<ComponentRemoved<T>, T>(entity, state.Value,
        static (Entity source, long sequence, in T data) => new(source, sequence, data));

    return this.CaptureValue<ComponentRemoved<T>, (T Value, ComponentDescriptor<T> Descriptor)>(entity, state,
      static (Entity source, long sequence, in (T Value, ComponentDescriptor<T> Descriptor) data) =>
        new(source, sequence, data.Descriptor.Copy(data.Value)));
  }

  internal PreparedEvents Tag<T>(Entity entity, bool add) where T : struct, ITag =>
    new(add ? this.AddedTag<T>(entity) : this.RemovedTag<T>(entity),
      this.ModifiedEvent(entity, typeof(T), add ? ChangeOperation.Added : ChangeOperation.Removed));

  internal PendingEvent? AddedTag<T>(Entity entity) where T : struct, ITag =>
    this.CaptureImmutable<TagAdded<T>, bool>(entity, false, static (Entity source, long sequence, in bool _) => new(source, sequence));

  internal PendingEvent? RemovedTag<T>(Entity entity) where T : struct, ITag =>
    this.CaptureImmutable<TagRemoved<T>, bool>(entity, false, static (Entity source, long sequence, in bool _) => new(source, sequence));

  internal void Modified(List<PendingEvent> events, Entity entity, Type type, ChangeOperation operation) =>
    Add(events, this.ModifiedEvent(entity, type, operation));

  internal PendingEvent? ModifiedEvent(Entity entity, Type type, ChangeOperation operation) =>
    this.CaptureImmutable<EntityModified, (Type Type, ChangeOperation Operation)>(entity, (type, operation),
      static (Entity source, long sequence, in (Type Type, ChangeOperation Operation) data) => new(source, sequence, data.Type, data.Operation));

  internal static void Add(List<PendingEvent> events, PendingEvent? item) {
    if (item is {} captured) events.Add(captured);
  }
}
