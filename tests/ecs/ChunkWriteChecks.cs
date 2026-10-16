using Archy;

namespace Archy.Tests;

internal static class ChunkWriteChecks {
  public static void Run() {
    WritesAcrossChunks();
    Interleaved(false);
    Interleaved(true);
    CallbackDisposesLease();
    ProjectionArities();
  }

  private static void WritesAcrossChunks() {
    using var store = new EntityStore();
    var entities = new Entity[5000];

    for (var i = 0; i < entities.Length; i++) {
      entities[i] = store.Create();
      entities[i].Add(new QueryPosition(i));
      entities[i].Add(new QueryVelocity(2));
    }

    var history = new List<ComponentUpdated<QueryPosition>>();
    using var events = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(history.Add);
    var visits = 0;

    foreach (var (positions, velocities, handles) in store.Query<QueryPosition, QueryVelocity>().Chunks) {
      for (var i = 0; i < handles.Count; i++) {
        var entity = handles[i];
        var position = positions[i];
        var velocity = velocities[i];
        entity.Update(new QueryPosition(position.Value + velocity.Value));
        Check.That(entity.TryUpsert(new QueryPosition(position.Value + 3)) && !entity.TryAdd(position),
          "Chunk bindings preserve upsert and strict add semantics.");
        entity.Update(new QueryVelocity(3));

        if (visits++ == 0) {
          Check.Throws<InvalidOperationException>(() => store.Create());
          Check.Throws<InvalidOperationException>(() => entity.Remove<QueryPosition>());
          Check.Throws<InvalidOperationException>(() => entity.AddTag<QueryVisible>());
        }

        Check.That(positions[i].Value == position.Value + 3 && velocities[i].Value == 3,
          "Chunk bindings alternate typed columns and read current values across native boundaries.");
      }
    }

    Check.That(visits == entities.Length && history.Count == 0,
      "Chunk updates visit each row and defer observed events.");
    store.Events.Dispatch();
    Check.That(
      history.Count == entities.Length * 2 && history.Zip(history.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
      "Bound chunk writes preserve deferred FIFO event sequence."
    );

    for (var i = 0; i < entities.Length; i++) {
      var before = history[i * 2];
      var after = history[i * 2 + 1];
      Check.That(before.Entity == after.Entity && before.OldValue.Value + 2 == before.NewValue.Value
        && before.NewValue == after.OldValue && after.NewValue.Value == before.OldValue.Value + 3,
        "Observed chunk writes capture independent per-write old/new history.");
      Check.That(entities[i].Get<QueryPosition>().Value == i + 3,
        "Bound writes stay aligned with every entity handle.");
    }
  }

  private static void Interleaved(bool reverse) {
    using var store = new EntityStore();
    var first = store.Create();
    first.Add(new QueryPosition(1));
    var second = store.Create();
    second.Add(new QueryPosition(2));
    using var left = store.Query<QueryPosition>().Chunks.GetEnumerator();
    using var right = store.Query<QueryPosition>().Chunks.GetEnumerator();
    Check.That(left.MoveNext() && right.MoveNext(), "Independent chunk cursors can be active together.");
    var a = left.Current;
    var b = right.Current;
    a.Item2[0].Update(new QueryPosition(10));
    b.Item2[1].Update(new QueryPosition(20));

    if (reverse) {
      right.Dispose();
      Check.Throws<InvalidOperationException>(() => _ = b.Item2[0]);
      a.Item2[0].Update(new QueryPosition(30));
      Check.Throws<InvalidOperationException>(() => first.Delete());
      left.Dispose();
    } else {
      left.Dispose();
      Check.Throws<InvalidOperationException>(() => _ = a.Item2[0]);
      b.Item2[0].Update(new QueryPosition(30));
      Check.Throws<InvalidOperationException>(() => first.Delete());
      right.Dispose();
    }

    Check.Throws<InvalidOperationException>(() => _ = a.Item1.Count);
    Check.Throws<InvalidOperationException>(() => _ = b.Item1[0]);
    Check.That(first.Get<QueryPosition>().Value == 30 && second.Get<QueryPosition>().Value == 20,
      "Interleaved cursors bind the selected handle in either disposal order.");
    first.Remove<QueryPosition>();
    Check.That(!first.TryUpdate(new QueryPosition(99)), "Expired cursors cannot write a vacated component column.");
    first.Delete();
    var replacement = store.Create();
    replacement.Add(new QueryPosition(40));
    Check.That(!first.TryUpsert(new QueryPosition(99)) && replacement.Get<QueryPosition>().Value == 40,
      "Expired chunk bindings reject stale generations after structural reuse.");
  }

  private static void CallbackDisposesLease() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    using var cursor = store.Query<QueryPosition>().Chunks.GetEnumerator();
    Check.That(cursor.MoveNext(), "A chunk lease can surround a nested callback traversal.");
    var view = cursor.Current;
    view.Item2[0].Update(new QueryPosition(2));
    store.Query<QueryPosition>().ForEachEntity((_, current) => {
      cursor.Dispose();
      current.Update(new QueryPosition(3));
    });
    Check.Throws<InvalidOperationException>(() => _ = view.Item2.Count);
    entity.Remove<QueryPosition>();
    Check.That(!entity.TryUpdate(new QueryPosition(99)),
      "Callback restoration never revives an expired chunk binding.");
    entity.Add(new QueryOwned([1]));
    entity.Add(new QueryPosition(4));

    foreach (var (owned, handles) in store.Query<QueryOwned>().Chunks) {
      _ = handles[0];
      QueryOwnedCopy.Reenter = () => entity.Update(new QueryPosition(99));

      try { Check.Throws<InvalidOperationException>(() => _ = owned[0]); }
      finally { QueryOwnedCopy.Reenter = null; }
    }

    Check.That(entity.Get<QueryPosition>().Value == 4, "Bound writes retain copy-policy preparation guards.");
  }

  private static void ProjectionArities() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    entity.Add(new QueryVelocity(2));
    entity.Add(new IndexedGroup { Value = 3 });
    entity.Add(new Extra(4));

    foreach (var handles in store.Query().Chunks) handles[0].Update(new QueryPosition(2));
    foreach (var (_, handles) in store.Query<QueryPosition>().Chunks) handles[0].Update(new QueryPosition(3));
    foreach (var (_, _, _, handles) in store.Query<QueryPosition, QueryVelocity, IndexedGroup>().Chunks)
      handles[0].Update(new IndexedGroup { Value = 4 });

    foreach (var (position, velocity, group, extra, handles) in store.Query<QueryPosition, QueryVelocity, IndexedGroup, Extra>().Chunks) {
      Check.That(position[0].Value == 3 && velocity[0].Value == 2 && group[0].Value == 4 && extra[0].Value == 4
        && handles[0] == entity, "Fused chunk projections preserve every public tuple arity and indexed writes.");
      Check.Throws<InvalidOperationException>(() => Task.Run(() => position.Count).GetAwaiter().GetResult());
    }
  }

  private readonly record struct Extra(int Value) : IComponent;
}
