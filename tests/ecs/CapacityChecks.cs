using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class CapacityChecks {
  public static void Run() {
    BufferedPopulation();
    LoadedPopulation();
  }

  private static void BufferedPopulation() {
    using var store = StoreFactory.Create();
    using var commands = store.CreateCommandBuffer();
    var population = new List<Entity>();
    var created = new List<Entity>();
    using var subscription = store.Events.Subscribe<EntityCreated>(item => {
      Check.That(!item.Snapshot.Has<QueryPosition>(), "Prepared create capacity leaves creation snapshots empty.");
      created.Add(item.Entity);
    });

    for (var i = 0; i < 5000; i++) population.Add(commands.Create());

    Check.That(store.Count == 0 && store.Query().Count == 0 && population.All(entity => !entity.IsAlive),
      "Recording a large batch reserves handles without publishing native rows.");
    commands.Add(population[0], new QueryPosition(1));
    commands.Add(population[0], new QueryPosition(2));
    var suffix = commands.Create();
    var ticket = commands.TryUpsert(suffix, new QueryPosition(3));

    try { commands.Playback(); throw new InvalidOperationException("Expected strict failure."); }
    catch (PlaybackException error) {
      Check.That(error.CommandIndex == 5001 && error.Result.Outcome(ticket) == CommandOutcome.Canceled,
        "Capacity preparation preserves the completed prefix and canceled suffix on failure.");
    }

    Check.That(store.Count == 5000 && store.Query().Count == 5000 && population.All(entity => entity.IsAlive),
      "Prepared playback publishes each create once and consumes the full successful prefix.");
    Check.That(!suffix.IsAlive && created.Count == 0 && population[0].Get<QueryPosition>().Value == 1,
      "Prepared playback retains deferred events and successful values across failure.");
    store.Events.Dispatch();
    Check.That(created.SequenceEqual(population), "Prepared creation events retain recording order.");

    for (var i = population.Count - 1; i >= 0; i -= 2) population[i].Delete();

    for (var i = 0; i < 3000; i++) _ = commands.Create();

    commands.Playback();
    Check.That(store.Count == 5500 && store.Query().Count == 5500,
      "Prepared capacity supports shrink, recycled handles and regrowth across native chunks.");
    commands.Create();
    commands.Clear();
    Check.That(store.Count == 5500, "Discarded batches leave prepared populations unchanged.");
  }

  private static void LoadedPopulation() {
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    string[] visible = ["saved.visible"];
    var nodes = Enumerable.Range(0, 6000).Select(i => new {
      id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
      components = i % 2 == 0 ? new[] { new { key = "saved.identity", version = 1, data = new { value = i } } } : [],
      tags = i % 3 == 0 ? visible : []
    });
    var json = JsonSerializer.Serialize(new { formatVersion = 1, entities = nodes });
    using var store = serializer.ReadStore(json, StoreFactory.CreateStorage);
    Check.That(store.Count == 6000 && store.Query<SavedIdentity>().Count == 3000,
      "Prepared store loads count separate final compositions correctly.");
    Check.That(store.Query().AllTags(Tags.Get<SavedVisible>()).Count == 2000,
      "Prepared tag-only and component/tag populations remain independently queryable.");
    var sum = 0L;
    store.Query<SavedIdentity>().ForEachEntity((value, entity) => {
      sum += value.Value;
      Check.That(store.ComponentIndex<SavedIdentity, int>()[value.Value].Single() == entity,
        "Prepared construction aligns component rows and generated indexes.");
    });
    Check.That(sum == 8997000, "Prepared construction retains every component payload.");
    var entity = store.ComponentIndex<SavedIdentity, int>()[200].Single();
    entity.Delete();
    Check.That(store.ComponentIndex<SavedIdentity, int>()[200].Count == 0 && store.Count == 5999,
      "Prepared populations support normal indexed deletion and swap removal.");
  }
}
