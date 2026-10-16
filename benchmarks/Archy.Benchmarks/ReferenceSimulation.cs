namespace Archy.Benchmarks;

internal sealed class ReferenceSimulation : ISimulation {
  private readonly Scenario _scenario;
  private readonly ActorState[] _actors;
  private long _nextId;
  private long _spawned;
  private long _retired;
  private long _notifications;
  private long _notificationChecksum;
  private long _lastResult;
  private int _step;

  public ReferenceSimulation((Scenario Scenario, int EntityCount) input) {
    this._scenario = input.Scenario;
    this._actors = Enumerable.Range(1, input.EntityCount).Select(id => Model.Actor(id, input.Scenario)).ToArray();
    this._nextId = input.EntityCount;
    this._spawned = input.Scenario == Scenario.WorldLoad ? 0 : input.EntityCount;
  }

  public long Step() {
    this._lastResult = this._scenario switch {
      Scenario.WorldLoad => this.LoadWorld(),
      Scenario.Movement => this.MoveActors(),
      Scenario.ActorActivation => this.Activation(),
      Scenario.ProjectileChurn => this.Projectiles(),
      Scenario.SpatialLookup => this.Spatial(),
      Scenario.DamageNotifications => this.Damage(),
      _ => throw new InvalidOperationException("Unknown scenario.")
    };
    this._step++;

    return this._lastResult;
  }

  public Snapshot Capture() => new(
    this._scenario == Scenario.WorldLoad ? 0 : this._actors.Length,
    this._scenario == Scenario.WorldLoad ? [] : this._actors.OrderBy(actor => actor.Id).ToArray(),
    this._spawned,
    this._retired,
    this._notifications,
    this._notificationChecksum,
    this._lastResult
  );

  public void Dispose() { }

  private long LoadWorld() {
    this._spawned += this._actors.Length;
    this._retired += this._actors.Length;

    return this._actors.Sum(actor => Model.WaveHash(actor.Id, actor.Position, actor.Velocity, actor.Health));
  }

  private long MoveActors() {
    long result = 0;

    for (var i = 0; i < this._actors.Length; i++) {
      var actor = this._actors[i];

      if (actor.Dormant) continue;

      var next = Model.Advance(actor.Position, actor.Velocity);
      this._actors[i] = actor with { Position = next };
      result += Model.Hash(actor.Id, next);
    }

    return result;
  }

  private long Activation() {
    long result = 0;

    for (var phase = 0; phase < 16; phase++) {
      for (var i = phase % 8; i < this._actors.Length; i += 8) {
        this._actors[i] = this._actors[i] with { Dormant = phase < 8 };
      }

      result += this.MoveActors();
    }

    return result;
  }

  private long Projectiles() {
    var result = this.MoveActors();

    for (var i = 0; i < this._actors.Length; i++) {
      var actor = this._actors[i];
      var remaining = actor.Lifetime - 1;

      if (remaining <= 0) {
        this._actors[i] = Model.Actor(++this._nextId, this._scenario, replacement: true);
        this._spawned++;
        this._retired++;
      } else this._actors[i] = actor with { Lifetime = remaining };
    }

    return result;
  }

  private long Spatial() {
    for (var i = this._step % 8; i < this._actors.Length; i += 8) {
      var actor = this._actors[i];
      var next = Model.Advance(actor.Position, actor.Velocity);
      this._actors[i] = actor with { Position = next, Cell = Model.CellFor(next) };
    }

    long result = 0;

    for (var lookup = 0; lookup < Model.LookupCount; lookup++) {
      var key = (lookup * 13 + this._step) % Model.CellCount;

      foreach (var actor in this._actors) {
        if (actor.Cell == key) result += Model.Hash(actor.Id, actor.Position) * (key + 1);
      }
    }

    return result;
  }

  private long Damage() {
    var before = this._notificationChecksum;

    for (var i = this._step % 8; i < this._actors.Length; i += 8) {
      var actor = this._actors[i];
      var next = Model.Damage(actor.Health);
      this._actors[i] = actor with { Health = next };
      this._notifications++;
      this._notificationChecksum += Model.Notification(actor.Id, actor.Health, next, this._notifications);
    }

    return this._notificationChecksum - before;
  }
}
