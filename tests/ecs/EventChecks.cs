using System.Runtime.CompilerServices;
using Archy;

namespace Archy.Tests;

internal static class EventChecks {
  private static void ImmutableUpdates() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    var history = new List<(int Old, int Next)>();
    var order = new List<string>();
    using var global = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => {
      history.Add((item.OldValue.Value, item.NewValue.Value));
      order.Add("global");
    });
    using var local = entity.Events.Subscribe<ComponentUpdated<QueryPosition>>(_ => order.Add("local"));
    using var metadata = store.Events.Subscribe<EntityModified>(_ => order.Add("modified"));
    entity.Update(new QueryPosition(2));
    var canceled = entity.Events.Subscribe<ComponentUpdated<QueryPosition>>(_ => throw new InvalidOperationException("canceled"));
    entity.Upsert(new QueryPosition(2));
    canceled.Dispose();
    Check.That(!entity.TryAdd(new QueryPosition(3)), "Observed immutable add remains strict.");
    store.Query<QueryPosition>().ForEachEntity((value, current) => current.Update(value with { Value = 3 }));
    Check.That(history.Count == 0, "Specialized observed updates stay deferred.");
    store.Events.Dispatch();
    Check.That(history.SequenceEqual([(1, 2), (2, 2), (2, 3)]), "Specialized updates own each old/new value, including equal updates.");
    Check.That(string.Join(",", order) == "global,local,modified,global,local,modified,global,local,modified",
      "Specialized updates preserve scope and specific-before-modified order.");
  }

  public static void Run() {
    ImmutableUpdates();
    ChangingRoutes();
    ReleasedRoutes();
    ImmutableSubscriptions();
    ReusedDeliveries();
    using var store = new EntityStore();
    var log = new List<string>();
    var sequences = new List<long>();
    using var created = store.Events.Subscribe<EntityCreated>(item => {
      log.Add("created"); sequences.Add(item.Sequence);
      Check.That(item.Entity.Store == store && !item.Snapshot.Has<QueryPosition>(), "Creation captures its construction state.");
    });
    var entity = store.Create();
    var scope = entity.Events;
    using var added = store.Events.Subscribe<ComponentAdded<QueryPosition>>(item => { log.Add("store-add"); sequences.Add(item.Sequence); });
    using var addedSecond = store.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => log.Add("store-add-2"));
    using var local = scope.Subscribe<ComponentAdded<QueryPosition>>(item => { log.Add("local-add"); Check.That(item.Entity == entity, "Both scopes carry full identity."); });
    using var modified = store.Events.Subscribe<EntityModified>(item => { log.Add("modified"); sequences.Add(item.Sequence); });
    using var updated = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => {
      log.Add($"update:{item.OldValue.Value}:{item.NewValue.Value}"); sequences.Add(item.Sequence);
    });
    entity.Upsert(new QueryPosition(1));
    Check.That(!entity.TryAdd(new QueryPosition(5)), "Failed tries leave event stream unchanged.");
    entity.Upsert(new QueryPosition(2));
    entity.Update(new QueryPosition(2));
    var lateCalls = 0;
    using var late = scope.Subscribe<ComponentAdded<QueryPosition>>(_ => lateCalls++);
    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["created", "store-add", "store-add-2", "local-add", "modified", "update:1:2", "modified", "update:2:2", "modified"]), "Specific events precede modification, store callbacks precede local callbacks, and equal updates emit.");
    Check.That(lateCalls == 0 && sequences.SequenceEqual(sequences.Order()) && sequences.Distinct().Count() == sequences.Count, "Captured subscriptions have no replay and sequences order event records.");
    var canceledCalls = 0;
    var canceled = scope.Subscribe<TagAdded<QueryVisible>>(_ => canceledCalls++);
    entity.AddTag<QueryVisible>();
    canceled.Dispose();
    store.Events.Dispatch();
    Check.That(canceledCalls == 0, "Unsubscribe suppresses pending callbacks.");
    log.Clear();
    using var removal = scope.Subscribe<ComponentRemoved<QueryPosition>>(item => {
      log.Add("removed"); Check.That(item.OldValue.Value == 2, "Removal owns the final old value.");
    });
    using var tagRemoval = scope.Subscribe<TagRemoved<QueryVisible>>(_ => log.Add("tag-removed"));
    using var deleted = scope.Subscribe<EntityDeleted>(item => {
      log.Add("deleted");
      Check.That(!item.Entity.IsAlive && item.Snapshot.Get<QueryPosition>().Value == 2 && item.Snapshot.HasTag<QueryVisible>(), "Deletion owns final state for a dead historical handle.");
    });
    entity.Delete();
    store.Events.Dispatch();
    Check.That(log.Last() == "deleted" && log.Contains("removed") && log.Contains("tag-removed") && !log.Contains("modified"), "Deletion emits removals then deletion without a modification event.");
    var replacement = store.Create();
    replacement.Add(new QueryPosition(3));
    store.Events.Dispatch();
    Check.That(lateCalls == 0 && replacement.Id == entity.Id && replacement != entity, "Slot recycling never inherits subscriptions.");
    Check.Throws<MutationException>(() => scope.Subscribe<EntityModified>(_ => {}));

    var bagEntity = store.Create();
    bagEntity.Add(new EventBag([1]));
    ComponentUpdated<EventBag> retained = default;
    using var bagFirst = bagEntity.Events.Subscribe<ComponentUpdated<EventBag>>(item => { item.NewValue.Values[0] = 90; });
    using var bagSecond = bagEntity.Events.Subscribe<ComponentUpdated<EventBag>>(item => retained = item);
    var input = new[] { 2 };
    bagEntity.Update(new EventBag(input));
    input[0] = 30;
    bagEntity.Update(new EventBag([3]));
    store.Events.Dispatch();
    Check.That(retained.OldValue.Values[0] == 2 && retained.NewValue.Values[0] == 3 && bagEntity.Get<EventBag>().Values[0] == 3, "Old/new payloads and each callback own copies independent of input, state, and other callbacks.");
    var errorsEntity = store.Create();
    var continued = 0;
    using var bad = errorsEntity.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => errorsEntity.Delete());
    using var recursive = errorsEntity.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => store.Events.Dispatch());
    using var good = errorsEntity.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => continued++);
    errorsEntity.Add(new QueryPosition(4));
    Check.Throws<AggregateException>(() => store.Events.Dispatch());
    Check.That(continued == 1 && errorsEntity.IsAlive && errorsEntity.Has<QueryPosition>(), "Handler errors aggregate after other handlers and preserve committed data.");
    store.Events.Dispatch();
    Check.That(continued == 1, "Dispatched batches are consumed even after handler failures.");
  }

  private static void ReleasedRoutes() {
    using var store = new EntityStore();
    var payload = PopulateAndCancelRoute(store);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check.That(!payload.IsAlive, "Invalidated observer caches release canceled handler captures while the store remains alive.");
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference PopulateAndCancelRoute(EntityStore store) {
    var payload = new object();
    var entity = store.Create();
    entity.Add(new QueryPosition(0));
    using var subscription = entity.Events.Subscribe<ComponentUpdated<QueryPosition>>(_ => GC.KeepAlive(payload));
    entity.Update(new QueryPosition(1));
    store.Events.Dispatch();

    return new(payload);
  }

  private static void ChangingRoutes() {
    using var store = new EntityStore();
    var first = store.Create();
    var second = store.Create();
    first.Add(new QueryPosition(0));
    second.Add(new QueryPosition(10));
    var global = new List<(Entity Entity, int Value)>();
    var local = new List<int>();
    using var metadata = store.Events.Subscribe<EntityModified>(_ => {});
    first.Update(new QueryPosition(1));
    using var subscription = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item =>
      global.Add((item.Entity, item.NewValue.Value)));
    second.Update(new QueryPosition(11));
    using var scoped = first.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => local.Add(item.NewValue.Value));
    first.Update(new QueryPosition(2));
    second.Update(new QueryPosition(12));
    scoped.Dispose();
    first.Update(new QueryPosition(3));
    store.Events.Dispatch();
    Check.That(global.SequenceEqual([(second, 11), (first, 2), (second, 12), (first, 3)]) && local.Count == 0,
      "Cached observer routes invalidate subscriptions without replay and suppress canceled local deliveries.");
    var next = new List<int>();
    using var replacement = first.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => next.Add(item.NewValue.Value));
    store.Query<QueryPosition>().ForEachEntity((value, entity) => entity.Update(value with { Value = value.Value + 1 }));
    store.Events.Dispatch();
    Check.That(next.SequenceEqual([4]) && global.TakeLast(2).Select(item => item.Value).Order().SequenceEqual([4, 13]),
      "Versioned routes retain per-entity routing while traversals write successive rows.");
  }

  private static void ImmutableSubscriptions() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new QueryPosition(0));
    var global = new List<int>();
    var local = new List<int>();
    var custom = 0;
    IDisposable? subscription = null;
    using var observer = store.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => {
      global.Add(item.NewValue.Value);
      subscription?.Dispose();
      entity.Emit(new JournalMessage(item.NewValue.Value));
      throw new InvalidOperationException("immutable handler");
    });
    using var message = entity.Events.Subscribe<CustomEvent<JournalMessage>>(_ => custom++);
    store.Query<QueryPosition>().ForEachEntity((_, current) => {
      current.Update(new QueryPosition(1));
      subscription = current.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => local.Add(item.NewValue.Value));
      current.Update(new QueryPosition(2));
    });
    Check.Throws<AggregateException>(() => store.Events.Dispatch());
    Check.That(global.SequenceEqual([1, 2]) && local.Count == 0 && custom == 0,
      "Immutable envelopes capture per-write subscriptions, honor cancellation and aggregate handler failures.");
    store.Events.Dispatch();
    Check.That(custom == 2, "Immutable handlers can emit custom events into the next dispatch batch.");
  }

  private static void ReusedDeliveries() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new EventBag([1]));
    var retained = new List<ComponentUpdated<EventBag>>();
    using var first = store.Events.Subscribe<ComponentUpdated<EventBag>>(item => retained.Add(item));
    var canceledCalls = 0;
    var canceled = entity.Events.Subscribe<ComponentUpdated<EventBag>>(_ => canceledCalls++);
    IDisposable? late = null;
    var lateCalls = 0;
    using var control = store.Events.Subscribe<ComponentUpdated<EventBag>>(_ => {
      canceled.Dispose();
      late ??= entity.Events.Subscribe<ComponentUpdated<EventBag>>(_ => lateCalls++);
    });
    entity.Update(new EventBag([2]));
    entity.Update(new EventBag([3]));
    store.Events.Dispatch();
    Check.That(canceledCalls == 0 && lateCalls == 0, "Subscription edits during dispatch preserve captured membership and suppress canceled callbacks.");
    entity.Update(new EventBag([4]));
    store.Events.Dispatch();
    Check.That(lateCalls == 1 && retained.Count == 3 && retained.Select(item => item.Sequence).Distinct().Count() == 3,
      "Reused event records deliver fresh sequences and subsequent subscription snapshots.");
    Check.That(retained[0].OldValue.Values[0] == 1 && retained[0].NewValue.Values[0] == 2
      && retained[1].OldValue.Values[0] == 2 && retained[2].NewValue.Values[0] == 4,
      "Retained payloads survive internal record reuse across dispatches.");
    late?.Dispose();
  }
}

[ComponentCopy(typeof(EventBagCopy))]
internal readonly record struct EventBag(int[] Values) : IComponent;
internal sealed class EventBagCopy : IComponentCopy<EventBag> {
  public EventBag Copy(in EventBag value) => new([..value.Values]);
}
