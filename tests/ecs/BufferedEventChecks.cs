using Archy;

namespace Archy.Tests;

internal static class BufferedEventChecks {
  public static void Run() {
    using var store = new EntityStore();
    using var commands = store.CreateCommandBuffer();
    var log = new List<string>();
    using var created = store.Events.Subscribe<EntityCreated>(_ => log.Add("create"));
    using var added = store.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => log.Add("add"));
    using var custom = store.Events.Subscribe<CustomEvent<Damage>>(item => log.Add($"global:{item.Data.Amount}"));
    using var deleted = store.Events.Subscribe<EntityDeleted>(_ => log.Add("delete"));
    using var storeEvent = store.Events.Subscribe<StoreEvent<Damage>>(_ => log.Add("store"));
    var entity = commands.Create();
    commands.Add(entity, new QueryPosition(1));
    commands.Emit(entity, new Damage(4, default));
    commands.Emit(new Damage(9, default));
    commands.Delete(entity);
    var afterDeletion = commands.TryEmit(entity, new Damage(5, default));
    using var local = entity.Events.Subscribe<CustomEvent<Damage>>(_ => log.Add("local"));
    var result = commands.Playback();
    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["create", "add", "global:4", "local", "store", "delete"]) && !result.Get(afterDeletion), "Buffered emissions share lifecycle FIFO positions and subscriptions are captured at playback.");
    var origin = store.Create();
    var input = new[] { 7 };
    MutableMessage retained = default;
    using var message = origin.Events.Subscribe<CustomEvent<MutableMessage>>(item => retained = item.Data);
    commands.Emit(origin, new MutableMessage(input));
    input[0] = 50;
    var accepted = commands.TryEmit(origin, new Damage(6, default));
    Check.That(commands.Playback().Get(accepted), "Buffered TryEmit returns a resolved success ticket.");
    store.Events.Dispatch();
    Check.That(retained.Values[0] == 7, "Recording custom payloads owns a copy.");
    log.Clear();
    commands.Emit(origin, new Damage(8, default));
    commands.Delete(origin);
    commands.Emit(origin, new Damage(9, default));
    var canceled = commands.TryEmit(origin, new Damage(10, default));

    try { commands.Playback(); throw new InvalidOperationException("Expected strict emission failure."); }
    catch (PlaybackException error) {
      Check.That(error.CommandIndex == 2 && error.Result.Outcome(canceled) == CommandOutcome.Canceled, "Strict emission failure reports its position and cancels later emissions.");
    }

    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["global:8", "delete"]), "Only the committed event prefix is observable after failure.");
  }
}
