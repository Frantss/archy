using FrifloEcs = global::Friflo.Engine.ECS;

namespace Archy.Benchmarks;

public enum Scenario { WorldLoad, Movement, ActorActivation, ProjectileChurn, SpatialLookup, DamageNotifications }

internal readonly record struct ActorId : IComponent, FrifloEcs.IComponent {
  public long Value { get; init; }
}

internal readonly record struct Position : IComponent, FrifloEcs.IComponent {
  public long X { get; init; }
  public long Y { get; init; }
}

internal readonly record struct Velocity : IComponent, FrifloEcs.IComponent {
  public long X { get; init; }
  public long Y { get; init; }
}

internal readonly record struct Health : IComponent, FrifloEcs.IComponent {
  public int Value { get; init; }
}

internal readonly record struct Lifetime : IComponent, FrifloEcs.IComponent {
  public int Remaining { get; init; }
}

internal partial struct Cell : IComponent, FrifloEcs.IIndexedComponent<int> {

  [Indexed]
  public int Value { get; init; }

  public int GetIndexedValue() => this.Value;
}

internal readonly struct Dormant : ITag, FrifloEcs.ITag;

internal readonly record struct ActorState {
  public long Id { get; init; }
  public Position Position { get; init; }
  public Velocity Velocity { get; init; }
  public Health Health { get; init; }
  public int Lifetime { get; init; }
  public int Cell { get; init; }
  public bool Dormant { get; init; }
}

internal sealed record Snapshot(
  int Population,
  ActorState[] Actors,
  long Spawned,
  long Retired,
  long Notifications,
  long NotificationChecksum,
  long LastResult
);

internal interface ISimulation : IDisposable {
  long Step();
  Snapshot Capture();
}

internal static class Model {
  public const int CellCount = 64;
  public const int LookupCount = 128;

  public static ActorState Actor(long id, Scenario scenario, bool replacement = false) {
    var position = new Position { X = id % 97, Y = id % 53 };

    return new() {
      Id = id,
      Position = position,
      Velocity = new() { X = id % 5 - 2, Y = id % 7 - 3 },
      Health = new() { Value = 100 },
      Lifetime = scenario == Scenario.ProjectileChurn ? replacement ? 8 : (int)((id - 1) % 8) + 1 : -1,
      Cell = scenario == Scenario.SpatialLookup ? CellFor(position) : -1
    };
  }

  public static Position Advance(Position position, Velocity velocity) =>
    new() { X = position.X + velocity.X, Y = position.Y + velocity.Y };

  public static Health Damage(Health health) => new() { Value = health.Value <= 7 ? 100 : health.Value - 7 };

  public static int CellFor(Position position) => ((int)(position.X / 16) & 7) + (((int)(position.Y / 16) & 7) << 3);

  public static long Hash(long id, Position position) => unchecked(id * 397 ^ position.X * 31 ^ position.Y);

  public static long WaveHash(long id, Position position, Velocity velocity, Health health) =>
    unchecked(Hash(id, position) + velocity.X * 17 + velocity.Y * 13 + health.Value);

  public static long Notification(long id, Health oldValue, Health newValue, long sequence) =>
    unchecked(id * 397 + oldValue.Value * 31 + newValue.Value * 17 + sequence);
}
