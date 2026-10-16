using Archy;

namespace Archy.Tests;

internal static class ReservationChecks {
  public static void Run() {
    InterleavedOwners();
    using var store = new EntityStore();
    using var commands = store.CreateCommandBuffer();
    using var otherBuffer = store.CreateCommandBuffer();
    var root = store.Create();
    root.Add(new QueryPosition(1));
    Entity pending = default;
    var creations = 0;

    foreach (var _ in store.Query<QueryPosition>().Entities) {
      pending = commands.Create();
      pending.Events.Subscribe<EntityCreated>(item => { if (item.Entity == pending) creations++; });
      commands.Add(pending, new IndexedIdentity(15));
      Check.That(!pending.IsAlive && store.Count == 1, "Query-time reservation allocates no live row.");
      Check.That(!pending.TryUpsert(new QueryPosition(1)), "Direct mutations cannot use pending entities.");
      Check.Throws<MutationException>(() => otherBuffer.Upsert(pending, new QueryPosition(1)));
    }

    commands.Playback();
    store.Events.Dispatch();
    Check.That(pending.IsAlive && creations == 1 && store.ComponentIndex<IndexedIdentity, int>()[15].Single() == pending, "Creation activates before dependent commands and supports reserved subscriptions and indexes.");
    var canceled = commands.Create();
    var canceledCallbacks = 0;
    canceled.Events.Subscribe<EntityCreated>(_ => canceledCallbacks++);
    commands.Clear();
    var reused = commands.Create();
    Check.That(reused.Id == canceled.Id && reused.Generation != canceled.Generation && !canceled.IsAlive, "Discarded reservations advance generation before slot reuse.");
    commands.Playback();
    store.Events.Dispatch();
    Check.That(canceledCallbacks == 0, "Cancellation detaches reserved subscriptions.");
    var prefix = commands.Create();
    commands.Add(prefix, new QueryPosition(3));
    commands.Add(prefix, new QueryPosition(4));
    var suffix = commands.Create();
    var suffixTicket = commands.TryUpsert(suffix, new QueryPosition(5));

    try { commands.Playback(); throw new InvalidOperationException("Expected failure."); }
    catch (PlaybackException error) {
      Check.That(error.CommandIndex == 2 && error.Result.Outcome(suffixTicket) == CommandOutcome.Canceled, "Failed streams cancel all unexecuted creation dependencies.");
    }

    Check.That(prefix.IsAlive && prefix.Get<QueryPosition>().Value == 3 && !suffix.IsAlive, "Committed creations survive failure while unexecuted reservations do not.");
    var afterFailure = store.Create();
    Check.That(afterFailure.Id == suffix.Id && afterFailure.Generation != suffix.Generation, "Failed playback releases pending slots safely.");
    Entity disposed;
    using (var temporary = store.CreateCommandBuffer()) disposed = temporary.Create();

    Check.That(!disposed.IsAlive && !disposed.TryDelete(), "Disposing a buffer cancels pending entities.");
    Check.That(commands.Count == 0, "Creation streams are consumed.");
  }
  private static void InterleavedOwners() {
    using var store = new EntityStore();
    using var first = store.CreateCommandBuffer();
    using var second = store.CreateCommandBuffer();
    var kept = new List<Entity>();
    var canceled = new List<Entity>();
    for (var i = 0; i < 1024; i++) {
      kept.Add(first.Create());
      canceled.Add(second.Create());
      if (i % 128 == 0) Check.Throws<MutationException>(() => second.TryDelete(kept[^1]));
    }
    second.Clear();
    first.Playback();
    Check.That(store.Count == 1024 && kept.All(entity => entity.IsAlive) && canceled.All(entity => !entity.IsAlive),
      "Interleaved reservation owners remain isolated across metadata pages and cancellation.");
    for (var i = 0; i < 1024; i++) {
      var live = store.Create();
      live.Add(new QueryPosition(i));
    }
    Check.That(store.Count == 2048 && kept.All(entity => entity.IsAlive) && canceled.All(entity => !entity.IsAlive),
      "Canceled reservation IDs can be reused while activated handles retain their locations.");
  }

}
