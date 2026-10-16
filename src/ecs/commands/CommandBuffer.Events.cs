namespace Archy;

public sealed partial class CommandBuffer {
  public void Emit<T>(Entity entity, in T data) where T : struct, ICustomEvent => this.Event(entity, data, false, false);
  public CommandTicket TryEmit<T>(Entity entity, in T data) where T : struct, ICustomEvent => this.Event(entity, data, true, false);
  public void Emit<T>(in T data) where T : struct, ICustomEvent => this.Event(default, data, false, true);

  private readonly Dictionary<Type, IPayloads> _eventPayloads = new();

  private CommandTicket Event<T>(Entity entity, in T data, bool attempt, bool storeOnly) where T : struct, ICustomEvent {
    this.EnsureRecording();
    this._store.ValidateReservation(entity, this);

    if (!this._eventPayloads.TryGetValue(typeof(T), out var entry)) {
      entry = new EventPayloads<T>();
      this._eventPayloads.Add(typeof(T), entry);
    }

    var payloads = (EventPayloads<T>)entry;
    T copy;
    using (this._store.Prepare()) copy = CustomPayload<T>.Instance.Copy(data);
    var position = payloads.Values.Count;
    payloads.Values.Add(copy);

    return this.Record(entity, storeOnly ? CommandKind.StoreEmit : CommandKind.EntityEmit, payloads, position, attempt);
  }

  private sealed class EventPayloads<T> : IPayloads where T : struct, ICustomEvent {
    public List<T> Values { get; } = [];
    public bool Execute(EntityStore store, Entity entity, CommandKind kind, int index) {
      if (kind == CommandKind.EntityEmit) return store.TryEmit(entity, this.Values[index]);

      store.Emit(this.Values[index]);

      return true;
    }
  }
}
