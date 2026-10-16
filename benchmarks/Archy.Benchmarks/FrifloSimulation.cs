using Friflo.Engine.ECS;
using NativeEntity = Friflo.Engine.ECS.Entity;
using NativeStore = Friflo.Engine.ECS.EntityStore;

namespace Archy.Benchmarks;

internal sealed class FrifloSimulation : ISimulation {
  private readonly Scenario _scenario;
  private readonly NativeEntity[] _entities;
  private readonly NativeStore? _store;
  private readonly Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity, ActorId>? _movement;
  private readonly Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity, Lifetime, ActorId>? _projectiles;
  private readonly ForEachEntity<Position, Velocity, ActorId> _move;
  private readonly ForEachEntity<Position, Velocity, Lifetime, ActorId> _moveProjectile;
  private readonly Friflo.Engine.ECS.ComponentIndex<Cell, int> _cells;
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

  public FrifloSimulation((Scenario Scenario, int EntityCount) input) {
    this._scenario = input.Scenario;
    this._entities = new NativeEntity[input.EntityCount];
    this._move = this.Move;
    this._moveProjectile = this.MoveProjectile;

    if (input.Scenario == Scenario.WorldLoad) return;

    this._store = new();

    for (var i = 0; i < this._entities.Length; i++) {
      this._entities[i] = this.Spawn(this._store, Model.Actor(++this._nextId, this._scenario));
    }

    this._spawned = this._store.Count;
    this._movement = this._store.Query<Position, Velocity, ActorId>();

    if (this._scenario == Scenario.ActorActivation)
      this._movement.WithoutAnyTags(Friflo.Engine.ECS.Tags.Get<Dormant>());

    this._movement.FreezeFilter();
    this._projectiles = this._store.Query<Position, Velocity, Lifetime, ActorId>();
    this._projectiles.FreezeFilter();

    if (this._scenario == Scenario.SpatialLookup) this._cells = this._store.ComponentIndex<Cell, int>();
    if (this._scenario == Scenario.DamageNotifications) this._store.OnComponentAdded += this.OnHealthChanged;
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
    var actors = this._store is null ? [] : this._entities.Select(this.Read).OrderBy(actor => actor.Id).ToArray();

    return new(
      this._store?.Count ?? 0,
      actors,
      this._spawned,
      this._retired,
      this._notifications,
      this._notificationChecksum,
      this._lastResult
    );
  }

  public void Dispose() {
    if (this._store is not null && this._scenario == Scenario.DamageNotifications)
      this._store.OnComponentAdded -= this.OnHealthChanged;

    this._events.Clear();
    Array.Clear(this._entities);
  }

  private NativeEntity Spawn(NativeStore store, ActorState actor) {
    var id = new ActorId { Value = actor.Id };

    return this._scenario switch {
      Scenario.ProjectileChurn => store.CreateEntity(
        id,
        actor.Position,
        actor.Velocity,
        actor.Health,
        new Lifetime { Remaining = actor.Lifetime }
      ),
      Scenario.SpatialLookup => store.CreateEntity(
        id,
        actor.Position,
        actor.Velocity,
        actor.Health,
        new Cell { Value = actor.Cell }
      ),
      _ => store.CreateEntity(id, actor.Position, actor.Velocity, actor.Health)
    };
  }

  private long LoadWorld() {
    var store = new NativeStore();

    for (var i = 0; i < this._entities.Length; i++) {
      this._entities[i] = this.Spawn(store, Model.Actor(i + 1, this._scenario));
    }

    foreach (var entity in this._entities) {
      var id = entity.GetComponent<ActorId>();
      var position = entity.GetComponent<Position>();
      var velocity = entity.GetComponent<Velocity>();
      var health = entity.GetComponent<Health>();

      this._checksum += Model.WaveHash(id.Value, position, velocity, health);
    }

    this._spawned += store.Count;
    this._retired += store.Count;
    Array.Clear(this._entities);

    return this._checksum;
  }

  private long Movement() {
    this._movement!.ForEachEntity(this._move);

    return this._checksum;
  }

  private void Move(ref Position position, ref Velocity velocity, ref ActorId id, NativeEntity entity) {
    position = Model.Advance(position, velocity);
    this._checksum += Model.Hash(id.Value, position);
  }

  private long Activation() {
    for (var phase = 0; phase < 16; phase++) {
      for (var i = phase % 8; i < this._entities.Length; i += 8) {
        if (phase < 8) this._entities[i].AddTag<Dormant>();
        else this._entities[i].RemoveTag<Dormant>();
      }

      this._movement!.ForEachEntity(this._move);
    }

    return this._checksum;
  }

  private long Projectiles() {
    this._projectiles!.ForEachEntity(this._moveProjectile);

    for (var i = 0; i < this._entities.Length; i++) {
      var entity = this._entities[i];
      var lifetime = entity.GetComponent<Lifetime>();

      if (lifetime.Remaining > 0) continue;

      entity.DeleteEntity();
      this._entities[i] = this.Spawn(this._store!, Model.Actor(++this._nextId, this._scenario, replacement: true));
      this._spawned++;
      this._retired++;
    }

    return this._checksum;
  }

  private void MoveProjectile(ref Position position, ref Velocity velocity, ref Lifetime lifetime, ref ActorId id, NativeEntity entity) {
    position = Model.Advance(position, velocity);
    lifetime = new() { Remaining = lifetime.Remaining - 1 };
    this._checksum += Model.Hash(id.Value, position);
  }

  private long Spatial() {
    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var position = entity.GetComponent<Position>();
      var velocity = entity.GetComponent<Velocity>();
      var next = Model.Advance(position, velocity);
      ref var current = ref entity.GetComponent<Position>();

      current = next;
      entity.AddComponent(new Cell { Value = Model.CellFor(next) });
    }

    for (var lookup = 0; lookup < Model.LookupCount; lookup++) {
      var key = (lookup * 13 + this._step) % Model.CellCount;

      foreach (var entity in this._cells[key]) {
        var id = entity.GetComponent<ActorId>();
        var position = entity.GetComponent<Position>();

        this._checksum += Model.Hash(id.Value, position) * (key + 1);
      }
    }

    return this._checksum;
  }

  private long Damage() {
    var before = this._notificationChecksum;

    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var health = entity.GetComponent<Health>();

      entity.AddComponent(Model.Damage(health));
    }

    foreach (var change in this._events) {
      var id = change.Entity.GetComponent<ActorId>();

      this._notifications++;
      this._notificationChecksum += Model.Notification(id.Value, change.OldValue, change.NewValue, change.Sequence);
    }

    this._events.Clear();

    return this._notificationChecksum - before;
  }

  private void OnHealthChanged(ComponentChanged change) {
    if (change.Type != typeof(Health) || change.Action != ComponentChangedAction.Update) return;

    this._events.Add(new() {
      Entity = change.Entity,
      OldValue = change.OldComponent<Health>(),
      NewValue = change.Component<Health>(),
      Sequence = ++this._sequence
    });
  }

  private ActorState Read(NativeEntity entity) {
    var id = entity.GetComponent<ActorId>();
    var lifetime = this._scenario == Scenario.ProjectileChurn ? entity.GetComponent<Lifetime>() : default;
    var cell = this._scenario == Scenario.SpatialLookup ? entity.GetComponent<Cell>() : default;

    return new() {
      Id = id.Value,
      Position = entity.GetComponent<Position>(),
      Velocity = entity.GetComponent<Velocity>(),
      Health = entity.GetComponent<Health>(),
      Lifetime = this._scenario == Scenario.ProjectileChurn ? lifetime.Remaining : -1,
      Cell = this._scenario == Scenario.SpatialLookup ? cell.Value : -1,
      Dormant = entity.Tags.Has<Dormant>()
    };
  }

  private readonly struct HealthChange {
    public NativeEntity Entity { get; init; }
    public Health OldValue { get; init; }
    public Health NewValue { get; init; }
    public long Sequence { get; init; }
  }
}
