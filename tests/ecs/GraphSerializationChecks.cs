using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class GraphSerializationChecks {
  public static void Run() {
    using var store = StoreFactory.Create();
    var first = store.Create();
    var second = store.Create();
    var self = store.Create();
    first.Add(new SavedIdentity(1));
    second.Add(new SavedIdentity(2));
    self.Add(new SavedIdentity(3));
    first.Add(new SavedOwner(second));
    second.Add(new SavedOwner(first));
    self.Add(new SavedOwner(self));
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    var json = serializer.WriteStore(store);
    using var restored = serializer.ReadStore(json, StoreFactory.CreateStorage);
    var ids = restored.ComponentIndex<SavedIdentity, int>();
    var restoredFirst = ids[1].Single();
    var restoredSecond = ids[2].Single();
    var restoredSelf = ids[3].Single();
    Check.That(restoredFirst.Get<SavedOwner>().Target == restoredSecond && restoredSecond.Get<SavedOwner>().Target == restoredFirst, "Two-pass graph construction remaps cycles to destination handles.");
    Check.That(restoredSelf.Get<SavedOwner>().Target == restoredSelf && restoredFirst != first, "Self references remap and restored handles are independent.");
    Check.That(restored.ComponentIndex<SavedOwner, Entity>()[restoredFirst].Single() == restoredSecond, "Graph construction builds reverse indexes from remapped values.");
    var log = new List<string>();
    using var cleanup = restoredSecond.Events.Subscribe<ComponentRemoved<SavedOwner>>(_ => log.Add("source"));
    using var deleted = restoredFirst.Events.Subscribe<EntityDeleted>(_ => log.Add("target"));
    restoredFirst.Delete();
    restored.Events.Dispatch();
    Check.That(!restoredSecond.Has<SavedOwner>() && log.SequenceEqual(["source", "target"]), "Restored relationship deletion policies and cleanup events retain their behavior.");
    Check.That(restoredSelf.TryDelete(), "Restored self references do not restrict their own removal.");
    Check.Throws<JsonException>(
      () => serializer.ReadStore(
        json.Replace("\"target\":\"2\"", "\"target\":\"missing\"", StringComparison.Ordinal),
        StoreFactory.CreateStorage
      )
    );
    Check.Throws<MutationException>(
      () => serializer.ReadStore(
        json.Replace("\"value\":2", "\"value\":1", StringComparison.Ordinal),
        StoreFactory.CreateStorage
      )
    );
    Check.That(store.Count == 3 && first.Get<SavedOwner>().Target == second, "Failed graph validation leaves the original graph untouched.");
    Check.That(serializer.WriteStore(store) == json, "Runtime handles do not affect document-local graph semantics.");
  }
}
