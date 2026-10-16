using Archy;

namespace Archy.Tests;

internal static class EntityChecks {
  private static void MetadataGrowth() {
    using var store = new EntityStore();
    using var commands = store.CreateCommandBuffer();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    var reserved = new List<Entity>();
    store.Query<QueryPosition>().ForEachEntity((value, current) => {
      for (var i = 0; i < 4096; i++) reserved.Add(commands.Create());

      current.Update(value with { Value = 2 });
      Check.That(store.Query<QueryPosition>().Count == 1, "Reservation growth leaves active query metadata intact.");
    });
    commands.Playback();
    Check.That(store.Count == 4097 && reserved.All(item => item.IsAlive), "Reservations across metadata pages activate normally.");
    using var subscription = entity.Events.Subscribe<EntityModified>(_ => {
      for (var i = 0; i < 4096; i++) reserved.Add(commands.Create());
    });
    entity.Update(new QueryPosition(3));
    store.Events.Dispatch();
    Check.That(entity.Get<QueryPosition>().Value == 3 && store.Count == 4097,
      "Dispatch-time reservations grow metadata without activating rows.");
    var canceled = reserved[^1];
    commands.Clear();
    var replacement = store.Create();
    Check.That(!canceled.IsAlive && replacement.IsAlive && replacement != canceled,
      "Canceling grown reservations preserves generation identity.");
  }

  public static void Run() {
    MetadataGrowth();
    using var store = new EntityStore();
    using var other = new EntityStore();
    var entity = store.Create();
    var foreign = other.Create();
    var copy = entity;

    Check.That(entity.IsAlive && copy == entity && store.Count == 1, "Creation returns a live value handle.");
    Check.That(entity != foreign, "Entity equality includes store identity.");
    Check.That(!other.IsAlive(entity) && !other.TryDelete(entity), "Foreign handles do not access this store.");
    Check.Throws<MutationException>(() => other.Delete(entity));
    entity.Delete();
    Check.That(!entity.IsAlive && !copy.IsAlive && store.Count == 0, "Deletion invalidates every handle copy.");
    Check.That(!entity.TryDelete(), "Repeated try-delete is an ordinary failure.");
    Check.Throws<MutationException>(entity.Delete);
    var replacement = store.Create();

    Check.That(replacement.Id == entity.Id && replacement != entity, "Reusing a slot changes its generation.");
    Check.That(!entity.IsAlive && replacement.IsAlive, "An old handle cannot become alive again.");

    for (var i = 0; i < 1_000; i++) {
      var previous = replacement;
      replacement.Delete();
      replacement = store.Create();
      Check.That(!previous.IsAlive && replacement != previous, "Generation reuse rejects stale handles.");
    }

    var empty = default(Entity);

    Check.That(!empty.IsAlive && !empty.TryDelete(), "Default handles are empty.");
    Check.Throws<MutationException>(empty.Delete);
    Check.Throws<InvalidOperationException>(() => Task.Run(() => store.Create()).GetAwaiter().GetResult());
    store.Dispose();
    Check.That(!replacement.IsAlive && !replacement.TryDelete(), "Store disposal invalidates all handles.");
    Check.Throws<ObjectDisposedException>(() => store.Create());
    store.Dispose();
  }
}
