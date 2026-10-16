namespace Archy;

public sealed partial class StoreEvents {
  private readonly EntityStore _store;
  private static int s_channelCount;
  private static int s_poolCount;
  private IChannel?[] _channels = [];
  private EventJournalBuffer?[] _journals = [];
  private readonly List<PendingEvent> _captured = [];
  private int _entitySubscriptionCount;
  private List<PendingEvent> _pending = [];
  private List<PendingEvent> _batch = [];
  private long _sequence;
  private int _subscriptionCount;
  private long _subscriptionVersion;
  private object?[] _updateRoutes = [];
  internal bool HasSubscribers => this._subscriptionCount != 0;

  internal bool Has<T>(Entity entity) where T : struct => this.HasSubscribers && this.FindChannel<T>() is {} channel
    && channel.Has(entity);

  private Channel<T>? FindChannel<T>() where T : struct {
    var slot = ChannelSlot<T>.Id;

    return slot < this._channels.Length ? (Channel<T>?)this._channels[slot] : null;
  }

  private Channel<T> GetChannel<T>() where T : struct {
    var slot = ChannelSlot<T>.Id;

    if (slot >= this._channels.Length) Array.Resize(ref this._channels, Math.Max(slot + 1, this._channels.Length * 2 + 8));

    return (Channel<T>)(this._channels[slot] ??= new Channel<T>());
  }

  internal StoreEvents(EntityStore store) => this._store = store;
  public IDisposable Subscribe<T>(Action<T> handler) where T : struct => this.Subscribe(default, handler);

  internal IDisposable Subscribe<T>(Entity entity, Action<T> handler) where T : struct {
    this._store.EnsureAccess();
    ArgumentNullException.ThrowIfNull(handler);

    if (entity != default) {
      _ = this._store.EntityEvents(entity);

      if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(StoreEvent<>))
        throw new InvalidOperationException("Store-only events have no entity subscription channel.");
    }

    var channel = this.GetChannel<T>();
    var subscription = new Subscription<T>(this, channel, entity, handler);
    channel.Add(entity, subscription);

    if (entity != default) this._entitySubscriptionCount++;

    this._subscriptionCount++;
    this._subscriptionVersion++;
    this.InvalidateRoutes();

    return subscription;
  }

  public void Dispatch() {
    this._store.BeginDispatch();
    if (this._pending.Count == 0) {
      this._store.EndDispatch();

      return;
    }

    (this._pending, this._batch) = (this._batch, this._pending);
    foreach (var journal in this._journals) journal?.BeginBatch();

    var errors = new List<Exception>();

    try {
      foreach (var item in this._batch) item.Deliver(errors);
    } finally {
      foreach (var journal in this._journals) journal?.EndBatch();

      this._batch.Clear();
      this._store.EndDispatch();
    }

    if (errors.Count > 0) throw new AggregateException("Event handlers failed after committed mutations.", errors);
  }

  internal PendingEvent? Capture<T>(Entity entity, Func<Func<long, T>> capture) where T : struct {
    var channel = this.HasSubscribers ? this.FindChannel<T>() : null;

    if (channel is null || !channel.Has(entity)) return null;

    var global = channel.Global;
    var local = channel.Local(entity);
    var value = capture();

    return this.Record<T, Func<long, T>>(
      entity, value, static (Entity _, long sequence, in Func<long, T> factory) => factory(sequence), global, local
    );
  }

  internal PendingEvent? CaptureValue<T, TState>(Entity entity, TState state, EventFactory<T, TState> factory) where T : struct {
    return this.CaptureValue<T, TState, CopiedDelivery>(entity, state, factory);
  }

  internal PendingEvent? CaptureImmutable<T, TState>(Entity entity, TState state, EventFactory<T, TState> factory) where T : struct {
    return this.CaptureValue<T, TState, ImmutableDelivery>(entity, state, factory);
  }

  private PendingEvent? CaptureValue<T, TState, TDelivery>(Entity entity, TState state, EventFactory<T, TState> factory) where T : struct where TDelivery : struct, IDelivery {
    var channel = this.HasSubscribers ? this.FindChannel<T>() : null;

    if (channel is null) return null;

    var global = channel.Global;
    var local = channel.Local(entity);

    if (global.Length == 0 && local.Length == 0) return null;

    return this.Record<T, TState, TDelivery>(entity, state, factory, global, local);
  }

  internal void Enqueue(PendingEvent item) {
    item.Buffer.MarkQueued(item.Position, ++this._sequence);
    this._pending.Add(item);
  }

  internal void Enqueue(PreparedEvents events) {
    if (events.First is {} first) this.Enqueue(first);
    if (events.Second is {} second) this.Enqueue(second);
  }

  internal void Enqueue(List<PendingEvent> events) {
    for (var i = 0; i < events.Count; i++) this.Enqueue(events[i]);
  }

  internal void Enqueue(PreparedEventBatch events) {
    this.Enqueue(events.Pair);

    if (events.Overflow is {} overflow) this.Enqueue(overflow);
  }

  internal void Clear() {
    foreach (var channel in this._channels) channel?.Clear();

    this._channels = [];
    this._updateRoutes = [];
    this._journals = [];
    this._captured.Clear();
    this._pending.Clear();
    this._batch.Clear();
  }

  internal PendingEvent? Cleanup(Entity entity) {
    if (this._entitySubscriptionCount == 0) return null;

    foreach (var channel in this._channels) {
      if (channel?.HasLocal(entity) == true)
        return this.Record<CleanupMarker, bool>(entity, false, null, [], []);
    }

    return null;
  }

  internal void Detach(Entity entity) {
    if (this._entitySubscriptionCount == 0) return;

    foreach (var channel in this._channels) channel?.Detach(entity);
  }

  private void InvalidateRoutes() {
    foreach (var route in this._updateRoutes) (route as IUpdateRoute)?.Invalidate();
  }

  private interface IUpdateRoute {
    void Invalidate();
  }

  private static class ChannelSlot<T> where T : struct {
    public static readonly int Id = Interlocked.Increment(ref s_channelCount) - 1;
  }

  private static class PoolSlot<T, TState, TDelivery> where T : struct {
    public static readonly int Id = Interlocked.Increment(ref s_poolCount) - 1;
  }

  private interface IChannel {
    bool HasLocal(Entity entity);
    void Detach(Entity entity);
    void Clear();
  }

  private sealed class Channel<T> : IChannel where T : struct {
    private readonly Dictionary<Entity, Subscription<T>[]> _local = new();
    public Subscription<T>[] Global { get; private set; } = [];

    private Entity _cachedEntity;
    private Subscription<T>[] _cachedLocal = [];
    public bool HasLocals => this._local.Count != 0;
    public bool Has(Entity entity) => this.Global.Length != 0 || this.Local(entity).Length != 0;
    public bool HasLocal(Entity entity) => entity != default && this._local.ContainsKey(entity);
    public Subscription<T>[] Local(Entity entity) {
      if (entity == default || this._local.Count == 0) return [];
      if (this._cachedEntity == entity) return this._cachedLocal;

      this._cachedEntity = entity;
      this._cachedLocal = this._local.GetValueOrDefault(entity) ?? [];

      return this._cachedLocal;
    }

    private void Invalidate() { this._cachedEntity = default; this._cachedLocal = []; }

    public void Add(Entity entity, Subscription<T> subscription) {
      if (entity == default) this.Global = [..this.Global, subscription];
      else this._local[entity] = [..this.Local(entity), subscription];

      this.Invalidate();
    }

    public void Remove(Entity entity, Subscription<T> subscription) {
      var current = entity == default ? this.Global : this.Local(entity);
      var remaining = current.Where(item => !ReferenceEquals(item, subscription)).ToArray();

      if (entity == default) this.Global = remaining;
      else if (remaining.Length == 0) this._local.Remove(entity);
      else this._local[entity] = remaining;

      this.Invalidate();
    }

    public void Detach(Entity entity) {
      if (!this._local.Remove(entity, out var subscriptions)) return;

      this.Invalidate();

      foreach (var subscription in subscriptions) subscription.Detach();
    }

    public void Clear() {
      foreach (var subscription in this.Global) subscription.Detach();

      foreach (var subscriptions in this._local.Values) {
        this.Invalidate();

        foreach (var subscription in subscriptions) subscription.Detach();
      }

      this.Global = [];
      this._local.Clear();
      this.Invalidate();
    }
  }

  private sealed class Subscription<T> : IDisposable where T : struct {
    private readonly StoreEvents _owner;
    private readonly Channel<T> _channel;
    private readonly Entity _entity;
    private readonly Action<T> _handler;
    public bool Active { get; private set; } = true;

    public Subscription(StoreEvents owner, Channel<T> channel, Entity entity, Action<T> handler) {
      this._owner = owner;
      this._channel = channel;
      this._entity = entity;
      this._handler = handler;
    }

    public void Invoke(T value) => this._handler(value);
    public void Detach() {
      if (!this.Active) return;

      this.Active = false;
      this._owner._subscriptionCount--;
      this._owner._subscriptionVersion++;
      this._owner.InvalidateRoutes();

      if (this._entity != default) this._owner._entitySubscriptionCount--;
    }
    public void Dispose() {
      if (!this.Active) return;

      this._owner._store.EnsureAccess();
      this.Detach();
      this._channel.Remove(this._entity, this);
    }
  }

  internal delegate T EventFactory<T, TState>(Entity entity, long sequence, in TState state) where T : struct;
}

public sealed class EntityEvents {
  private readonly Entity _entity;
  internal EntityEvents(Entity entity) => this._entity = entity;
  public IDisposable Subscribe<T>(Action<T> handler) where T : struct => this._entity.RequireStore().Events.Subscribe(this._entity, handler);
}

internal readonly record struct PreparedEvents(PendingEvent? First, PendingEvent? Second);
