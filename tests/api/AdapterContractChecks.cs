using System.Text.Json;

namespace Archy.Tests;

internal static class AdapterContractChecks {
  public static void Run() {
    Check.That(typeof(EntityStore).Assembly.GetReferencedAssemblies().All(name => name.Name != "Arch"),
      "The API assembly has no Arch dependency.");
    var storage = new MemoryStorage();
    var store = new EntityStore(storage);
    store.Create().Add(new QueryPosition(1));
    store.Dispose();
    Check.That(storage.Disposed, "Stores dispose their supplied storage adapter.");

    using var source = StoreFactory.Create();
    source.Create().Add(new SavedIdentity(42));
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    var json = serializer.WriteStore(source).Replace("saved.identity", "missing.component", StringComparison.Ordinal);
    var failed = new MemoryStorage();
    Check.Throws<JsonException>(() => serializer.ReadStore(json, () => failed));
    Check.That(failed.Disposed, "Failed restoration disposes the adapter created by its storage factory.");
    var calls = 0;
    Check.Throws<JsonException>(() => serializer.ReadStore("{}", () => {
      calls++;

      return new MemoryStorage();
    }));
    Check.That(calls == 0, "Invalid document headers are rejected before allocating an adapter.");
  }
}
