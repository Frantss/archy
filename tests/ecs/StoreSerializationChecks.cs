using System.Text;
using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class StoreSerializationChecks {
  public static void Run() {
    using var store = new EntityStore();
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    var first = store.Create();
    first.Add(new SavedIdentity(42));
    first.Add(new SavedBag([3, 4]));
    first.AddTag<SavedVisible>();
    var second = store.Create();
    second.Add(new SavedIdentity(43));
    using var buffer = store.CreateCommandBuffer();
    buffer.Update(first, new SavedIdentity(90));
    first.Emit(new Damage(2, default));
    var json = serializer.WriteStore(store);
    using var restored = serializer.ReadStore(json);
    var restoredFirst = restored.ComponentIndex<SavedIdentity, int>()[42].Single();
    Check.That(restored.Count == 2 && restoredFirst != first && restoredFirst.HasTag<SavedVisible>(), "Logical store restore creates independent handles, tags and indexes.");
    Check.That(restoredFirst.Get<SavedBag>().Values.SequenceEqual([3, 4]), "Store payloads round trip through component codecs.");
    var replayed = 0;
    using var subscription = restored.Events.Subscribe<EntityCreated>(_ => replayed++);
    restored.Events.Dispatch();
    Check.That(replayed == 0 && restored.ComponentIndex<SavedIdentity, int>()[90].Count == 0, "Pending events and commands are excluded from fresh restores.");
    restoredFirst.Update(new SavedIdentity(100));
    Check.That(first.Get<SavedIdentity>().Value == 42, "Restored writes do not affect original stores.");
    using var stream = new MemoryStream();
    serializer.WriteStoreAsync(store, stream).GetAwaiter().GetResult();
    first.Update(new SavedIdentity(44));
    stream.Position = 0;
    using var asyncRestored = serializer.ReadStore(stream);
    Check.That(asyncRestored.ComponentIndex<SavedIdentity, int>()[42].Count == 1, "Asynchronous output owns its captured state.");
    Check.Throws<JsonException>(() => serializer.ReadStore(json.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal)));
    Check.Throws<MutationException>(() => serializer.ReadStore(json.Replace("\"value\":43", "\"value\":42", StringComparison.Ordinal)));
    Check.That(store.Count == 2 && first.Get<SavedIdentity>().Value == 44, "Failed fresh loads leave existing stores untouched.");
    Check.Throws<JsonException>(() => serializer.ReadStore("{\"formatVersion\":1,\"entities\":[{\"id\":\"a\",\"components\":[],\"tags\":[]},{\"id\":\"a\",\"components\":[],\"tags\":[]}]}"));
    Check.Throws<JsonException>(() => serializer.ReadStore(json.Replace("saved.visible", "missing.tag", StringComparison.Ordinal)));
    using (var traversal = store.Query().Entities.GetEnumerator()) {
      Check.That(traversal.MoveNext(), "Save checkpoint traversal starts.");
      Check.Throws<InvalidOperationException>(() => serializer.WriteStore(store));
    }

    using var delayed = new DelayedStream();
    var pending = serializer.WriteStoreAsync(store, delayed);
    Check.That(!pending.IsCompleted, "Asynchronous output can remain pending after its writer snapshot.");
    first.Update(new SavedIdentity(45));
    delayed.Release();
    pending.GetAwaiter().GetResult();
    delayed.Position = 0;
    using var delayedRestored = serializer.ReadStore(delayed);
    Check.That(delayedRestored.ComponentIndex<SavedIdentity, int>()[44].Count == 1, "Pending asynchronous I/O owns state captured before subsequent writer mutations.");
    var bad = store.Create();
    bad.Add(new TransientData(1));
    Check.Throws<JsonException>(() => serializer.WriteStore(store));
    using var componentStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
    using var streamRestored = serializer.ReadStore(componentStream);
    Check.That(streamRestored.Count == 2 && componentStream.CanRead, "Stream overloads retain caller ownership.");
  }
  private sealed class DelayedStream : MemoryStream {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => this._release.SetResult();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
      await this._release.Task.ConfigureAwait(false);
      await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
  }
}
