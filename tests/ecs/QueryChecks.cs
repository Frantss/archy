using Archy;

namespace Archy.Tests;

internal static class QueryChecks {
  public static void Run() {
    OwnedColumns();
    TraversalLifetime();
    using var store = StoreFactory.Create();
    var all = new List<Entity>();

    for (var i = 0; i < 600; i++) {
      var entity = store.Create();
      entity.Add(new QueryPosition(i));

      if (i % 2 == 0) entity.Add(new QueryVelocity(2));
      if (i % 3 == 0) entity.AddTag<QueryVisible>();
      if (i % 5 == 0) entity.AddTag<QueryFrozen>();

      all.Add(entity);
    }

    var moving = store.Query<QueryPosition, QueryVelocity>().WithoutAnyTags(Tags.Get<QueryFrozen>());
    Check.That(moving.Count == 240, "Typed signatures and excluded tags compose.");
    Check.That(store.Query().AllTags(Tags.Get<QueryVisible>()).AnyTags(Tags.Get<QueryFrozen>()).Count == 280, "Positive All/Any tag groups are alternatives.");
    Check.That(store.Query().AllComponents(ComponentTypes.Get<QueryPosition, QueryVelocity>()).AnyComponents(ComponentTypes.Get<IndexedIdentity>()).Count == 300, "All/Any component groups are alternatives.");
    Check.That(store.Query().WithoutAllTags(Tags.Get<QueryVisible, QueryFrozen>()).Count == 560, "WithoutAll vetoes only the full group.");
    var independent = store.Query<QueryPosition>();
    moving.Filter.AllTags(Tags.Get<QueryVisible>());
    Check.That(moving.Count == 80 && independent.Count == 600, "Filters belong to individual queries.");
    moving.ForEachEntity((position, velocity, entity) => entity.Update(new QueryPosition(position.Value + velocity.Value)));
    Check.That(all[6].Get<QueryPosition>().Value == 8, "ForEachEntity supplies values for method-driven updates.");
    using (var traversal = moving.Entities.GetEnumerator()) {
      Check.That(traversal.MoveNext(), "Traversal begins.");
      var entity = traversal.Current;
      Check.Throws<InvalidOperationException>(() => store.Create());
      Check.Throws<InvalidOperationException>(() => entity.Delete());
      Check.Throws<InvalidOperationException>(() => entity.Remove<QueryVelocity>());
      Check.Throws<InvalidOperationException>(() => entity.Upsert(new IndexedIdentity(80)));
      Check.Throws<InvalidOperationException>(() => entity.AddTag<QueryFrozen>());
      Check.Throws<InvalidOperationException>(() => moving.Filter.AnyTags(Tags.Get<QueryFrozen>()));
      entity.Upsert(new QueryPosition(99));
      Check.That(independent.Count == 600, "Nested traversals have separate lifetime guards.");
    }

    var copyCount = 0;
    ComponentChunk<QueryPosition> escaped = default;

    foreach (var (positions, entities) in independent.Chunks) {
      escaped = positions;
      Check.Throws<ArgumentOutOfRangeException>(() => _ = positions[positions.Count]);
      Check.Throws<ArgumentOutOfRangeException>(() => _ = entities[-1]);
      Check.That(positions.Count == entities.Count, "Chunk values align with their entity handles.");

      for (var i = 0; i < positions.Count; i++) {
        var value = positions[i];
        Check.That(value == entities[i].Get<QueryPosition>(), "Chunk reads current owned values.");
        copyCount++;
      }
    }

    Check.That(copyCount == 600, "Chunk traversal spans storage chunks and archetypes.");
    Check.Throws<InvalidOperationException>(() => _ = escaped[0]);
    moving.FreezeFilter();
    Check.Throws<InvalidOperationException>(() => moving.AllTags(Tags.Get<QueryFrozen>()));
    foreach (var entity in all.Where((_, i) => i % 7 == 0)) entity.Delete();

    Check.That(independent.Entities.ToHashSet().SetEquals(all.Where(entity => entity.IsAlive)), "Plans remain valid after swap removals.");
    var late = store.Create();
    late.Add(new QueryPosition(1));
    late.Add(new IndexedIdentity(123));
    Check.That(independent.Entities.Contains(late), "New archetypes invalidate cached plans.");
  }

  private static void TraversalLifetime() {
    using var store = StoreFactory.Create();
    var first = store.Create();
    first.Add(new QueryPosition(1));
    var second = store.Create();
    second.Add(new QueryPosition(2));
    var query = store.Query<QueryPosition>();
    var enumerator = query.Entities.GetEnumerator();
    query.WithoutAnyTags(Tags.Get<QueryFrozen>());
    Check.That(enumerator.MoveNext(), "Entity traversal begins at first MoveNext.");
    Check.Throws<InvalidOperationException>(() => query.WithoutAnyTags(default));
    enumerator.Dispose();
    query.WithoutAnyTags(default);
    Check.That(store.Create().IsAlive, "Early iterator disposal releases structural guards.");
    Check.Throws<ArgumentException>(() => query.ForEachEntity((_, _) => throw new ArgumentException()));
    Check.That(store.Create().IsAlive, "Callback exceptions release structural guards.");
    using var otherThread = query.Entities.GetEnumerator();
    Check.That(otherThread.MoveNext(), "Thread test starts on the owning thread.");
    Check.Throws<InvalidOperationException>(() => Task.Run(() => otherThread.MoveNext()).GetAwaiter().GetResult());
    Check.Throws<InvalidOperationException>(() => Task.Run(() => _ = query.Count).GetAwaiter().GetResult());
    query.AllTags(Tags.Get<QueryFrozen>());
    Check.That(query.Count == 0, "Direct counts handle empty plans.");
    second.AddTag<QueryFrozen>();
    Check.That(query.Count == 1, "Direct counts refresh when an archetype is added.");
    second.Delete();
    Check.That(query.Count == 0, "Direct counts read current archetype populations after deletion.");
  }

  private static void OwnedColumns() {
    using var store = StoreFactory.Create();
    var entities = new List<Entity>();

    for (var i = 0; i < 300; i++) {
      var entity = store.Create();
      entity.Add(new QueryOwned([i]));
      entity.Add(new QueryPosition(i));
      entity.Add(new QueryVelocity(2));
      entity.Add(new IndexedGroup { Value = 1 });
      entities.Add(entity);
    }

    var visits = 0;
    var query = store.Query<QueryOwned, QueryPosition, QueryVelocity, IndexedGroup>().HasValue<IndexedGroup, int>(1);
    query.ForEachEntity((owned, position, velocity, group, entity) => {
      Check.That(owned.Values[0] == (entity == entities[299] ? 999 : position.Value), "Bound columns read values at visitation across chunks.");
      Check.That(velocity.Value == 2 && group.Value == (entity == entities[299] ? 2 : 1), "All four typed columns align with the entity row.");
      owned.Values[0] = -1;
      Check.That(entity.Get<QueryOwned>().Values[0] >= 0, "Callback copies cannot mutate owned storage.");

      if (visits++ == 0) {
        entities[299].Update(new QueryOwned([999]));
        entities[299].Update(new IndexedGroup { Value = 2 });
        Check.Throws<InvalidOperationException>(() => store.Create());
      }
    });
    Check.That(visits == 300 && query.Count == 299, "Typed callbacks preserve the initial indexed membership snapshot.");
    store.Query<QueryOwned>().ForEachEntity((owned, _) => owned.Values[0] = -1);
    store.Query<QueryOwned, QueryPosition>().ForEachEntity((owned, _, _) => owned.Values[0] = -1);
    store.Query<QueryOwned, QueryPosition, QueryVelocity>().ForEachEntity((owned, _, _, _) => owned.Values[0] = -1);

    ComponentChunk<QueryOwned> escaped = default;

    foreach (var (owned, _, _, _, handles) in query.Chunks) {
      escaped = owned;
      var value = owned[0];
      value.Values[0] = -1;
      Check.That(handles[0].Get<QueryOwned>().Values[0] >= 0, "Chunk reads apply copy policies.");
      handles[0].Update(new QueryOwned([777]));
      Check.That(owned[0].Values[0] == 777, "Bound chunk reads reflect later method updates.");
      QueryOwnedCopy.Reenter = () => store.Create();
      Check.Throws<InvalidOperationException>(() => _ = owned[0]);
      QueryOwnedCopy.Reenter = null;
    }

    Check.Throws<InvalidOperationException>(() => _ = escaped.Count);
    Check.Throws<InvalidOperationException>(() => _ = escaped[0]);
    QueryOwnedCopy.Reenter = () => store.Create();
    Check.Throws<InvalidOperationException>(() => store.Query<QueryOwned>().ForEachEntity((_, _) => {}));
    QueryOwnedCopy.Reenter = null;
    Check.That(store.Create().IsAlive, "Failed copy callbacks release traversal guards.");
  }
}

internal readonly record struct QueryPosition(int Value) : IComponent;
internal readonly record struct QueryVelocity(int Value) : IComponent;
internal readonly struct QueryVisible : ITag;
internal readonly struct QueryFrozen : ITag;

[ComponentCopy(typeof(QueryOwnedCopy))]
internal readonly record struct QueryOwned(int[] Values) : IComponent;

internal sealed class QueryOwnedCopy : IComponentCopy<QueryOwned> {
  public static Action? Reenter { get; set; }

  public QueryOwned Copy(in QueryOwned value) {
    Reenter?.Invoke();

    return new([..value.Values]);
  }
}
