using System.Runtime.CompilerServices;
using Archy;

namespace Archy.Tests;

internal static class DisposalChecks {
  public static void Run() {
    var (entity, payload, retained) = DisposeWithLivePayload();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check.That(
      !payload.IsAlive,
      "Retained handles, queries, escaped chunks, indexes and event hubs release disposed component data."
    );
    Check.That(!entity.IsAlive && !entity.TryGet<JournalPayload>(out _), "Disposed handles remain invalid.");
    GC.KeepAlive(retained);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static (Entity Entity, WeakReference Payload, object[] Retained) DisposeWithLivePayload() {
    using var store = new EntityStore();
    var entity = store.Create();
    var token = new JournalToken();
    entity.Add(new JournalPayload(token));
    entity.Add(new IndexedGroup { Value = 1 });
    var query = store.Query<JournalPayload>().FreezeFilter();
    _ = query.Count;
    var escaped = query.Chunks.First().Item1;
    var index = store.ComponentIndex<IndexedGroup, int>()[1];
    var events = store.Events;
    _ = events.Subscribe<ComponentUpdated<JournalPayload>>(_ => {});
    var historical = store.Create();
    historical.Add(new QueryPosition(9));
    EntitySnapshot? snapshot = null;
    _ = historical.Events.Subscribe<EntityDeleted>(item => snapshot = item.Snapshot);
    historical.Delete();
    events.Dispatch();
    entity.Update(new JournalPayload(token));
    store.Dispose();
    Check.That(snapshot!.Get<QueryPosition>().Value == 9, "Owned snapshots survive disposal cleanup.");

    return (entity, new WeakReference(token), [entity, query, escaped, index, events, snapshot]);
  }
}
