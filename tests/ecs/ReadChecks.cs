using Archy;

namespace Archy.Tests;

internal static class ReadChecks {
  public static void Run() {
    using var store = StoreFactory.Create();
    var entity = store.Create();
    entity.Add(new QueryPosition(7));
    entity.Add(new EventBag([8]));
    Check.That(
      entity.TryGet<QueryPosition>(out var value) && value.Value == 7,
      "Validated reads return the stored value."
    );
    Check.That(!entity.TryGet<WideChunkValue>(out var missing) && missing == default,
      "Validated reads of absent columns return false and a default value.");
    Check.Throws<InvalidOperationException>(
      () => Task.Run(() => entity.TryGet<QueryPosition>(out _)).GetAwaiter().GetResult()
    );
    Check.Throws<InvalidOperationException>(
      () => Task.Run(() => entity.TryGet<EventBag>(out _)).GetAwaiter().GetResult()
    );
    Check.That(entity.TryGet<EventBag>(out var bag), "Policy-owned reads use the prepared fallback.");
    bag.Values[0] = 99;
    var owned = entity.Get<EventBag>();
    Check.That(owned.Values[0] == 8, "Prepared reads retain independent ownership.");
    entity.Delete();
    var replacement = store.Create();
    replacement.Add(new QueryPosition(9));
    Check.That(!entity.TryGet<QueryPosition>(out var stale) && stale == default,
      "Recycled slots reject stale generations and clear out values.");
    Check.Throws<MutationException>(() => entity.Get<QueryPosition>());
    using var commands = store.CreateCommandBuffer();
    var reserved = commands.Create();
    Check.That(!reserved.TryGet<QueryPosition>(out _), "Reserved slots cannot be read as live entities.");
    commands.Dispose();
    store.Dispose();
    Check.That(!replacement.TryGet<QueryPosition>(out _), "Disposed stores reject validated reads.");
  }
}
