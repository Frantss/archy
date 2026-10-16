using Archy;

namespace Archy.Tests;

internal static class ComponentChecks {
  public static void Run() {
    RecycledChunks();
    using var store = new EntityStore();
    var entity = store.Create();

    Check.That(!entity.Has<Position>() && !entity.TryGet<Position>(out _), "Missing components are observable.");
    Check.That(!entity.TryUpdate(new Position(1)) && !entity.TryRemove<Position>(), "Try methods reject missing data.");
    Check.Throws<MutationException>(() => entity.Get<Position>());
    Check.Throws<MutationException>(() => entity.Update(new Position(1)));
    entity.Add(new Position(1));
    Check.That(!entity.TryAdd(new Position(2)), "Try-add never replaces a component.");
    Check.Throws<MutationException>(() => entity.Add(new Position(2)));
    Check.That(entity.Get<Position>() == new Position(1), "Failed writes leave the value unchanged.");
    entity.Update(new Position(3));
    Check.That(entity.TryUpsert(new Position(4)), "Try-upsert succeeds on an existing component.");
    entity.Upsert(new Speed(5));
    Check.That(entity.Get<Position>() == new Position(4) && entity.Get<Speed>() == new Speed(5), "Upsert supports both branches.");
    entity.AddTag<Selected>();
    Check.That(entity.HasTag<Selected>() && !entity.TryAddTag<Selected>(), "Tags are presence-only and add is strict.");
    Check.Throws<MutationException>(entity.AddTag<Selected>);
    entity.RemoveTag<Selected>();
    Check.That(!entity.TryRemoveTag<Selected>() && entity.Has<Position>(), "Removing tags preserves components.");
    entity.Remove<Position>();
    Check.That(!entity.Has<Position>() && entity.Get<Speed>() == new Speed(5), "Component removal preserves other columns.");
    Check.Throws<MutationException>(entity.Remove<Position>);
    entity.Delete();
    Check.That(!entity.TryUpsert(new Position(1)) && !entity.TryGet<Speed>(out _), "Deleted handles reject reads/writes.");
    Check.That(!default(Entity).TryAdd(new Position(1)), "Default handles reject try writes.");
    Check.Throws<InvalidOperationException>(() => store.Create().Add(new UnsafeBag([1])));
    Check.Throws<InvalidOperationException>(() => store.Create().AddTag<DataTag>());

    var input = new Bag([1, 2]);
    var copied = store.Create();
    copied.Add(input);
    input.Values[0] = 99;
    var read = copied.Get<Bag>();
    Check.That(read.Values[0] == 1, "A copy policy isolates caller-owned input.");
    read.Values[0] = 88;
    Check.That(copied.Get<Bag>().Values[0] == 1, "A copy policy isolates returned values.");
    copied.AddTag<Selected>();
    Check.That(copied.Get<Bag>().Values[0] == 1, "Structural moves retain owned payloads.");
    copied.Add(new Positive(7));
    Check.Throws<ArgumentOutOfRangeException>(() => copied.Upsert(new Positive(-1)));
    Check.That(copied.Get<Positive>() == new Positive(7), "Copy-policy failure leaves existing values unchanged.");
    ReentrantCopy.Store = store;
    var count = store.Count;
    Check.Throws<InvalidOperationException>(() => copied.Add(new Reentrant()));
    Check.That(store.Count == count && !copied.Has<Reentrant>(), "Copy policies cannot reenter mutations.");
    ReentrantCopy.Store = null;

    var entities = new List<Entity>();

    for (var i = 0; i < 800; i++) {
      var item = store.Create();
      item.Add(new Position(i));
      item.Add(new Speed(i * 2));
      entities.Add(item);
    }

    for (var i = 0; i < entities.Count; i += 3) entities[i].Delete();

    for (var i = entities.Count - 1; i >= 0; i--) {
      var item = entities[i];

      if (!item.IsAlive) continue;

      item.AddTag<Selected>();
      item.Remove<Speed>();
      item.Upsert(new Speed(i * 3));
      item.RemoveTag<Selected>();
      Check.That(item.Get<Position>() == new Position(i), "Chunk/swap moves repair the moved entity's location.");
      Check.That(item.Get<Speed>() == new Speed(i * 3), "Chunk moves preserve the correct component row.");
    }
  }

  private static void RecycledChunks() {
    using var store = new EntityStore();
    var query = store.Query<Position>();

    for (var cycle = 0; cycle < 3; cycle++) {
      var entities = Enumerable.Range(0, 600).Select(i => {
        var entity = store.Create();
        entity.Add(new Position(i + cycle * 1000));

        return entity;
      }).ToArray();

      foreach (var entity in entities.Where((_, i) => i % 3 == 0)) entity.Delete();

      foreach (var entity in entities.Where(entity => entity.IsAlive)) {
        entity.Add(new Speed(7));
        entity.Remove<Speed>();
      }

      Check.That(query.Count == 400 && entities.Where(entity => entity.IsAlive)
        .All(entity => entity.Get<Position>().X >= cycle * 1000 && !entity.Has<Speed>()),
        "Recycled chunks preserve current rows after swap removal and structural moves.");

      foreach (var entity in entities.Where(entity => entity.IsAlive)) entity.Delete();

      Check.That(query.Count == 0, "Empty recycled chunks do not contribute query rows.");
    }
  }

  private readonly record struct Position(int X) : IComponent;
  private readonly record struct Speed(int Value) : IComponent;
  private readonly record struct Selected : ITag;
  private readonly record struct UnsafeBag(int[] Values) : IComponent;
  private readonly record struct DataTag(int Value) : ITag;

  [ComponentCopy(typeof(BagCopy))]
  private readonly record struct Bag(int[] Values) : IComponent;

  private sealed class BagCopy : IComponentCopy<Bag> {
    public Bag Copy(in Bag value) => new([..value.Values]);
  }

  [ComponentCopy(typeof(PositiveCopy))]
  private readonly record struct Positive(int Value) : IComponent;

  private sealed class PositiveCopy : IComponentCopy<Positive> {
    public Positive Copy(in Positive value) {
      ArgumentOutOfRangeException.ThrowIfNegative(value.Value);

      return value;
    }
  }

  [ComponentCopy(typeof(ReentrantCopy))]
  private readonly record struct Reentrant : IComponent;

  private sealed class ReentrantCopy : IComponentCopy<Reentrant> {
    public static EntityStore? Store { get; set; }

    public Reentrant Copy(in Reentrant value) {
      Store?.Create();

      return value;
    }
  }
}
