namespace Archy;

public sealed partial class CommandBuffer : IDisposable {
  private readonly EntityStore _store;
  private readonly List<Instruction> _commands = [];
  private readonly Dictionary<Type, IPayloads> _payloads = new();
  private readonly HashSet<Entity> _reservations = [];
  private object _stream = new();
  private bool _playing;
  private bool _disposed;
  public int Count { get { this.EnsureRecording(); return this._commands.Count; } }
  internal CommandBuffer(EntityStore store) => this._store = store;

  public Entity Create() {
    this.EnsureRecording();
    var entity = this._store.Reserve(this);
    this._reservations.Add(entity);
    this.Record(entity, CommandKind.Create, null, 0, false);

    return entity;
  }

  public void Add<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Add, false);
  public void Update<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Update, false);
  public void Upsert<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Upsert, false);
  public CommandTicket TryAdd<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Add, true);
  public CommandTicket TryUpdate<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Update, true);
  public CommandTicket TryUpsert<T>(Entity entity, in T value) where T : struct, IComponent => this.Write(entity, value, CommandKind.Upsert, true);
  public void Remove<T>(Entity entity) where T : struct, IComponent => this.Record(entity, CommandKind.Remove, this.Components<T>(), 0, false);
  public CommandTicket TryRemove<T>(Entity entity) where T : struct, IComponent => this.Record(entity, CommandKind.Remove, this.Components<T>(), 0, true);
  public void AddTag<T>(Entity entity) where T : struct, ITag => this.Record(entity, CommandKind.AddTag, this.Tags<T>(), 0, false);
  public CommandTicket TryAddTag<T>(Entity entity) where T : struct, ITag => this.Record(entity, CommandKind.AddTag, this.Tags<T>(), 0, true);
  public void RemoveTag<T>(Entity entity) where T : struct, ITag => this.Record(entity, CommandKind.RemoveTag, this.Tags<T>(), 0, false);
  public CommandTicket TryRemoveTag<T>(Entity entity) where T : struct, ITag => this.Record(entity, CommandKind.RemoveTag, this.Tags<T>(), 0, true);
  public void Delete(Entity entity) => this.Record(entity, CommandKind.Delete, null, 0, false);
  public CommandTicket TryDelete(Entity entity) => this.Record(entity, CommandKind.Delete, null, 0, true);

  public PlaybackResult Playback() {
    this.EnsureRecording();
    this._store.EnsurePlayback();
    this._playing = true;
    var outcomes = this._commands.Where(command => command.Try).ToDictionary(command => command.Ticket, _ => CommandOutcome.Canceled);
    var result = new PlaybackResult(outcomes);

    var createRunEnd = -1;

    try {
      for (var i = 0; i < this._commands.Count; i++) {
        var command = this._commands[i];

        try {
          if (command.Kind == CommandKind.Create && i >= createRunEnd) {
            createRunEnd = i + 1;

            while (createRunEnd < this._commands.Count && this._commands[createRunEnd].Kind == CommandKind.Create)
              createRunEnd++;

            if (createRunEnd - i > 1) this._store.PrepareCreateCapacity(createRunEnd - i);
          }
          var success = command.Kind switch {
            CommandKind.Create => this._store.Activate(command.Entity, this),
            CommandKind.Delete => this._store.TryDelete(command.Entity),
            _ => command.Payloads!.Execute(this._store, command.Entity, command.Kind, command.Payload)
          };

          if (command.Try) outcomes[command.Ticket] = success ? CommandOutcome.Succeeded : CommandOutcome.Failed;
          else if (!success) throw new MutationException("Command entity or component state does not match.");
        } catch (Exception error) { throw new PlaybackException(i, result, error); }
      }

      return result;
    } finally {
      this.Reset();
      this._playing = false;
    }
  }

  public void Clear() { this.EnsureRecording(); this.Reset(); }
  public void Dispose() {
    if (this._disposed) return;

    this.EnsureRecording();
    this.Reset();
    this._disposed = true;
    GC.SuppressFinalize(this);
  }

  private CommandTicket Write<T>(Entity entity, in T value, CommandKind kind, bool attempt) where T : struct, IComponent {
    this.EnsureRecording();
    var payloads = this.Components<T>();
    T copy;
    using (this._store.Prepare()) copy = TypeCatalog.Component<T>().Copy(value);
    var position = payloads.Values.Count;
    payloads.Values.Add(copy);

    return this.Record(entity, kind, payloads, position, attempt);
  }

  private ComponentPayloads<T> Components<T>() where T : struct, IComponent {
    this.EnsureRecording();

    if (!this._payloads.TryGetValue(typeof(T), out var payloads)) {
      payloads = new ComponentPayloads<T>();
      this._payloads.Add(typeof(T), payloads);
    }

    return (ComponentPayloads<T>)payloads;
  }

  private TagPayloads<T> Tags<T>() where T : struct, ITag {
    this.EnsureRecording();

    if (!this._payloads.TryGetValue(typeof(T), out var payloads)) {
      payloads = new TagPayloads<T>();
      this._payloads.Add(typeof(T), payloads);
    }

    return (TagPayloads<T>)payloads;
  }

  private CommandTicket Record(Entity entity, CommandKind kind, IPayloads? payloads, int position, bool attempt) {
    this.EnsureRecording();
    this._store.ValidateReservation(entity, this);
    var ticket = new CommandTicket(this._stream, this._commands.Count);
    this._commands.Add(new(entity, kind, payloads, position, attempt, ticket));

    return ticket;
  }

  private void Reset() {
    foreach (var entity in this._reservations) this._store.CancelReservation(entity, this);

    this._reservations.Clear();
    this._commands.Clear();
    this._payloads.Clear();
    this._eventPayloads.Clear();
    this._stream = new();
  }

  private void EnsureRecording() {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this._store.EnsureRecording();

    if (this._playing) throw new InvalidOperationException("Cannot record or play recursively during playback.");
  }

  private readonly record struct Instruction(Entity Entity, CommandKind Kind, IPayloads? Payloads, int Payload, bool Try, CommandTicket Ticket);
  private enum CommandKind { Add, Update, Upsert, Remove, AddTag, RemoveTag, Delete, Create, EntityEmit, StoreEmit }
  private interface IPayloads { bool Execute(EntityStore store, Entity entity, CommandKind kind, int index); }

  private sealed class ComponentPayloads<T> : IPayloads where T : struct, IComponent {
    public List<T> Values { get; } = [];
    public bool Execute(EntityStore store, Entity entity, CommandKind kind, int index) => kind switch {
      CommandKind.Add => store.TryWrite(entity, this.Values[index], WriteMode.Add),
      CommandKind.Update => store.TryWrite(entity, this.Values[index], WriteMode.Update),
      CommandKind.Upsert => store.TryWrite(entity, this.Values[index], WriteMode.Upsert),
      CommandKind.Remove => store.TryRemove<T>(entity),
      _ => throw new InvalidOperationException("Unexpected component command.")
    };
  }

  private sealed class TagPayloads<T> : IPayloads where T : struct, ITag {
    public bool Execute(EntityStore store, Entity entity, CommandKind kind, int index) => store.TryTag<T>(entity, kind == CommandKind.AddTag);
  }
}
