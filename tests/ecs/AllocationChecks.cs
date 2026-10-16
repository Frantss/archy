using Archy;

namespace Archy.Tests;

internal static class AllocationChecks {
  public static void Run() {
    FreshPopulation();
    DirectQueries();
    Movement();
    Movement(observed: true);
    IndexLookups();
    EntityChurn();
    IndexedWrites();
  }

  private static void FreshPopulation() {
    var before = GC.GetAllocatedBytesForCurrentThread();
    using var store = new EntityStore();

    for (var i = 0; i < 4096; i++) _ = store.Create();

    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    const int budget = 128;
    Check.That(store.Count == 4096 && bytes < 4096 * budget,
      $"Fresh entity population allocates less than {budget} bytes/entity; observed {bytes / 4096.0}.");
  }

  private static void DirectQueries() {
    using var store = new EntityStore();

    for (var i = 0; i < 600; i++) store.Create().Add(new QueryPosition(i));

    var query = store.Query<QueryPosition>().FreezeFilter();
    var sum = 0L;
    Action<QueryPosition, Entity> consume = (value, _) => sum += value.Value;

    void Tick() {
      for (var i = 0; i < 256; i++) sum += query.Count;

      query.ForEachEntity(consume);
    }

    for (var i = 0; i < 32; i++) Tick();

    var before = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < 8; i++) Tick();

    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check.That(allocated == 0, $"Warm direct counts and typed callbacks allocate zero bytes; observed {allocated}.");
    Check.That(sum > 0, "Direct query allocation checks consume every result.");
  }

  private static void Movement(bool observed = false) {
    using var store = new EntityStore();

    for (var i = 0; i < 2048; i++) {
      var entity = store.Create();
      entity.Add(new QueryPosition(i));
    }

    long observedValues = 0;
    using var subscription = observed ? store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => observedValues += item.NewValue.Value) : null;
    using var metadata = observed ? store.Events.Subscribe<EntityModified>(_ => observedValues++) : null;
    var query = store.Query<QueryPosition>().FreezeFilter();

    void Tick() {
      foreach (var entity in query.Entities) {
        var value = entity.Get<QueryPosition>();
        entity.Update(value with { Value = value.Value + 1 });
      }

      store.Events.Dispatch();
    }

    for (var i = 0; i < 32; i++) Tick();

    var before = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < 8; i++) Tick();

    var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
    Check.That(!observed || observedValues > 0, "Observed allocation checks deliver and consume callbacks.");
    Check.That(bytesPerTick <= 512, $"2048-entity updates (observed={observed}) allocate at most 512 bytes/tick; observed {bytesPerTick} bytes.");
  }

  private static void IndexLookups() {
    using var store = new EntityStore();

    for (var i = 0; i < 2048; i++) {
      var entity = store.Create();
      entity.Add(new IndexedIdentity(i));
      entity.Add(new IndexedGroup { Value = i % 64 });
    }

    var identities = store.ComponentIndex<IndexedIdentity, int>();
    var groups = store.ComponentIndex<IndexedGroup, int>();
    long checksum = 0;

    void Tick() {
      for (var i = 0; i < 256; i++) {
        foreach (var entity in identities[i]) checksum += entity.Id;
        foreach (var entity in groups[i % 64]) checksum += entity.Id;
        _ = groups[i % 64].Count;
        _ = identities.TryGet(i, out _);
      }
    }

    for (var i = 0; i < 32; i++) Tick();

    var before = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < 8; i++) Tick();

    var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
    Check.That(bytesPerTick <= 40960, $"Repeated populated index reads allocate at most 40 KiB/tick; observed {bytesPerTick} bytes.");
    Check.That(checksum > 0, "Index allocation checks consume their results.");
  }

  private static void EntityChurn() {
    using var store = new EntityStore();
    var nextId = 0;
    var checksum = 0;

    void Tick() {
      for (var i = 0; i < 64; i++) {
        var entity = store.Create();
        entity.Add(new QueryPosition(i));
        entity.Add(new IndexedIdentity(nextId++));
        checksum += entity.Get<QueryPosition>().Value;
        entity.Delete();
      }

      store.Events.Dispatch();
    }

    for (var i = 0; i < 32; i++) Tick();

    var before = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < 8; i++) Tick();

    var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
    Check.That(bytesPerTick == 0, $"Warm 64-entity fresh-key churn allocates zero bytes; observed {bytesPerTick} bytes.");
    Check.That(checksum > 0 && store.Count == 0, "Entity churn checks consume values and release every entity.");
  }

  private static void IndexedWrites() {
    using var store = new EntityStore();
    var entities = Enumerable.Range(0, 256).Select(_ => store.Create()).ToArray();

    foreach (var entity in entities) entity.Add(new IndexedGroup { Value = 0 });

    var step = 0;

    void Tick() {
      var key = ++step % 2;

      foreach (var entity in entities) entity.Update(new IndexedGroup { Value = key });
    }

    for (var i = 0; i < 32; i++) Tick();

    var before = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < 8; i++) Tick();

    var bytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
    Check.That(bytesPerTick <= 4096, $"256 indexed writes allocate at most 4 KiB/tick; observed {bytesPerTick} bytes.");
  }
}
