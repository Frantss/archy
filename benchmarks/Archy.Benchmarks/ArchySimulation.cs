using Archy.Storage;

namespace Archy.Benchmarks;

internal sealed class ArchySimulation : ISimulation {
  private readonly Scenario _scenario;
  private readonly Func<IEntityStorage> _createStorage;
  private readonly Entity[] _entities;
  private readonly EntityStore? _store;
  private readonly ArchetypeQuery<Position, Velocity, ActorId>? _movement;
  private readonly ArchetypeQuery<Position, Velocity, Lifetime, ActorId>? _projectiles;
  private readonly Action<Position, Velocity, ActorId, Entity> _move;
  private readonly Action<Position, Velocity, Lifetime, ActorId, Entity> _moveProjectile;
  private readonly ComponentIndex<Cell, int> _cells;
  private readonly IDisposable? _subscription;
  private long _nextId;
  private long _spawned;
  private long _retired;
  private long _notifications;
  private long _notificationChecksum;
  private long _checksum;
  private long _lastResult;
  private int _step;

  public ArchySimulation((Scenario Scenario, int EntityCount) input, Func<IEntityStorage> createStorage) {
    this._scenario = input.Scenario;
    this._createStorage = createStorage;
    this._entities = new Entity[input.EntityCount];
    this._move = this.Move;
    this._moveProjectile = this.MoveProjectile;

    if (input.Scenario == Scenario.WorldLoad) return;

    this._store = new(createStorage());

    for (var i = 0; i < this._entities.Length; i++) {
      this._entities[i] = this.Spawn(this._store, Model.Actor(++this._nextId, this._scenario));
    }

    this._spawned = this._store.Count;
    this._movement = this._store.Query<Position, Velocity, ActorId>();

    if (this._scenario == Scenario.ActorActivation) this._movement.WithoutAnyTags(Tags.Get<Dormant>());

    this._movement.FreezeFilter();
    this._projectiles = this._store.Query<Position, Velocity, Lifetime, ActorId>().FreezeFilter();

    if (this._scenario == Scenario.SpatialLookup) this._cells = this._store.ComponentIndex<Cell, int>();
    if (this._scenario == Scenario.DamageNotifications)
      this._subscription = this._store.Events.Subscribe<ComponentUpdated<Health>>(this.OnHealthUpdated);
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
    this._subscription?.Dispose();
    this._store?.Dispose();
    Array.Clear(this._entities);
  }

  private Entity Spawn(EntityStore store, ActorState actor) {
    var entity = store.Create();
    entity.Add(new ActorId { Value = actor.Id });
    entity.Add(actor.Position);
    entity.Add(actor.Velocity);
    entity.Add(actor.Health);

    if (this._scenario == Scenario.ProjectileChurn) entity.Add(new Lifetime { Remaining = actor.Lifetime });
    if (this._scenario == Scenario.SpatialLookup) entity.Add(new Cell { Value = actor.Cell });

    return entity;
  }

  private long LoadWorld() {
    using var store = new EntityStore(this._createStorage());

    for (var i = 0; i < this._entities.Length; i++) {
      this._entities[i] = this.Spawn(store, Model.Actor(i + 1, this._scenario));
    }

    foreach (var entity in this._entities) {
      var id = entity.Get<ActorId>();
      var position = entity.Get<Position>();
      var velocity = entity.Get<Velocity>();
      var health = entity.Get<Health>();
      this._checksum += Model.WaveHash(id.Value, position, velocity, health);
    }

    this._spawned += store.Count;
    this._retired += store.Count;

    return this._checksum;
  }

  private long Movement() {
    this._movement!.ForEachEntity(this._move);

    return this._checksum;
  }

  private void Move(Position position, Velocity velocity, ActorId id, Entity entity) {
    var next = Model.Advance(position, velocity);
    entity.Update(next);
    this._checksum += Model.Hash(id.Value, next);
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
      var lifetime = entity.Get<Lifetime>();

      if (lifetime.Remaining > 0) continue;

      entity.Delete();
      this._entities[i] = this.Spawn(this._store!, Model.Actor(++this._nextId, this._scenario, replacement: true));
      this._spawned++;
      this._retired++;
    }

    return this._checksum;
  }

  private void MoveProjectile(Position position, Velocity velocity, Lifetime lifetime, ActorId id, Entity entity) {
    var next = Model.Advance(position, velocity);
    entity.Update(next);
    entity.Update(new Lifetime { Remaining = lifetime.Remaining - 1 });
    this._checksum += Model.Hash(id.Value, next);
  }

  private long Spatial() {
    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var position = entity.Get<Position>();
      var velocity = entity.Get<Velocity>();
      var next = Model.Advance(position, velocity);
      entity.Update(next);
      entity.Update(new Cell { Value = Model.CellFor(next) });
    }

    for (var lookup = 0; lookup < Model.LookupCount; lookup++) {
      var key = (lookup * 13 + this._step) % Model.CellCount;

      foreach (var entity in this._cells[key]) {
        var id = entity.Get<ActorId>();
        var position = entity.Get<Position>();
        this._checksum += Model.Hash(id.Value, position) * (key + 1);
      }
    }

    return this._checksum;
  }

  private long Damage() {
    var before = this._notificationChecksum;

    for (var i = this._step % 8; i < this._entities.Length; i += 8) {
      var entity = this._entities[i];
      var health = entity.Get<Health>();
      entity.Update(Model.Damage(health));
    }

    this._store!.Events.Dispatch();

    return this._notificationChecksum - before;
  }

  private void OnHealthUpdated(ComponentUpdated<Health> change) {
    var id = change.Entity.Get<ActorId>();
    this._notifications++;
    this._notificationChecksum += Model.Notification(id.Value, change.OldValue, change.NewValue, change.Sequence);
  }

  private ActorState Read(Entity entity) {
    var id = entity.Get<ActorId>();
    var lifetime = this._scenario == Scenario.ProjectileChurn ? entity.Get<Lifetime>() : default;
    var cell = this._scenario == Scenario.SpatialLookup ? entity.Get<Cell>() : default;

    return new() {
      Id = id.Value,
      Position = entity.Get<Position>(),
      Velocity = entity.Get<Velocity>(),
      Health = entity.Get<Health>(),
      Lifetime = this._scenario == Scenario.ProjectileChurn ? lifetime.Remaining : -1,
      Cell = this._scenario == Scenario.SpatialLookup ? cell.Value : -1,
      Dormant = entity.HasTag<Dormant>()
    };
  }
}
