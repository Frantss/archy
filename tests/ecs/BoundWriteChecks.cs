using Archy;

namespace Archy.Tests;

internal static class BoundWriteChecks {
  public static void Run() {
    DormantWrites();
    using var store = StoreFactory.Create();
    using var otherStore = StoreFactory.Create();
    var first = store.Create();
    first.Add(new QueryPosition(1));
    first.Add(new QueryVelocity(2));
    first.Add(new IndexedGroup { Value = 1 });
    first.Add(new Extra(3));
    var second = store.Create();
    second.Add(new QueryPosition(10));
    var stale = store.Create();
    stale.Add(new QueryPosition(20));
    stale.Delete();
    var foreign = otherStore.Create();
    foreign.Add(new QueryPosition(30));
    var history = new List<(Entity Entity, int Old, int Next)>();
    using var subscription = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item =>
      history.Add((item.Entity, item.OldValue.Value, item.NewValue.Value)));
    var query = store.Query<QueryPosition, QueryVelocity, IndexedGroup, Extra>();

    query.ForEachEntity((position, velocity, group, extra, entity) => {
      Check.That(entity == first, "Four-column callbacks bind the matching public entity.");
      Check.That(!entity.TryAdd(position), "Bound writes retain strict add semantics.");
      Check.That(!stale.TryUpdate(position), "Bound writes reject historical handles.");
      foreign.Update(new QueryPosition(31));
      second.Update(new QueryPosition(11));
      Check.Throws<InvalidOperationException>(() => entity.AddTag<QueryVisible>());
      Check.Throws<InvalidOperationException>(() => entity.Remove<QueryVelocity>());
      store.Query<QueryPosition, QueryVelocity>().ForEachEntity(
        (nested, _, current) => current.Upsert(nested with { Value = nested.Value + 1 })
      );
      Check.Throws<ArgumentException>(() => store.Query<QueryPosition, QueryVelocity>().ForEachEntity((_, _, current) => {
        current.Update(new QueryPosition(40));
        throw new ArgumentException();
      }));
      _ = store.Query<QueryPosition>().Entities.Last();
      entity.Update(new QueryPosition(50));
      entity.Update(new QueryVelocity(4));
      entity.Upsert(new Extra(6));
      entity.Update(new QueryPosition(51));
      entity.Update(new IndexedGroup { Value = 2 });
      Check.That(store.ComponentIndex<IndexedGroup, int>()[2].Contains(entity),
        "Indexed writes during callbacks retain index maintenance.");
      Check.That(history.Count == 0, "Bound event capture remains deferred.");
    });
    Check.That(first.Get<QueryPosition>().Value == 51 && second.Get<QueryPosition>().Value == 11,
      "Nested callbacks, plain enumerators and exceptions restore the outer write destination.");
    Check.That(first.Get<QueryVelocity>().Value == 4 && first.Get<Extra>().Value == 6,
      "Alternating typed writes bind the correct component arrays.");
    Check.That(foreign.Get<QueryPosition>().Value == 31, "Foreign-store writes use their own store context.");
    store.Events.Dispatch();
    Check.That(history.SequenceEqual(new[] {
      (second, 10, 11), (first, 1, 2), (first, 2, 40), (first, 40, 50), (first, 50, 51)
    }), "Bound and fallback updates capture ordered old/new values for the correct entities.");
    first.Update(new QueryPosition(52));
    Check.That(first.Get<QueryPosition>().Value == 52, "Finished callbacks release their write bindings.");
    Check.That(store.Create().IsAlive, "Finished nested callbacks release structural guards.");
    var callbacks = 0;
    store.Query().ForEachEntity(entity => {
      if (entity.Has<QueryPosition>()) { entity.Update(new QueryPosition(60)); callbacks++; }
    });
    store.Query<QueryPosition>().ForEachEntity((_, entity) => entity.Update(new QueryPosition(61)));
    store.Query<QueryPosition, QueryVelocity>().ForEachEntity((_, _, entity) => entity.Update(new QueryPosition(62)));
    store.Query<QueryPosition, QueryVelocity, Extra>().ForEachEntity(
      (_, _, _, entity) => entity.Update(new QueryPosition(63))
    );
    Check.That(callbacks == 2 && first.Get<QueryPosition>().Value == 63 && second.Get<QueryPosition>().Value == 61,
      "All callback arities preserve method-driven updates.");
  }

  private static void DormantWrites() {
    using var store = StoreFactory.Create();
    var entities = new Entity[5000];
    var expected = Enumerable.Range(0, entities.Length).ToArray();

    for (var i = 0; i < entities.Length; i++) {
      entities[i] = store.Create();
      entities[i].Add(new QueryPosition(i));
    }

    var callbacks = 0;
    store.Query<QueryPosition>().ForEachEntity((value, entity) => {
      var index = entity.Id - 1;
      callbacks++;
      Check.That(value.Value == expected[index], "Dormant callback bindings read current owned values.");

      if (index % 257 != 0) return;

      var other = (index + 1) % entities.Length;
      expected[other] = -index;
      entities[other].Update(new QueryPosition(expected[other]));
      expected[index]++;
      entity.Upsert(new QueryPosition(expected[index]));
      _ = store.Query<QueryPosition>().Entities.Last();
      entity.Update(new QueryPosition(expected[index]));
    });

    for (var i = 0; i < entities.Length; i++) {
      Check.That(entities[i].Get<QueryPosition>().Value == expected[i],
        "Sparse callback writes after read-only prefixes preserve current and other entity destinations.");
    }

    Check.That(callbacks == entities.Length, "Lazy bindings traverse all rows across chunk boundaries.");
  }

  private readonly record struct Extra(int Value) : IComponent;
}
