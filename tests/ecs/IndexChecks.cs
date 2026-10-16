using Archy;

namespace Archy.Tests;

internal static class IndexChecks {
  public static void Run() {
    PrimitiveIndexes();
    SparseAndRecycledEntries();
    GuardedComparers();
    using var store = StoreFactory.Create();
    var first = store.Create();
    var second = store.Create();
    first.Add(new IndexedIdentity(1));
    second.Add(new IndexedIdentity(2));
    var ids = store.ComponentIndex<IndexedIdentity, int>();

    Check.That(ids.TryGet(1, out var found) && found == first, "Generated unique properties select the right entity.");
    Check.That(!first.TryUpsert(new IndexedIdentity(2)), "Conflicting unique upserts fail.");
    Check.That(first.Get<IndexedIdentity>().Value == 1 && ids[1].Single() == first, "A conflict changes neither value nor index.");
    Check.Throws<MutationException>(() => first.Update(new IndexedIdentity(2)));
    first.Upsert(new IndexedIdentity(3));
    Check.That(ids[1].Count == 0 && ids[3].Single() == first, "Key updates remove old membership.");
    Check.That(ids.Values.Order().SequenceEqual([2, 3]), "Values contains only populated keys.");
    var bare = store.Create();
    Check.That(!bare.TryAdd(new IndexedIdentity(3)) && !bare.Has<IndexedIdentity>(), "Conflicting adds leave composition unchanged.");
    first.Add(new IndexedGroup { Value = 10 });
    second.Add(new IndexedGroup { Value = 10 });
    var groups = store.ComponentIndex<IndexedGroup, int>();

    Check.That(groups[10].Count == 2, "Generated fields support shared keys.");
    Check.Throws<InvalidOperationException>(() => groups.TryGet(10, out _));
    first.Remove<IndexedIdentity>();
    Check.That(ids[3].Count == 0, "Component removal unregisters its key.");
    second.Delete();
    Check.That(ids[2].Count == 0 && groups[10].Single() == first, "Deletion removes every indexed component.");
    Check.Throws<InvalidOperationException>(() => store.ComponentIndex<IndexedIdentity, string>());

    first.Add(new CheckedKey(1));
    Check.Throws<InvalidOperationException>(() => first.TryUpsert(new CheckedKey(-1)));
    Check.That(first.Get<CheckedKey>().Value == 1 && store.ComponentIndex<CheckedKey, int>()[1].Single() == first, "Computed-key exceptions preserve component data and index membership.");
    var nullStore = StoreFactory.Create();
    var nullView = nullStore.ComponentIndex<IndexedName, string>()[null!];
    Check.That(nullView.Count == 0, "Null index keys produce empty views.");
    nullStore.Dispose();
    Check.Throws<ObjectDisposedException>(() => _ = nullView.Count);

    var all = new List<Entity>();

    for (var i = 0; i < 100; i++) {
      var entity = store.Create();
      entity.Add(new IndexedGroup { Value = i % 3 });
      all.Add(entity);
    }

    foreach (var entity in all.Where((_, i) => i % 2 == 0)) entity.Remove<IndexedGroup>();

    Check.That(groups.Values.Where(value => value < 3).Sum(value => groups[value].Count) == 50, "Bucket swap removal preserves reverse positions.");
    foreach (var entity in all) entity.Delete();

    Check.That(groups.Values.SequenceEqual([10]), "Deleting a bucket's last entity removes its key.");
  }

  private static void PrimitiveIndexes() {
    using var store = StoreFactory.Create();
    var first = store.Create();
    var second = store.Create();
    first.Add(new PrimitiveLong(0));
    second.Add(new PrimitiveLong(0x100000001));
    var longs = store.ComponentIndex<PrimitiveLong, long>();
    Check.That(longs[0].Single() == first && longs[0x100000001].Single() == second,
      "Generated primitive indexes preserve full keys despite equal default hash codes.");
    Check.That(!second.TryUpsert(new PrimitiveLong(0)) && second.Get<PrimitiveLong>().Value == 0x100000001,
      "Specialized primitive unique conflicts leave values and membership intact.");
    var historical = longs[0];
    using var snapshot = historical.GetEnumerator();
    first.Update(new PrimitiveLong(long.MinValue));
    Check.That(snapshot.MoveNext() && snapshot.Current == first && historical.Count == 0
      && longs[long.MinValue].Single() == first, "Primitive updates preserve historical snapshots and live key views.");
    first.Add(new PrimitiveFloat(double.NaN));
    second.Add(new PrimitiveFloat(double.NaN));
    var floats = store.ComponentIndex<PrimitiveFloat, double>();
    Check.That(floats[double.NaN].Count == 2, "Primitive floating-point keys retain default NaN equality.");
    first.Update(new PrimitiveFloat(-0.0));
    second.Update(new PrimitiveFloat(0.0));
    Check.That(floats[0.0].Count == 2 && floats[double.NaN].Count == 0,
      "Signed zero and removed floating-point buckets retain default comparer behavior.");
    var guid = Guid.Parse("c0c02c39-8b43-45c1-8760-5c046c18c73f");
    first.Add(new PrimitiveGuid(guid));
    Check.That(store.ComponentIndex<PrimitiveGuid, Guid>()[guid].Single() == first,
      "Generated Guid indexes use their complete immutable key.");
    var graph = new PrefabGraphBuilder();
    graph.Create("first").Draft.Add(new PrimitiveLong(42));
    graph.Create("second").Draft.Add(new PrimitiveLong(42));
    Check.Throws<MutationException>(() => store.Instantiate(graph.Build()));
    Check.That(store.Count == 2 && longs[42].Count == 0,
      "Conflicting staged primitive keys roll back before construction publication.");
    var valid = new PrefabGraphBuilder();
    valid.Create("first").Draft.Add(new PrimitiveLong(42));
    valid.Create("second").Draft.Add(new PrimitiveLong(43));
    store.Instantiate(valid.Build());
    Check.That(longs[42].Count == 1 && longs[43].Count == 1 && longs.Values.Count == 4,
      "Primitive staging remains usable after a rejected construction.");
    Check.That(store.Query<PrimitiveLong>().ValueInRange<PrimitiveLong, long>(0, 43).Count == 2,
      "Range queries traverse all specialized primitive buckets.");
    first.Delete();
    second.Delete();
    Check.That(longs[long.MinValue].Count == 0 && floats[0].Count == 0
      && store.ComponentIndex<PrimitiveGuid, Guid>()[guid].Count == 0, "Primitive deletion removes every indexed membership.");
  }

  private static void GuardedComparers() {
    using var store = StoreFactory.Create();
    GuardedIndexComparer.Store = store;

    try {
      var first = store.Create();
      var second = store.Create();
      first.Add(new GuardedIndexKey(1));
      second.Add(new GuardedIndexKey(2));
      Check.That(!second.TryUpdate(new GuardedIndexKey(1)), "Custom-comparer unique conflicts leave paged reverse entries intact.");
      Check.Throws<InvalidOperationException>(() => first.Update(new GuardedIndexKey(99)));
      Check.That(first.Get<GuardedIndexKey>().Value == 1 && store.Count == 2,
        "Comparer callbacks run under preparation and failures preserve indexed component data.");
      first.Update(new GuardedIndexKey(3));
      second.Delete();
      Check.That(store.ComponentIndex<GuardedIndexKey, int>()[3].Single() == first,
        "Collision chains and reverse removal commit without invoking unguarded comparer callbacks.");
    } finally { GuardedIndexComparer.Store = null; }
  }

  private static void SparseAndRecycledEntries() {
    using var store = StoreFactory.Create();
    using var foreign = StoreFactory.Create();
    var entities = Enumerable.Range(0, 8193).Select(_ => store.Create()).ToArray();
    var index = store.ComponentIndex<IndexedGroup, int>();
    var selected = entities.Where((_, i) => i % 1025 == 0).ToArray();
    var old = new List<Entity>();

    foreach (var entity in selected) entity.Add(new IndexedGroup { Value = 1 });

    var view = index[1];
    using var snapshot = view.GetEnumerator();

    foreach (var entity in selected) {
      old.Add(entity);
      entity.Delete();
      var replacement = store.Create();
      Check.That(replacement.Id == entity.Id && replacement != entity, "Sparse indexed slots recycle with a new generation.");
      replacement.Add(new IndexedGroup { Value = 2 });
      Check.That(!entity.TryRemove<IndexedGroup>() && !entity.TryUpdate(new IndexedGroup { Value = 3 }),
        "Stale indexed operations cannot remove or rewrite a replacement entry.");
    }

    var captured = new List<Entity>();

    while (snapshot.MoveNext()) captured.Add(snapshot.Current);

    Check.That(captured.SequenceEqual(old) && view.Count == 0 && index[2].Count == selected.Length,
      "Sparse index pages preserve historical snapshots and live key views after generation reuse.");
    var unrelated = foreign.Create();
    unrelated.Add(new IndexedGroup { Value = 1 });
    unrelated.Remove<IndexedGroup>();
    Check.That(index[2].Count == selected.Length, "An index reverse table remains store-scoped.");

    foreach (var entity in index[2].ToArray()) entity.Update(new IndexedGroup { Value = 4 });
    foreach (var entity in index[4].ToArray().Where((_, i) => i % 2 == 0)) entity.Remove<IndexedGroup>();

    Check.That(index[2].Count == 0 && index[4].Count == selected.Length / 2,
      "Reverse positions repair swaps across sparse pages and reused buckets.");
  }
}

public readonly partial record struct IndexedIdentity([property: Indexed(Unique = true)] int Value) : IComponent;

internal partial struct IndexedGroup : IComponent {
  [Indexed] public int Value;
}

internal readonly partial record struct CheckedKey(int Value) : IComponent {
  [Indexed] private int Key() => this.Value >= 0 ? this.Value : throw new InvalidOperationException("Negative key.");
}

internal readonly partial record struct IndexedName([property: Indexed] string Value) : IComponent;

internal readonly partial record struct GuardedIndexKey(
  [property: Indexed(Unique = true, Comparer = typeof(GuardedIndexComparer))] int Value
) : IComponent;

internal sealed class GuardedIndexComparer : IEqualityComparer<int> {
  public static EntityStore? Store { get; set; }
  public bool Equals(int left, int right) {
    Check.Throws<InvalidOperationException>(() => Store!.Create());

    return left == right;
  }
  public int GetHashCode(int value) {
    Check.Throws<InvalidOperationException>(() => Store!.Create());

    return value == 99 ? throw new InvalidOperationException("comparer failed") : 0;
  }
}

internal readonly partial record struct PrimitiveLong([property: Indexed(Unique = true)] long Value) : IComponent;
internal readonly partial record struct PrimitiveFloat([property: Indexed] double Value) : IComponent;
internal readonly partial record struct PrimitiveGuid([property: Indexed(Unique = true)] Guid Value) : IComponent;
