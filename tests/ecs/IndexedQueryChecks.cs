using Archy;

namespace Archy.Tests;

internal static class IndexedQueryChecks {
  public static void Run() {
    CachedSnapshots();
    ReusedIndexStorage();
    SparseTraversal();
    RetainedBuckets();
    using var store = new EntityStore();
    var all = new List<Entity>();

    for (var i = 0; i < 60; i++) {
      var entity = store.Create();
      entity.Add(new IndexedGroup { Value = i % 6 });
      entity.Add(new QueryPosition(i));
      all.Add(entity);
    }

    var range = store.Query<QueryPosition>().ValueInRange<IndexedGroup, int>(2, 4);
    Check.That(range.Count == 30, "Range endpoints are inclusive and duplicate keys include every entity.");
    var union = store.Query().HasValue<IndexedGroup, int>(2).ValueInRange<IndexedGroup, int>(2, 3);
    Check.That(union.Count == 20, "Indexed predicates union candidate handles without duplicate results.");
    Check.That(store.Query().ValueInRange<IndexedGroup, int>(8, 10).Count == 0, "Missing ranges are empty.");
    Check.That(store.Query().ValueInRange<IndexedGroup, int>(4, 2).Count == 0, "Reversed ranges are empty.");
    var expected = range.Entities.ToHashSet();
    var visited = new HashSet<Entity>();

    foreach (var entity in range.Entities) {
      Check.That(visited.Add(entity), "Key changes never duplicate a traversal candidate.");
      entity.Upsert(new IndexedGroup { Value = 99 });
      Check.That(entity.Get<IndexedGroup>().Value == 99, "Visited rows read current component values.");
      Check.That(range.Count == expected.Count - visited.Count, "Nested enumerators own independent snapshots.");
    }

    Check.That(visited.SetEquals(expected), "Key changes preserve initial membership across chunks.");
    var bucket = store.ComponentIndex<IndexedGroup, int>()[99];
    var bucketVisited = 0;

    foreach (var entity in bucket) {
      entity.Update(new IndexedGroup { Value = 100 });
      bucketVisited++;
    }

    Check.That(bucketVisited == 30 && bucket.Count == 0, "Direct bucket enumeration snapshots membership.");
    all[0].AddTag<QueryFrozen>();
    Check.That(store.Query().HasValue<IndexedGroup, int>(0).WithoutAnyTags(Tags.Get<QueryFrozen>()).Count == 9, "Composition exclusions apply after indexed selection.");
    var latest = store.Query<QueryPosition>().HasValue<IndexedGroup, int>(100);
    using var traversal = latest.Entities.GetEnumerator();
    Check.That(traversal.MoveNext(), "Indexed traversal starts.");
    var other = latest.Entities.Last();
    other.Update(new QueryPosition(999));
    var foundLatest = false;

    do {
      if (traversal.Current == other) foundLatest = traversal.Current.Get<QueryPosition>().Value == 999;
    } while (traversal.MoveNext());

    Check.That(foundLatest, "Values are read at visitation, independently of candidate snapshots.");
  }

  private static void CachedSnapshots() {
    using var store = new EntityStore();
    var first = store.Create();
    var second = store.Create();
    first.Add(new IndexedGroup { Value = 7 });
    second.Add(new IndexedGroup { Value = 7 });
    var bucket = store.ComponentIndex<IndexedGroup, int>()[7];
    using var earlier = bucket.GetEnumerator();
    using var sameVersion = bucket.GetEnumerator();
    first.Update(new IndexedGroup { Value = 8 });
    using var later = bucket.GetEnumerator();
    Check.That(earlier.MoveNext() && earlier.Current == first && sameVersion.MoveNext()
      && sameVersion.Current == first, "Each cursor owns its position even when membership snapshots are shared.");
    earlier.Dispose();
    Check.That(sameVersion.MoveNext() && sameVersion.Current == second && later.MoveNext() && later.Current == second,
      "Disposing one cursor preserves shared snapshots and subsequent membership versions.");
    sameVersion.Reset();
    Check.That(sameVersion.MoveNext() && sameVersion.Current == first, "Reset retains the cursor's captured membership version.");
    Check.Throws<ObjectDisposedException>(earlier.Reset);
    second.Delete();
    Check.That(!later.MoveNext() && bucket.Count == 0, "A live view and an older snapshot follow their separate lifetime rules.");
  }

  private static void ReusedIndexStorage() {
    using var store = new EntityStore();
    var index = store.ComponentIndex<IndexedGroup, int>();
    var entity = store.Create();
    entity.Add(new IndexedGroup { Value = 0 });
    var missing = index[1001];
    var views = new List<(int Key, EntityCollection View)>();

    for (var key = 0; key < 1000; key++) {
      entity.Update(new IndexedGroup { Value = key });
      views.Add((key, index[key]));
    }

    foreach (var item in views) {
      Check.That(item.View.Count == (item.Key == 999 ? 1 : 0), "Retained key views do not follow recycled internal buckets.");
    }

    entity.Update(new IndexedGroup { Value = 1001 });
    Check.That(missing.Count == 1 && missing[0] == entity, "Missing key views bind to later reused index storage.");
    entity.Update(new IndexedGroup { Value = 0 });
    Check.That(views[0].View.Count == 1 && views[0].View[0] == entity, "An old key view follows membership after repeated recycling.");
    using var snapshot = views[0].View.GetEnumerator();
    entity.Delete();
    var replacement = store.Create();
    replacement.Add(new IndexedGroup { Value = 1 });
    Check.That(snapshot.MoveNext() && snapshot.Current == entity && !snapshot.Current.IsAlive,
      "Index snapshots retain historical generations independently of bucket reuse.");
    snapshot.Reset();
    Check.That(snapshot.MoveNext() && snapshot.Current == entity, "Reset replays the original snapshot after backend reuse.");
  }

  private static void SparseTraversal() {
    using var store = new EntityStore();
    var all = new List<Entity>();

    for (var i = 0; i < 5000; i++) {
      var entity = store.Create();
      entity.Add(new QueryPosition(i));
      entity.Add(new IndexedGroup { Value = i is 0 or 1024 or 4095 ? 1 : 0 });
      all.Add(entity);
    }

    var foreignComposition = store.Create();
    foreignComposition.Add(new IndexedGroup { Value = 1 });
    all[1024].AddTag<QueryFrozen>();
    var query = store.Query<QueryPosition>().HasValue<IndexedGroup, int>(1)
      .ValueInRange<IndexedGroup, int>(1, 1).WithoutAnyTags(Tags.Get<QueryFrozen>());
    var expected = new HashSet<Entity> { all[0], all[4095] };
    Check.That(query.Count == 2 && query.Entities.ToHashSet().SetEquals(expected),
      "Selective counts and traversal validate composition and deduplicate unions.");
    var visited = new HashSet<Entity>();
    query.ForEachEntity((position, entity) => {
      Check.That(visited.Add(entity), "Selective callbacks visit each captured candidate once.");

      if (visited.Count == 1) {
        var later = expected.Single(item => item != entity);
        later.Update(new IndexedGroup { Value = 2 });
        later.Update(new QueryPosition(999));
        all[4000].Update(new IndexedGroup { Value = 1 });
      } else Check.That(position.Value == 999, "Selective callbacks read updated values at visitation.");
    });
    Check.That(visited.SetEquals(expected), "Selective traversal preserves captured membership after key changes.");
    var handles = query.Entities.ToHashSet();
    var fromChunks = new HashSet<Entity>();
    ComponentChunk<QueryPosition> escaped = default;

    foreach (var (positions, entities) in query.Chunks) {
      escaped = positions;

      for (var i = 0; i < positions.Count; i++) {
        fromChunks.Add(entities[i]);
        Check.That(positions[i] == entities[i].Get<QueryPosition>(), "Selective chunk row mappings remain aligned.");
      }
    }

    Check.That(fromChunks.SetEquals(handles), "Selective chunk and entity traversal agree.");
    Check.Throws<InvalidOperationException>(() => _ = escaped[0]);
  }

  private static void RetainedBuckets() {
    using var store = new EntityStore();
    var index = store.ComponentIndex<IndexedGroup, int>();
    var missing = index[7];
    var first = store.Create();
    first.Add(new IndexedGroup { Value = 7 });
    var retained = index[7];
    using var outer = retained.GetEnumerator();
    using var inner = retained.GetEnumerator();
    first.Delete();
    var replacement = store.Create();
    replacement.Add(new IndexedGroup { Value = 8 });
    Check.That(retained.Count == 0, "Recycled bucket buffers cannot expose a different key through a retained view.");
    replacement.Update(new IndexedGroup { Value = 7 });
    Check.That(missing.Count == 1 && missing[0] == replacement, "Initially empty bucket views follow later membership.");
    Check.That(retained.Count == 1 && retained[0] == replacement, "Retained bucket views follow bucket removal and recreation.");
    Check.That(outer.MoveNext() && outer.Current == first && !outer.MoveNext(), "Bucket snapshots retain original generations after deletion and reuse.");
    outer.Reset();
    Check.That(outer.MoveNext() && outer.Current == first, "Reset restarts the same captured bucket membership.");
    Check.That(inner.MoveNext() && inner.Current == first, "Nested bucket snapshots own separate buffers.");
    outer.Dispose();
    Check.That(inner.Current == first, "Disposing one bucket snapshot cannot invalidate another.");
    Check.That(!inner.MoveNext(), "Bucket snapshots do not include later additions.");
  }
}
