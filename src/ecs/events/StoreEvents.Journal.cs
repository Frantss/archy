namespace Archy;

public sealed partial class StoreEvents {
  internal int CapturedCount => this._captured.Count;

  private PendingEvent Record<T, TState>(Entity entity, TState state, EventFactory<T, TState>? factory, Subscription<T>[] global, Subscription<T>[] local) where T : struct =>
    this.Record<T, TState, CopiedDelivery>(entity, state, factory, global, local);

  private PendingEvent Record<T, TState, TDelivery>(Entity entity, TState state, EventFactory<T, TState>? factory, Subscription<T>[] global, Subscription<T>[] local)
    where T : struct where TDelivery : struct, IDelivery {
    var slot = PoolSlot<T, TState, TDelivery>.Id;

    if (slot >= this._journals.Length)
      Array.Resize(ref this._journals, Math.Max(slot + 1, this._journals.Length * 2 + 8));

    var journal = (Journal<T, TState, TDelivery>)(this._journals[slot] ??= new Journal<T, TState, TDelivery>(this));
    var position = journal.Capture(entity, state, factory, global, local);
    var item = new PendingEvent(journal, position, this._store.CapturingEvents);

    if (item.Tracked) this._captured.Add(item);

    return item;
  }

  internal void CompleteCapture(int start) {
    for (var i = start; i < this._captured.Count; i++) {
      var item = this._captured[i];

      if (!item.Buffer.IsQueued(item.Position)) item.Release();
    }

    this._captured.RemoveRange(start, this._captured.Count - start);

    if (this._captured.Count == 0 && this._captured.Capacity > 4096) this._captured.TrimExcess();
  }

  private interface IDelivery {
    static abstract bool Immutable { get; }
  }

  private readonly struct CopiedDelivery : IDelivery {
    public static bool Immutable => false;
  }

  private readonly struct ImmutableDelivery : IDelivery {
    public static bool Immutable => true;
  }

  private readonly struct CleanupMarker;

  private sealed class Journal<T, TState, TDelivery> : EventJournalBuffer where T : struct where TDelivery : struct, IDelivery {
    private const int PageBits = 6;
    private const int PageSize = 1 << PageBits;
    private readonly StoreEvents _owner;
    private readonly Arena[] _arenas = [new(), new()];
    private int _captureArena;
    private int _batchArena;
    private bool _batchActive;

    private sealed class Arena {
      public Entry[][] Pages = new Entry[4][];
      public int PageCount;
      public int Next;
      public int Active;

      public void Reset() {
        for (var page = 0; page < this.PageCount && page * PageSize < this.Next; page++)
          Array.Clear(this.Pages[page], 0, Math.Min(PageSize, this.Next - page * PageSize));

        if (this.PageCount > 4096 / PageSize) {
          Array.Resize(ref this.Pages, 4096 / PageSize);
          this.PageCount = this.Pages.Length;
        }

        this.Next = 0;
        this.Active = 0;
      }
    }

    public Journal(StoreEvents owner) => this._owner = owner;
    private ref Entry At(int position) =>
      ref this._arenas[(int)((uint)position >> 31)].Pages[(position & int.MaxValue) >> PageBits][position & (PageSize - 1)];

    public override void BeginBatch() {
      this._batchActive = true;
      this._batchArena = this._captureArena;
      this._captureArena ^= 1;
    }

    public override void EndBatch() {
      if (!this._batchActive) return;

      this._arenas[this._batchArena].Reset();
      this._batchActive = false;
    }

    public int Capture(
      Entity entity,
      TState state,
      EventFactory<T, TState>? factory,
      Subscription<T>[] global,
      Subscription<T>[] local
    ) {
      var arena = this._arenas[this._captureArena];
      var offset = arena.Next++;
      var page = offset >> PageBits;

      if (page == arena.PageCount) {
        if (page == arena.Pages.Length) Array.Resize(ref arena.Pages, arena.Pages.Length * 2);

        arena.Pages[arena.PageCount++] = new Entry[PageSize];
      }

      var position = offset | (this._captureArena << 31);
      this.At(position) = new(entity, state, factory, global, local);
      arena.Active++;

      return position;
    }

    public override bool IsQueued(int position) => this.At(position).Status == 2;
    public override void MarkQueued(int position, long sequence) {
      ref var entry = ref this.At(position);
      entry.Status = 2;
      entry.Sequence = sequence;
    }

    public override void Deliver(int position, List<Exception> errors) {
      ref var entry = ref this.At(position);
      var entity = entry.Id == 0 ? default : new Entity(this._owner._store, entry.Id, entry.Generation);
      var sequence = entry.Sequence;

      if (typeof(T) == typeof(CleanupMarker)) {
        this._owner.Detach(entity);

        return;
      }

      if (TDelivery.Immutable) {
        var value = entry.Factory!(entity, sequence, entry.State);
        DeliverImmutable(entry.Global, value, errors);
        DeliverImmutable(entry.Local, value, errors);
      } else {
        this.Deliver(entry.Global, ref entry, entity, sequence, errors);
        this.Deliver(entry.Local, ref entry, entity, sequence, errors);
      }
    }

    private static void DeliverImmutable(Subscription<T>[] subscribers, T value, List<Exception> errors) {
      foreach (var subscriber in subscribers) {
        if (!subscriber.Active) continue;

        try { subscriber.Invoke(value); }
        catch (Exception error) { errors.Add(error); }
      }
    }

    private void Deliver(Subscription<T>[] subscribers, ref Entry entry, Entity entity, long sequence, List<Exception> errors) {
      foreach (var subscriber in subscribers) {
        if (!subscriber.Active) continue;

        try {
          T value;
          using (this._owner._store.Prepare()) value = entry.Factory!(entity, sequence, entry.State);
          subscriber.Invoke(value);
        } catch (Exception error) { errors.Add(error); }
      }
    }

    public override void Release(int position) {
      ref var entry = ref this.At(position);

      if (entry.Status == 0) return;

      entry = default;
      var arena = this._arenas[(int)((uint)position >> 31)];

      if (--arena.Active == 0) arena.Reset();
    }

    private struct Entry {
      public int Id;
      public uint Generation;
      public long Sequence;
      public TState State;
      public EventFactory<T, TState>? Factory;
      public Subscription<T>[] Global;
      public Subscription<T>[] Local;
      public byte Status;

      public Entry(
        Entity entity,
        TState state,
        EventFactory<T, TState>? factory,
        Subscription<T>[] global,
        Subscription<T>[] local
      ) {
        this.Id = entity.Id;
        this.Generation = entity.Generation;
        this.Sequence = 0;
        this.State = state;
        this.Factory = factory;
        this.Global = global;
        this.Local = local;
        this.Status = 1;
      }
    }
  }
}

internal readonly record struct PendingEvent(EventJournalBuffer Buffer, int Position, bool Tracked) {
  public void Deliver(List<Exception> errors) => this.Buffer.Deliver(this.Position, errors);
  public void Release() => this.Buffer.Release(this.Position);
}

internal struct PreparedEventBatch {
  public PreparedEvents Pair;
  public List<PendingEvent>? Overflow;

  public void Add(PendingEvent item) {
    if (this.Pair.First is null) this.Pair = new(item, null);
    else if (this.Pair.Second is null) this.Pair = new(this.Pair.First, item);
    else (this.Overflow ??= []).Add(item);
  }
}

internal abstract class EventJournalBuffer {
  public abstract bool IsQueued(int position);
  public abstract void MarkQueued(int position, long sequence);
  public abstract void BeginBatch();
  public abstract void EndBatch();
  public abstract void Deliver(int position, List<Exception> errors);
  public abstract void Release(int position);
}

public sealed partial class EntityStore {
  private int _eventCaptureDepth;
  internal bool CapturingEvents => this._eventCaptureDepth != 0;
  private EventCapture CaptureEvents() => new(this);

  private readonly struct EventCapture : IDisposable {
    private readonly EntityStore _store;
    private readonly int _start;

    public EventCapture(EntityStore store) {
      this._store = store;
      this._start = store._events?.CapturedCount ?? 0;
      store._eventCaptureDepth++;
    }

    public void Dispose() {
      this._store._events?.CompleteCapture(this._start);
      this._store._eventCaptureDepth--;
    }
  }
}
