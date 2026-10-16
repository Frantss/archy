using System.Reflection;
using Arch.Core;
using Archy;
using Entity = Archy.Entity;
using Store = Archy.EntityStore;

namespace Archy.Tests;

internal static class NativeIdentityChecks {
  public static void Run() {
    GenerationBits();
    using var ordinary = World.Create();
    var ordinaryEntity = ordinary.Create(new QueryPosition(7));
    Check.That(ordinary.IsAlive(ordinaryEntity) && ordinary.Get<QueryPosition>(ordinaryEntity).Value == 7,
      "Independent Arch worlds retain normal metadata and entity operations.");
    ordinary.Destroy(ordinaryEntity);
    Check.That(!ordinary.IsAlive(ordinaryEntity), "Destroying an independent native entity preserves its generation contract.");

    using var store = StoreFactory.Create();
    using var commands = store.CreateCommandBuffer();
    var entities = new List<Entity>();
    var expected = new Dictionary<Entity, int?>();

    void Verify() {
      var handles = store.Query().Entities.ToHashSet();
      Check.That(handles.SetEquals(expected.Keys) && store.Count == expected.Count,
        "Public population follows reservations, activation, moves, deletion and reuse.");
      Check.That(store.Query<QueryPosition>().Count == expected.Count(pair => pair.Value.HasValue),
        "Typed query counts preserve public membership through native structural changes.");

      foreach (var (entity, value) in expected) {
        Check.That(entity.IsAlive && entity.Has<QueryPosition>() == value.HasValue,
          "Live handles retain their component membership after native row swaps.");

        if (value.HasValue) {
          var position = entity.Get<QueryPosition>();
          Check.That(position.Value == value, "Native row swaps preserve the value belonging to each public handle.");
        }
      }

      store.Query<QueryPosition>().ForEachEntity((value, entity) =>
        Check.That(expected[entity] == value.Value, "Typed callbacks project public identities from native rows."));
    }

    for (var i = 0; i < 4096; i++) entities.Add(commands.Create());

    Verify();
    commands.Clear();
    Verify();

    for (var i = 0; i < 4096; i++) {
      var entity = commands.Create();
      entities.Add(entity);
      commands.Add(entity, new QueryPosition(i));
      expected.Add(entity, i);
    }

    commands.Playback();
    Verify();

    foreach (var entity in entities.Where(entity => entity.IsAlive).Where((_, index) => index % 3 == 0)) {
      entity.Remove<QueryPosition>();
      entity.AddTag<QueryVisible>();
      expected[entity] = null;
    }

    Verify();

    foreach (var entity in entities.Where(entity => entity.IsAlive).Where((_, index) => index % 2 == 0)) {
      entity.Delete();
      expected.Remove(entity);
    }

    Verify();

    for (var i = 0; i < 5000; i++) {
      var entity = store.Create();
      entity.Add(new QueryPosition(i));
      expected.Add(entity, i);
    }

    Verify();
    Check.That(entities.Take(4096).All(entity => !entity.IsAlive),
      "Canceled generations stay invalid after native growth and reuse.");
  }

  private static void GenerationBits() {
    using var store = StoreFactory.Create();
    var stale = store.Create();
    stale.Delete();
    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
    var slots = typeof(Store).GetField("_slots", flags)!.GetValue(store)!;
    var pages = (Array[])slots.GetType().GetField("_pages", flags)!.GetValue(slots)!;
    var slot = pages[0].GetValue(stale.Id)!;
    slot.GetType().GetField("Generation")!.SetValue(slot, uint.MaxValue - 1);
    pages[0].SetValue(slot, stale.Id);
    var high = store.Create();
    high.Add(new QueryPosition(7));
    high.Add(new IndexedGroup { Value = 1 });
    high.AddTag<QueryVisible>();
    Check.That(high.Generation == uint.MaxValue - 1 && store.Query().Entities.Single() == high,
      "Query handle projection preserves high unsigned generation bits.");
    store.Query<QueryPosition>().ForEachEntity((_, entity) => {
      Check.That(entity == high, "Callback handles carry the public unsigned generation.");
      entity.Update(new QueryPosition(8));
    });
    Entity projected = default;

    foreach (var (_, handles) in store.Query<QueryPosition>().Chunks) projected = handles[0];

    Check.That(projected == high && store.ComponentIndex<IndexedGroup, int>()[1].Single() == high,
      "Chunk and indexed traversal retain the complete public generation after structural moves.");
    high.Delete();
    var saturated = store.Create();
    saturated.Add(new QueryPosition(9));
    Check.That(saturated.Id == high.Id && saturated.Generation == uint.MaxValue
      && store.Query().Entities.Single() == saturated, "The maximum unsigned generation remains a valid public handle.");
    Check.That(!high.TryUpdate(new QueryPosition(0)) && !stale.IsAlive,
      "Native query projection does not make historical handles valid.");
    saturated.Delete();
    Check.That(store.Create().Id != saturated.Id, "Saturated public slots retire instead of reusing their IDs.");
  }
}
