using System.Runtime.CompilerServices;
using Archy;

namespace Archy.Tests;

internal static class JournalChecks {
  public static void Run() {
    BurstAndNextBatch();
    AlternatingBatches();
    RetainedSnapshots();
    using var store = new EntityStore();
    var payload = FailedDeletion(store);
    var construction = FailedConstruction(store);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check.That(!payload.IsAlive && !construction.IsAlive,
      "Failed deletion and construction release uncommitted journal payloads without a dispatch.");
  }

  private static void RetainedSnapshots() {
    var snapshot = SnapshotAfterDisposal();
    Check.That(snapshot.HasTag<QueryVisible>() && snapshot.Get<EventBag>().Values[0] == 1,
      "Snapshots retain tags and copied values after store disposal.");
    snapshot.Get<EventBag>().Values[0] = 9;
    Check.That(snapshot.Get<EventBag>().Values[0] == 1, "Snapshot reads preserve copy ownership.");
    Check.That(!snapshot.Has<JournalFailure>() && !snapshot.HasTag<UnknownSnapshotTag>(),
      "Snapshot membership does not initialize absent types.");
    Check.Throws<MutationException>(() => snapshot.Get<JournalFailure>());
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static EntitySnapshot SnapshotAfterDisposal() {
    using var store = new EntityStore();
    var first = store.Create();
    first.Add(new JournalPayload(new JournalToken()));
    first.Add(new EventBag([1]));
    first.AddTag<QueryVisible>();
    EntitySnapshot? snapshot = null;
    using var subscription = store.Events.Subscribe<EntityDeleted>(item => snapshot = item.Snapshot);
    first.Delete();
    store.Events.Dispatch();

    return snapshot!;
  }

  private static void AlternatingBatches() {
    using var store = new EntityStore();
    var entity = store.Create();
    var delivered = 0;
    var storeDelivered = 0;
    using var other = store.Events.Subscribe<StoreEvent<JournalOther>>(_ => storeDelivered++);
    using var global = store.Events.Subscribe<CustomEvent<JournalMessage>>(item => {
      delivered++;
      if (item.Data.Value < 100) entity.Emit(new JournalMessage(item.Data.Value + 1));
      if (item.Data.Value == 0) store.Emit(new JournalOther());
    });
    entity.Emit(new JournalMessage(0));
    for (var i = 0; i <= 100; i++) store.Events.Dispatch();
    Check.That(delivered == 101 && storeDelivered == 1,
      "Repeated handler emissions and newly initialized typed journals survive alternating batches.");
    store.Events.Dispatch();
    Check.That(delivered == 101, "Drained batches do not redeliver released entries.");
  }

  private static void BurstAndNextBatch() {
    using var store = new EntityStore();
    var entity = store.Create();
    var values = new List<int>();
    long sequence = 0;
    using var global = store.Events.Subscribe<CustomEvent<JournalMessage>>(item => {
      Check.That(item.Sequence > sequence, "Mixed journal batches retain sequence order.");
      sequence = item.Sequence;
      values.Add(item.Data.Value);

      if (item.Data.Value < 32) entity.Emit(new JournalMessage(10000 + item.Data.Value));
    });

    for (var i = 0; i < 10000; i++) entity.Emit(new JournalMessage(i));

    store.Events.Dispatch();
    Check.That(values.SequenceEqual(Enumerable.Range(0, 10000)), "Large journal batches deliver the original FIFO only.");
    store.Events.Dispatch();
    Check.That(values.TakeLast(32).SequenceEqual(Enumerable.Range(10000, 32)),
      "Handler emissions survive page growth and free-slot reuse for the next batch.");
    values.Clear();

    for (var i = 0; i < 100; i++) entity.Emit(new JournalMessage(20000 + i));

    store.Events.Dispatch();
    Check.That(values.SequenceEqual(Enumerable.Range(20000, 100)), "Journal pages remain usable after burst capacity is trimmed.");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference FailedConstruction(EntityStore store) {
    var token = new JournalToken();
    var weak = new WeakReference(token);
    var builder = new PrefabGraphBuilder();
    builder.Create("payload").Draft.Add(new JournalPayload(token));
    builder.Create("failure").Draft.Add(new JournalFailure(1));
    var graph = builder.Build();
    using var subscription = store.Events.Subscribe<ComponentAdded<JournalPayload>>(_ => {});
    var count = store.Count;
    JournalFailureCopy.Fail = true;

    try { Check.Throws<InvalidOperationException>(() => store.Instantiate(graph)); }
    finally { JournalFailureCopy.Fail = false; }

    Check.That(store.Count == count, "Failed graph construction does not publish journal-backed nodes.");

    return weak;
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference FailedDeletion(EntityStore store) {
    var entity = store.Create();
    var token = new JournalToken();
    var weak = new WeakReference(token);
    entity.Add(new JournalPayload(token));
    entity.Add(new JournalFailure(1));
    using var payload = store.Events.Subscribe<ComponentRemoved<JournalPayload>>(_ => {});
    using var failure = store.Events.Subscribe<ComponentRemoved<JournalFailure>>(_ => {});
    JournalFailureCopy.Fail = true;

    try { Check.Throws<InvalidOperationException>(entity.Delete); }
    finally { JournalFailureCopy.Fail = false; }

    Check.That(entity.IsAlive && entity.Has<JournalPayload>(), "Failed event preparation leaves the entity intact.");
    payload.Dispose();
    failure.Dispose();
    entity.Remove<JournalPayload>();
    entity.Remove<JournalFailure>();

    return weak;
  }
}

internal readonly record struct JournalMessage(int Value) : ICustomEvent;
[ImmutableComponentValue]
internal sealed class JournalToken;
internal readonly record struct JournalPayload(JournalToken Value) : IComponent;
[ComponentCopy(typeof(JournalFailureCopy))]
internal readonly record struct JournalFailure(int Value) : IComponent;
internal sealed class JournalFailureCopy : IComponentCopy<JournalFailure> {
  public static bool Fail { get; set; }

  public JournalFailure Copy(in JournalFailure value) => Fail ? throw new InvalidOperationException("copy failed") : value;
}

internal readonly record struct UnknownSnapshotTag(int Value) : ITag;

internal readonly record struct JournalOther : ICustomEvent;
