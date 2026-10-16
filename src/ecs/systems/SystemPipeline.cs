namespace Archy;

public interface ISystem {
  void Update(SystemContext context);
}

public readonly record struct Tick(long Number, double Delta);

public sealed class SystemContext {
  private readonly Action _checkpoint;
  public EntityStore Store { get; }
  public Tick Tick { get; }
  public CommandBuffer Commands { get; }

  internal SystemContext(EntityStore store, Tick tick, CommandBuffer commands, Action checkpoint) {
    this.Store = store;
    this.Tick = tick;
    this.Commands = commands;
    this._checkpoint = checkpoint;
  }

  public void Checkpoint() => this._checkpoint();
}

public sealed class SystemPipeline : IDisposable {
  private readonly EntityStore _store;
  private readonly CommandBuffer _commands;
  private readonly List<ISystem> _systems = [];
  private bool _updating;
  private bool _checkpointing;
  private bool _disposed;

  public SystemPipeline(EntityStore store) {
    ArgumentNullException.ThrowIfNull(store);
    this._store = store;
    this._commands = store.CreateCommandBuffer();
  }

  public SystemPipeline Add(ISystem system) {
    this.EnsureIdle();
    ArgumentNullException.ThrowIfNull(system);
    this._systems.Add(system);

    return this;
  }

  public void Update(Tick tick) {
    this.EnsureIdle();
    this._updating = true;
    var context = new SystemContext(this._store, tick, this._commands, this.Checkpoint);

    try {
      this.Checkpoint();

      foreach (var system in this._systems) {
        system.Update(context);
        this.Checkpoint();
      }
    } finally { this._updating = false; }
  }

  public void Dispose() {
    if (this._disposed) return;

    this.EnsureIdle();
    this._disposed = true;
    var errors = new List<Exception>();

    foreach (var system in this._systems.AsEnumerable().Reverse().OfType<IDisposable>()) {
      try { system.Dispose(); } catch (Exception error) { errors.Add(error); }
    }

    this._commands.Dispose();
    this._systems.Clear();
    GC.SuppressFinalize(this);

    if (errors.Count > 0) throw new AggregateException("System disposal failed.", errors);
  }

  private void Checkpoint() {
    if (!this._updating || this._checkpointing) throw new InvalidOperationException("Checkpoint must run within a system phase.");

    this._checkpointing = true;

    try {
      this._commands.Playback();
      this._store.Events.Dispatch();
    } finally { this._checkpointing = false; }
  }

  private void EnsureIdle() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this._store.EnsureAccess();

    if (this._updating) throw new InvalidOperationException("Pipeline registration, disposal and updates cannot nest.");
  }
}
