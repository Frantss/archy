using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Archy;

namespace Archy.Tests;

internal static class ChunkChecks {
  public static void Run() {
    foreach (var count in new[] { 15, 16, 31, 32, 63, 64, 123, 124, 125, 255, 256, 511, 512, 1023, 1024, 5000 }) {
      using var store = StoreFactory.Create();
      var entities = new Entity[count];

      for (var pass = 0; pass < 2; pass++) {
        for (var i = 0; i < count; i++) {
          var entity = entities[i] = store.Create();
          entity.Add(new WideChunkValue(i + pass));
          entity.Add(new EventBag([i + pass]));
          entity.AddTag<QueryVisible>();
        }

        for (var i = 0; i < count; i += 2) entities[i].Delete();

        Check.That(store.Query<WideChunkValue, EventBag>().Count == count / 2,
          "Variable-capacity queries count swap-compacted rows across boundaries.");
        store.Query<WideChunkValue, EventBag>().ForEachEntity((wide, bag, entity) => {
          Check.That(wide.Value == bag.Values[0] && entity.HasTag<QueryVisible>(),
            "Compaction preserves aligned wide, managed, and tag data.");
          bag.Values[0] = -1;
          var owned = entity.Get<EventBag>();
          Check.That(owned.Values[0] == wide.Value, "Variable columns retain copy-policy reads.");
        });

        for (var i = 1; i < count; i += 2) entities[i].Delete();

        Check.That(store.Count == 0, "Multi-chunk shrink/regrow releases all live rows.");
      }
    }

    using var retained = StoreFactory.Create();
    var payload = FillAndClear(retained);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check.That(!payload.IsAlive, "Retained spare chunks clear managed payload references.");
    GC.KeepAlive(retained);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static WeakReference FillAndClear(EntityStore store) {
    var token = new JournalToken();
    var weak = new WeakReference(token);
    var entities = new Entity[5000];

    for (var i = 0; i < entities.Length; i++) {
      entities[i] = store.Create();
      entities[i].Add(new JournalPayload(token));
    }

    foreach (var entity in entities) entity.Delete();

    return weak;
  }
}

[StructLayout(LayoutKind.Sequential, Size = 128)]
internal readonly record struct WideChunkValue(long Value) : IComponent;
