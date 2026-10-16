using Archy;

namespace Archy.Tests;

internal static class CommandChecks {
  public static void Run() {
    using var store = new EntityStore();
    using var commands = store.CreateCommandBuffer();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    var updates = new List<int>();
    using var observed = entity.Events.Subscribe<ComponentUpdated<QueryPosition>>(item => updates.Add(item.NewValue.Value));

    foreach (var current in store.Query<QueryPosition>().Entities) {
      commands.Upsert(current, new QueryPosition(2));
      commands.Upsert(current, new QueryPosition(3));
      commands.Remove<QueryPosition>(current);
      commands.Upsert(current, new QueryPosition(4));
      commands.AddTag<QueryVisible>(current);
      Check.Throws<InvalidOperationException>(() => commands.Playback());
    }

    var absent = commands.TryRemove<QueryVelocity>(entity);
    var upsert = commands.TryUpsert(entity, new QueryVelocity(5));
    var existing = commands.TryAdd(entity, new QueryVelocity(6));
    var result = commands.Playback();
    Check.That(!result.Get(absent) && result.Get(upsert) && !result.Get(existing), "Try tickets resolve at playback and ordinary failures continue.");
    Check.That(entity.Get<QueryPosition>().Value == 4 && entity.HasTag<QueryVisible>(), "Buffered operations safely change queried compositions afterward.");
    store.Events.Dispatch();
    Check.That(updates.SequenceEqual([2, 3]), "FIFO playback preserves observable updates instead of coalescing.");
    commands.Upsert(entity, new QueryPosition(8));
    var prefix = commands.TryRemove<QueryVelocity>(entity);
    commands.Add(entity, new QueryPosition(9));
    var suffix = commands.TryRemove<QueryPosition>(entity);

    try { commands.Playback(); throw new InvalidOperationException("Expected strict failure."); }
    catch (PlaybackException error) {
      Check.That(error.CommandIndex == 2 && error.Result.Get(prefix) && error.Result.Outcome(suffix) == CommandOutcome.Canceled, "Strict failure reports command position, completed results and canceled suffix.");
      Check.Throws<OperationCanceledException>(() => error.Result.Get(suffix));
    }

    Check.That(commands.Count == 0 && entity.Get<QueryPosition>().Value == 8, "Failed playback consumes the stream and retains its committed prefix.");
    Check.Throws<ArgumentException>(() => commands.Playback().Get(prefix));
    var input = new[] { 1 };
    commands.Add(entity, new EventBag(input));
    input[0] = 20;
    commands.Playback();
    Check.That(entity.Get<EventBag>().Values[0] == 1, "Recording owns component payloads.");
    using var foreign = new EntityStore();
    var other = foreign.Create();
    var foreignTicket = commands.TryUpsert(other, new QueryPosition(1));
    Check.That(!commands.Playback().Get(foreignTicket) && !other.Has<QueryPosition>(), "Commands validate full handles against their bound store.");
    using var handler = entity.Events.Subscribe<TagRemoved<QueryVisible>>(_ => {
      commands.Upsert(entity, new QueryVelocity(7));
      Check.Throws<InvalidOperationException>(() => commands.Playback());
    });
    entity.RemoveTag<QueryVisible>();
    store.Events.Dispatch();
    Check.That(!entity.Has<QueryVelocity>() && commands.Count == 1, "Handlers can record commands for a later checkpoint.");
    commands.Playback();
    Check.That(entity.Get<QueryVelocity>().Value == 7, "Handler commands commit after dispatch finishes.");
    var captured = entity;
    entity.Delete();
    var replacement = store.Create();
    var stale = commands.TryUpsert(captured, new QueryPosition(100));
    Check.That(!commands.Playback().Get(stale) && !replacement.Has<QueryPosition>(), "Stale targets never mutate reused slots.");
  }
}
