using global::Arch.Core;
using NativeEntity = global::Arch.Core.Entity;
using NativeWorld = global::Arch.Core.World;

namespace Archy.Benchmarks;

internal sealed class ArchSimulation : ISimulation {
  private readonly Scenario _scenario;
  private readonly NativeEntity[] _entities;
  private readonly NativeWorld? _world;
  private readonly QueryDescription _movement;
  private readonly QueryDescription _projectiles;
  private readonly ForEach<Position, Velocity, ActorId> _move;
  private readonly ForEach<Position, Velocity, Lifetime, ActorId> _moveProjectile;
  private readonly Dictionary<int, HashSet<NativeEntity>> _cells = [];
  private readonly List<HealthChange> _events = [];
  private long _nextId;
  private long _spawned;
  private long _retired;
  private long _notifications;
  private long _sequence;
  private long _notificationChecksum;
  private long _checksum;
  private long _lastResult;
  private int _step;

  public ArchSimulation((Scenario Scenario, int EntityCount) input) {
    this._scenario = input.Scenario;
    this._entities = new NativeEntity[input.EntityCount];
    this._move = this.Move;
    this._moveProjectile = this.MoveProjectile;
    this._movement = new QueryDescription().WithAll<Position, Velocity, ActorId>();
    this._projectiles = new QueryDescription().WithAll<Position, Velocity, Lifetime, ActorId>();

    if (input.Scenario == Scenario.WorldLoad) return;

    this._world = NativeWorld.Create();

    if (this._scenario == Scenario.ActorActivation) this._movement = this._movement.WithNone<Dormant>();

    if (this._scenario == Scenario.SpatialLookup) {
      for (var i = 0; i < Model.CellCount; i++) this._cells.Add(i, []);
    }

    for (var i = 0; i < this._entities.Length; i++) {
      var actor = Model.Actor(++this._nextId, this._scenario);
      var entity = this.Spawn(this._world, actor);
      this._entities[i] = entity;

      if (this._scenario == Scenario.SpatialLookup) this._cells[actor.Cell].Add(entity);
    }

    this._spawned = this._world.Size;
  }

  public long Step() {
    this._checksum = 0;

    this._lastResult = this._scenario switch {
      Scenario.WorldLoad => this.LoadWorld(),
      Scenario.Movement => this.Movement(),
      Scenario.ActorActivation => this.Activation(),
      Scenario.ProjectileChurn => this.Projectiles(),
      Scenario.SpatialLookup => this.Spatial(),
      Scenario.DamageNotifications => this.Damage(),
      _ => throw new InvalidOperationException("Unknown scenario.")
    };
    this._step++;

    return this._lastResult;
  }

  public Snapshot Capture() {
    var actors = this._world is null ? [] : this._entities.Select(this.Read).OrderBy(actor => actor.Id).ToArray();

    return new(
      this._world?.Size ?? 0,
      actors,
      this._spawned,
      this._retired,
      this._notifications,
      this._notificationChecksum,
      this._lastResult
    );
  }

  public void Dispose() {
    this._world?.Dispose();
    this._events.Clear();
    this._cells.Clear();
    Array.Clear(this._entities);
  }

  private NativeEntity Spawn(NativeWorld world, ActorState actor) {
    var id = new ActorId { Value = actor.Id };

    return this._scenario switch {
      Scenario.ProjectileChurn => world.Create(
        id,
        actor.Position,
        actor.Velocity,
        actor.Health,
        new Lifetime { Remaining = actor.Lifetime }
      ),
      Scenario.SpatialLookup => world.Create(
        id,
        actor.Position,
        actor.Velocity,
        actor.Health,
        new Cell { Value = actor.Cell }
      ),
      _ => world.Create(id, actor.Position, actor.Velocity, actor.Health)
    };
  }

  private long LoadWorld() {
    using var world = NativeWorld.Create();

    for (var i = 0; i < this._entities.Length; i++) {
      this._entities[i] = this.Spawn(world, Model.Actor(i + 1, this._scenario));
    }

    foreach (var entity in this._entities) {
      var id = world.Get<ActorId>(entity);
      var position = world.Get<Position>(entity);
      var velocity = world.Get<Velocity>(entity);
      var health = world.Get<Health>(entity);
      this._checksum += Model.WaveHash(id.Value, position, velocity, health);
    }

    this._spawned += world.Size;
    this._retired += world.Size;

    return this._checksum;
  }

  private long Movement() {
    this._world!.Query(in this._movement, this._move);

    return this._checksum;
  }

  private void Move(ref Position position, ref Velocity velocity, ref ActorId id) {
    position = Model.Advance(position, velocity);
    this._checksum += Model.Hash(id.Value, position);
  }

  private long Activation() {
    var world = this._world!;

    for (var phase = 0; phase < 16; phase++) {
      for (var i = phase % 8; i < this._entities.Length; i += 8) {
        if (phase < 8) world.Add<Dormant>(this._entities[i]);
        else world.Remove<Dormant>(this._entities[i]);
      }

      world.Query(in this._movement, this._move);
    }

    return this._checksum;
  }

  private long Projectiles() {
    var world = this._world!;
    world.Query(in this._projectiles, this._moveProjectile);

    for (var i = 0; i < this._entities.Length; i++) {
      var entity = this._entities[i];
      var lifetime = world.Get<Lifetime>(entity);

      if (lifetime.Remaining > 0) continue;

      world.Destroy(entity);
      this._entities[i] = this.Spawn(world, Model.Actor(++this._nextId, this._scenario, replacement: true));
      this._spawned++;
      this._retired++;
    }

    return this._checksum;
  }

  private void MoveProjectile(ref Position position, ref Velocity velocity, ref Lifetime lifetime, ref ActorId id) {
    position = Model.Advance(position, velocity);
    lifetime = new() { Remaining = lifetime.Remaining - 1 };
    this._checksum += Model.Hash(id.Value, position);
  }

  private long Spatial() {
    var world = this._world!;

    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var position = world.Get<Position>(entity);
      var velocity = world.Get<Velocity>(entity);
      var cell = world.Get<Cell>(entity);
      var next = Model.Advance(position, velocity);
      var key = Model.CellFor(next);
      world.Set(entity, next);
      world.Set(entity, new Cell { Value = key });

      if (key != cell.Value) {
        this._cells[cell.Value].Remove(entity);
        this._cells[key].Add(entity);
      }
    }

    for (var lookup = 0; lookup < Model.LookupCount; lookup++) {
      var key = (lookup * 13 + this._step) % Model.CellCount;

      foreach (var entity in this._cells[key]) {
        var id = world.Get<ActorId>(entity);
        var position = world.Get<Position>(entity);
        this._checksum += Model.Hash(id.Value, position) * (key + 1);
      }
    }

    return this._checksum;
  }

  private long Damage() {
    var world = this._world!;
    var before = this._notificationChecksum;

    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var health = world.Get<Health>(entity);
      var next = Model.Damage(health);
      world.Set(entity, next);
      this._events.Add(new() { Entity = entity, OldValue = health, NewValue = next, Sequence = ++this._sequence });
    }

    foreach (var change in this._events) {
      var id = world.Get<ActorId>(change.Entity);
      this._notifications++;
      this._notificationChecksum += Model.Notification(id.Value, change.OldValue, change.NewValue, change.Sequence);
    }

    this._events.Clear();

    return this._notificationChecksum - before;
  }

  private ActorState Read(NativeEntity entity) {
    var world = this._world!;
    var id = world.Get<ActorId>(entity);
    var lifetime = this._scenario == Scenario.ProjectileChurn ? world.Get<Lifetime>(entity) : default;
    var cell = this._scenario == Scenario.SpatialLookup ? world.Get<Cell>(entity) : default;

    return new() {
      Id = id.Value,
      Position = world.Get<Position>(entity),
      Velocity = world.Get<Velocity>(entity),
      Health = world.Get<Health>(entity),
      Lifetime = this._scenario == Scenario.ProjectileChurn ? lifetime.Remaining : -1,
      Cell = this._scenario == Scenario.SpatialLookup ? cell.Value : -1,
      Dormant = world.Has<Dormant>(entity)
    };
  }

  private readonly struct HealthChange {
    public NativeEntity Entity { get; init; }
    public Health OldValue { get; init; }
    public Health NewValue { get; init; }
    public long Sequence { get; init; }
  }
}
