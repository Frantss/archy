using Archy;

namespace Archy.Tests;

internal static class PrefabChecks {
  public static void Run() {
    using var store = new EntityStore();
    using var other = new EntityStore();
    var draft = new PrefabBuilder();
    Check.That(!draft.TryUpdate(new QueryPosition(1)), "Prefab strict update requires an existing component.");
    draft.Add(new QueryPosition(1));
    draft.Upsert(new QueryVelocity(2));
    draft.AddTag<QueryVisible>();
    Check.That(!draft.TryAdd(new QueryPosition(4)) && !draft.TryAddTag<QueryVisible>(), "Prefab try operations enforce presence.");
    var input = new[] { 3 };
    draft.Add(new EventBag(input));
    var prefab = draft.Build();
    input[0] = 99;
    draft.Update(new QueryPosition(20));
    draft.RemoveTag<QueryVisible>();
    var variantDraft = prefab.ToBuilder();
    variantDraft.Upsert(new QueryVelocity(8));
    Check.That(variantDraft.TryRemove<EventBag>() && !variantDraft.TryRemove<EventBag>(), "Variants use local strict/try removals.");
    var variant = variantDraft.Build();
    var log = new List<string>();
    var modified = 0;
    using var created = store.Events.Subscribe<EntityCreated>(item => {
      log.Add("create");
      Check.That(item.Entity.IsAlive && item.Entity.Has<QueryVelocity>() && item.Snapshot.Get<QueryPosition>().Value == 1, "Prefab creation observes a complete valid entity and construction snapshot.");
    });
    using var addition = store.Events.Subscribe<ComponentAdded<QueryPosition>>(_ => log.Add("add"));
    using var modification = store.Events.Subscribe<EntityModified>(_ => modified++);
    var instance = store.Instantiate(prefab);
    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["create", "add"]) && modified == 0, "Construction emits creation then additions without intermediate modifications.");
    Check.That(instance.Get<EventBag>().Values[0] == 3 && instance.HasTag<QueryVisible>(), "Built templates remain independent of draft/input edits.");
    var second = store.Instantiate(variant);
    var third = other.Instantiate(prefab);
    instance.Update(new QueryPosition(10));
    Check.That(second.Get<QueryVelocity>().Value == 8 && second.Get<QueryPosition>().Value == 1 && third.Get<QueryPosition>().Value == 1, "Variants and instances across stores remain independent.");
    var identityDraft = new PrefabBuilder();
    identityDraft.Add(new SavedIdentity(5));
    var identity = identityDraft.Build();
    store.Instantiate(identity);
    var count = store.Count;
    var observed = log.Count;
    Check.Throws<MutationException>(() => store.Instantiate(identity));
    Check.That(store.Count == count && log.Count == observed, "Unique conflicts publish no partial instance or events.");
    identityDraft.Upsert(new SavedIdentity(6));
    Check.That(store.Instantiate(identityDraft.Build()).Get<SavedIdentity>().Value == 6, "Per-instance identity overrides avoid unique template collisions.");
    using var isolated = new EntityStore();
    var copies = new PrefabBuilder();
    copies.Add(new FragileLink(default));
    var unsafePrefab = copies.Build();
    Check.Throws<MutationException>(() => isolated.Instantiate(unsafePrefab));
    Check.That(isolated.Count == 0, "Reference validation failures publish no entity.");
  }
}
