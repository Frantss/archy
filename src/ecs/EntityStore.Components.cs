using System.Runtime.CompilerServices;
using Archy.Storage;

namespace Archy;

public sealed partial class EntityStore {
  private readonly Dictionary<string, Archetype> _archetypes = new(StringComparer.Ordinal);
  private readonly Archetype _empty = new([], []);
  private bool _preparing;

  public EntityStore(IEntityStorage storage) {
    ArgumentNullException.ThrowIfNull(storage);

    this._storage = storage;
    this._empty.Initialize(this, this._storage);
    this._archetypes.Add("|", this._empty);
  }

  internal bool Has<T>(Entity entity) where T : struct, IComponent {
    if (!this.IsAlive(entity)) return false;

    using var preparation = this.Prepare();

    return this._slots[entity.Id].Archetype!.ComponentMask.Has(TypeCatalog.Component<T>().Id);
  }

  internal bool HasTag<T>(Entity entity) where T : struct, ITag =>
    this.IsAlive(entity) && this._slots[entity.Id].Archetype!.TagMask.Has(TypeCatalog.Tag<T>().Id);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal T Get<T>(Entity entity) where T : struct, IComponent {
    if (!this.TryGet<T>(entity, out var value)) throw new MutationException("Entity or component is absent.");

    return value;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal bool TryGet<T>(Entity entity, out T value) where T : struct, IComponent {
    value = default;
    var binding = this._writeChunk;
    if (binding is not null && this.TryCachedRead(entity, out value)) return true;

    if (!this.HasSlot(entity)) return false;

    ref var slot = ref this._slots[entity.Id];

    if (!slot.Alive || slot.Generation != entity.Generation) return false;

    var descriptor = TypeCatalog.ReadyComponent<T>();

    if (descriptor is null || descriptor.HasCopyPolicy) return this.TryGetCopy(entity, out value);

    var column = slot.Archetype!.ColumnIndex(descriptor.Id);

    if (column < 0) return false;

    var values = ((ComponentColumn<T>)slot.Chunk!.Columns[column]).Values;
    value = values[slot.Row];
    if (binding is not null) this.RememberRead(entity, ref slot, values);

    return true;
  }

  private bool TryGetCopy<T>(Entity entity, out T value) where T : struct, IComponent {
    using var preparation = this.Prepare();
    var descriptor = TypeCatalog.Component<T>();
    ref var slot = ref this._slots[entity.Id];
    var column = slot.Archetype!.ColumnIndex(descriptor.Id);
    value = default;

    if (column < 0) return false;

    value = descriptor.Copy(((ComponentColumn<T>)slot.Chunk!.Columns[column]).Values[slot.Row]);

    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal bool TryWrite<T>(Entity entity, in T value, WriteMode mode) where T : struct, IComponent {
    this.EnsureMutation();

    if (mode != WriteMode.Add && this.TryBoundWrite(entity, value)) return true;

    if (!this.HasSlot(entity)) return false;

    ref var slot = ref this._slots[entity.Id];

    if (!slot.Alive || slot.Generation != entity.Generation) return false;

    var descriptor = TypeCatalog.ReadyComponent<T>();

    if (descriptor is { HasCopyPolicy: false, IndexFactory: null, RequiresReference: false }) {
      var column = slot.Archetype!.ColumnIndex(descriptor.Id);

      if (column >= 0) {
        if (mode == WriteMode.Add) return false;

        var values = ((ComponentColumn<T>)slot.Chunk!.Columns[column]).Values;

        if (this._events is { HasSubscribers: true } hub) WriteObserved(entity, value, values, slot.Row, hub);
        else values[slot.Row] = value;
        if (this._writeChunk is not null) this.RememberBoundWrite(entity, ref slot, descriptor, values);

        return true;
      }

      if (mode == WriteMode.Update) return false;
      if (this._events is not { HasSubscribers: true }) {
        this.EnsureStructural();
        this.Move(entity.Id, this.Transition(slot.Archetype!, descriptor, true), descriptor, true);
        column = slot.Archetype!.ColumnIndex(descriptor.Id);
        ((ComponentColumn<T>)slot.Chunk!.Columns[column]).Values[slot.Row] = value;

        return true;
      }
    }

    return this.TryWritePrepared(entity, value, mode, ref slot);
  }

  private static void WriteObserved<T>(Entity entity, in T value, T[] values, int row, StoreEvents events) where T : struct, IComponent {
    var prepared = events.ImmutableUpdate(entity, values[row], value);
    values[row] = value;
    events.Enqueue(prepared);
  }

  private bool TryWritePrepared<T>(Entity entity, in T value, WriteMode mode, ref EntitySlot slot) where T : struct, IComponent {
    ComponentDescriptor<T> descriptor;
    bool exists;
    int column;
    T copy;
    IndexState<T>? index;
    PreparedEvents events;

    using (this.Prepare()) {
      descriptor = TypeCatalog.Component<T>();
      column = slot.Archetype!.ColumnIndex(descriptor.Id);
      exists = column >= 0;

      if (mode == WriteMode.Add && exists || mode == WriteMode.Update && !exists) return false;

      if (!exists) this.EnsureStructural();

      copy = descriptor.Copy(value);
      index = this.GetIndex(descriptor);

      if (index is not null && !index.PrepareWrite(entity, copy)) return false;

      events = this._events is { } hub && hub.ObservesWrite<T>(entity, exists)
        ? hub.Write(entity, exists, copy, descriptor) : default;
    }

    if (!exists) {
      this.Move(entity.Id, this.Transition(slot.Archetype!, descriptor, true), descriptor, true);
      column = slot.Archetype!.ColumnIndex(descriptor.Id);
    }

    ((ComponentColumn<T>)slot.Chunk!.Columns[column]).Values[slot.Row] = copy;
    index?.CommitWrite(entity);

    this._events?.Enqueue(events);

    return true;
  }

  internal bool TryRemove<T>(Entity entity) where T : struct, IComponent {
    this.EnsureMutation();

    if (!this.Has<T>(entity)) return false;

    this.EnsureStructural();
    RemovalPlan plan;
    using (this.Prepare()) plan = this.PrepareRemoval(entity, TypeCatalog.Component<T>());
    this.CommitRemoval(plan);

    return true;
  }

  internal bool TryTag<T>(Entity entity, bool add) where T : struct, ITag {
    this.EnsureMutation();

    if (!this.IsAlive(entity)) return false;

    var descriptor = TypeCatalog.Tag<T>();
    ref var slot = ref this._slots[entity.Id];

    if (slot.Archetype!.TagMask.Has(descriptor.Id) == add) return false;

    this.EnsureStructural();
    PreparedEvents events;
    using (this.Prepare()) events = this._events?.Tag<T>(entity, add) ?? default;
    this.Move(entity.Id, this.Transition(slot.Archetype, descriptor, add), descriptor, add);

    this._events?.Enqueue(events);

    return true;
  }

  private RemovalPlan PrepareRemoval(Entity entity, TypeDescriptor descriptor, Archetype? composition = null) {
    var events = this._events is {} hub
      ? new PreparedEvents(descriptor.RemovalEvent(hub, entity), hub.ModifiedEvent(entity, descriptor.Type, ChangeOperation.Removed))
      : default;

    return new(entity, descriptor, this.Transition(composition ?? this._slots[entity.Id].Archetype!, descriptor, false), events);
  }

  private void CommitRemoval(RemovalPlan plan) {
    this.Move(plan.Entity.Id, plan.Target, plan.Type, false);

    if (this._indexes.TryGetValue(plan.Type.Id, out var index)) index.Remove(plan.Entity);

    this._events?.Enqueue(plan.Events);
  }

  private readonly record struct RemovalPlan(Entity Entity, TypeDescriptor Type, Archetype Target, PreparedEvents Events);

  private Archetype Transition(Archetype source, TypeDescriptor type, bool add) {
    if (source.Transitions.TryGetValue((type.Id, add), out var cached)) return cached;

    return this.CreateTransition(source, type, add);
  }

  private Archetype CreateTransition(Archetype source, TypeDescriptor type, bool add) {
    var types = type.IsTag ? source.Tags : source.Components;
    var next = add ? types.Append(type).OrderBy(item => item.Id).ToArray() : types.Where(item => item.Id != type.Id).ToArray();
    var components = type.IsTag ? source.Components : next;
    var tags = type.IsTag ? next : source.Tags;
    var key = $"{string.Join(',', components.Select(item => item.Id))}|{string.Join(',', tags.Select(item => item.Id))}";

    if (!this._archetypes.TryGetValue(key, out var target)) {
      target = new(components, tags);
      target.Initialize(this, this._storage);
      this._archetypes.Add(key, target);
      this.ArchetypeVersion++;
    }

    source.Transitions.Add((type.Id, add), target);

    return target;
  }

  private void EnsureMutation() {
    this.EnsureThread();

    if (this._dispatching) throw new InvalidOperationException("Event handlers must defer mutations through commands.");
    if (this._preparing) throw new InvalidOperationException("Copy policies cannot reenter store mutations.");
  }

  internal Preparation Prepare() {
    var previous = this._preparing;
    this._preparing = true;

    return new(this, previous);
  }

  internal readonly struct Preparation : IDisposable {
    private readonly EntityStore _store;
    private readonly bool _previous;

    public Preparation(EntityStore store, bool previous) {
      this._store = store;
      this._previous = previous;
    }

    public void Dispose() => this._store._preparing = this._previous;
  }
}
