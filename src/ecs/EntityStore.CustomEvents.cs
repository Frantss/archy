namespace Archy;

public sealed partial class EntityStore {
  public void Emit<T>(Entity entity, in T data) where T : struct, ICustomEvent {
    if (!this.TryEmit(entity, data)) throw new MutationException("Custom event origin must be alive in this store.");
  }

  public bool TryEmit<T>(Entity entity, in T data) where T : struct, ICustomEvent {
    this.EnsureEmission();

    if (!this.IsAlive(entity)) return false;

    PendingEvent? pending;

    using (this.Prepare()) {
      var policy = CustomPayload<T>.Instance;
      pending = this._events is {} hub && hub.Has<CustomEvent<T>>(entity)
        ? hub.CaptureValue<CustomEvent<T>, (T Value, CustomPayload<T> Policy)>(entity, (policy.Copy(data), policy),
          static (Entity source, long sequence, in (T Value, CustomPayload<T> Policy) state) => new(source, sequence, state.Policy.Copy(state.Value)))
        : null;
    }

    if (pending is {} captured) this._events!.Enqueue(captured);

    return true;
  }

  public void Emit<T>(in T data) where T : struct, ICustomEvent {
    this.EnsureEmission();
    PendingEvent? pending;

    using (this.Prepare()) {
      var policy = CustomPayload<T>.Instance;
      pending = this._events is {} hub && hub.Has<StoreEvent<T>>(default)
        ? hub.CaptureValue<StoreEvent<T>, (EntityStore Store, T Value, CustomPayload<T> Policy)>(default, (this, policy.Copy(data), policy),
          static (Entity _, long sequence, in (EntityStore Store, T Value, CustomPayload<T> Policy) state) => new(state.Store, sequence, state.Policy.Copy(state.Value)))
        : null;
    }

    if (pending is {} captured) this._events!.Enqueue(captured);
  }

  private void EnsureEmission() {
    this.EnsureAccess();

    if (this._preparing) throw new InvalidOperationException("Preparation callbacks cannot emit events.");
  }
}

public readonly partial record struct Entity {
  public void Emit<T>(in T data) where T : struct, ICustomEvent => this.RequireStore().Emit(this, data);
  public bool TryEmit<T>(in T data) where T : struct, ICustomEvent => this.Store?.TryEmit(this, data) == true;
}
