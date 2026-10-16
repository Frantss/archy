using System.Runtime.CompilerServices;

namespace Archy;

public sealed partial class EntityStore : IDisposable {
  private readonly int _threadId = Environment.CurrentManagedThreadId;
  private readonly EntitySlots _slots = new();
  private readonly Stack<int> _free = new();
  private Dictionary<int, CommandBuffer>? _reservations;
  private bool _disposed;
  private int _count;

  public int Count {
    get {
      this.EnsureAccess();

      return this._count;
    }
  }

  public Entity Create() {
    this.EnsureMutation();
    this.EnsureAccess();
    this.EnsureStructural();
    var entity = this.Allocate(null);
    if (this._events is not { HasSubscribers: true }) {
      ref var slot = ref this._slots[entity.Id];
      (slot.Chunk, slot.Row) = this._empty.Append(entity.Id);
      slot.Alive = true;
      this._count++;
    } else this.Activate(entity, null);

    return entity;
  }

  internal Entity Reserve(CommandBuffer owner) {
    this.EnsureRecording();

    return this.Allocate(owner);
  }

  private Entity Allocate(CommandBuffer? owner) {
    var id = this._free.Count > 0 ? this._free.Pop() : this._slots.Count;

    if (id == this._slots.Count) this._slots.Add();

    ref var slot = ref this._slots[id];
    if (owner is not null) (this._reservations ??= new()).Add(id, owner);

    return new(this, id, slot.Generation);
  }

  internal bool IsReserved(Entity entity) => ReferenceEquals(entity.Store, this) && entity.Id > 0
    && entity.Id < this._slots.Count && this._slots[entity.Id].Generation == entity.Generation
    && this._reservations?.ContainsKey(entity.Id) == true;

  internal void ValidateReservation(Entity entity, CommandBuffer owner) {
    if (this.IsReserved(entity) && !ReferenceEquals(this._reservations!.GetValueOrDefault(entity.Id), owner))
      throw new MutationException("Reserved handles belong to their creating buffer.");
  }

  internal void CancelReservation(Entity entity, CommandBuffer owner) {
    if (!this.IsReserved(entity) || !ReferenceEquals(this._reservations!.GetValueOrDefault(entity.Id), owner)) return;

    ref var slot = ref this._slots[entity.Id];
    this._reservations?.Remove(entity.Id);
    this._events?.Detach(entity);

    if (slot.Generation == uint.MaxValue) return;

    slot.Generation++;
    this._free.Push(entity.Id);
  }

  internal bool Activate(Entity entity, CommandBuffer? owner) {
    this.EnsurePlayback();
    var id = entity.Id;

    if (!ReferenceEquals(entity.Store, this) || id <= 0 || id >= this._slots.Count
      || this._slots[id].Generation != entity.Generation || this._slots[id].Alive
      || !ReferenceEquals(this._reservations?.GetValueOrDefault(id), owner)) return false;

    ref var slot = ref this._slots[id];

    PendingEvent? created;

    using (this.Prepare()) created = this._events is { } hub && hub.Has<EntityCreated>(entity) ? hub.Created(entity) : null;

    (slot.Chunk, slot.Row) = this._empty.Append(id);
    slot.Alive = true;
    this._reservations?.Remove(entity.Id);
    this._count++;

    if (created is {} captured) this._events!.Enqueue(captured);

    return true;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool IsAlive(Entity entity) {
    if (!this.HasSlot(entity)) return false;

    ref var slot = ref this._slots[entity.Id];

    return slot.Alive && slot.Generation == entity.Generation;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private bool HasSlot(Entity entity) {
    this.EnsureThread();

    return !this._disposed && ReferenceEquals(entity.Store, this) && entity.Id > 0 && entity.Id < this._slots.Count;
  }

  public void Delete(Entity entity) {
    if (!this.TryDelete(entity)) throw new MutationException("Entity is not alive in this store.");
  }

  public bool TryDelete(Entity entity) {
    this.EnsureMutation();
    if (!this.IsAlive(entity)) return false;

    this.EnsureStructural();
    ref var slot = ref this._slots[entity.Id];

    if (!this._hasReferenceIndexes && this._events is not { HasSubscribers: true }) {
      this.RemoveIndexMembership(entity, slot.Archetype!);
      this.RemoveRow(slot);
      slot.Chunk = null;
      this.Release(entity.Id);

      return true;
    }

    using var capture = this.CaptureEvents();
    PreparedEventBatch events;
    List<RemovalPlan>? cleanup = null;

    using (this.Prepare()) {
      List<(Entity Source, int Type)>? incoming = null;

      foreach (var pair in this._indexes) {
        if (pair.Value.ReferencePolicy is not {} policy) continue;

        foreach (var source in pair.Value.Incoming(entity)) {
          if (source == entity || !this.IsAlive(source)) continue;
          if (policy == TargetDeletion.Restrict) return false;

          (incoming ??= []).Add((source, pair.Key));
        }
      }

      if (incoming is not null) {
        incoming.Sort(static (left, right) => left.Source.Id == right.Source.Id
          ? left.Type.CompareTo(right.Type) : left.Source.Id.CompareTo(right.Source.Id));
        cleanup = new(incoming.Count);
        var previous = default(Entity);
        Archetype? composition = null;

        foreach (var link in incoming) {
          var original = this._slots[link.Source.Id].Archetype!;

          if (previous != link.Source) composition = original;

          var descriptor = original.Components[original.ColumnMap[link.Type]];
          var plan = this.PrepareRemoval(link.Source, descriptor, composition);
          cleanup.Add(plan);
          composition = plan.Target;
          previous = link.Source;
        }
      }

      events = this.DeleteEvents(entity);
    }

    if (cleanup is not null) {
      foreach (var removal in cleanup) this.CommitRemoval(removal);
    }

    this.RemoveIndexMembership(entity, slot.Archetype!);
    this.RemoveRow(slot);
    slot.Chunk = null;
    this.Release(entity.Id);
    this._events?.Enqueue(events);

    return true;
  }

  private void RemoveIndexMembership(Entity entity, Archetype archetype) {
    if (this._indexes.Count == 0) return;

    foreach (var component in archetype.Components) {
      if (this._indexes.TryGetValue(component.Id, out var index)) index.Remove(entity);
    }
  }

  public void Dispose() {
    this.EnsureMutation();
    this.EnsureStructural();
    this._events?.Clear();
    this._disposed = true;
    this._count = 0;
    this._slots.Clear();
    this._free.Clear();
    this._reservations?.Clear();
    this._reservations = null;
    foreach (var archetype in this._archetypes.Values) archetype.Clear();

    this._archetypes.Clear();
    this._storage.Dispose();

    foreach (var index in this._indexes.Values) index.Clear();

    this._indexes.Clear();
    GC.SuppressFinalize(this);
  }

  internal void EnsureAccess() {
    this.EnsureThread();
    ObjectDisposedException.ThrowIf(this._disposed, this);
  }

  private void EnsureThread() {
    if (Environment.CurrentManagedThreadId != this._threadId)
      throw new InvalidOperationException("The entity store must be accessed from its owning thread.");
  }

  private void Release(int id) {
    ref var slot = ref this._slots[id];
    slot.Alive = false;
    this._count--;

    if (slot.Generation == uint.MaxValue) return;

    slot.Generation++;
    this._free.Push(id);
  }

  private struct EntitySlot {
    public uint Generation;
    public bool Alive;
    public readonly Archetype? Archetype => this.Chunk?.Owner;
    public StorageChunk? Chunk;
    public int Row;
  }

  private sealed class EntitySlots {
    private const int PageBits = 8;
    private const int PageSize = 1 << PageBits;
    private EntitySlot[][] _pages = [new EntitySlot[PageSize]];
    private int _pageCount = 1;
    public int Count { get; private set; } = 1;
    public ref EntitySlot this[int index] {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      get => ref this._pages[index >> PageBits][index & (PageSize - 1)];
    }

    public void Add() {
      if ((this.Count >> PageBits) == this._pageCount) {
        if (this._pageCount == this._pages.Length) Array.Resize(ref this._pages, this._pages.Length * 2);

        this._pages[this._pageCount++] = new EntitySlot[PageSize];
      }

      this[this.Count++].Generation = 1;
    }

    public void Clear() { this._pages = []; this._pageCount = 0; this.Count = 0; }
  }
}
