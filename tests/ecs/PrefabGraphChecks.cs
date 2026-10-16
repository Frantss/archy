using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class PrefabGraphChecks {
  public static void Run() {
    using var store = StoreFactory.Create();
    var builder = new PrefabGraphBuilder();
    var first = builder.Create("first");
    var second = builder.Create("second");
    first.Draft.Add(new SavedOwner(second.Reference));
    second.Draft.Add(new SavedOwner(first.Reference));
    first.Draft.Add(new SavedBag([1]));
    second.Draft.AddTag<SavedVisible>();
    second.Draft.Add(new OwnedBy(builder.External("owner")));
    var graph = builder.Build();
    Check.That(store.Count == 0 && !first.Reference.IsAlive, "Template nodes are outside live stores and queries.");
    Check.Throws<MutationException>(() => store.Instantiate(graph));
    var external = store.Create();
    var bindings = new Dictionary<string, Entity> { ["owner"] = external };
    var events = 0;
    using var created = store.Events.Subscribe<EntityCreated>(item => {
      events++;
      var link = item.Snapshot.Get<SavedOwner>();
      Check.That(item.Entity.IsAlive && link.Target.IsAlive && link.Target.Has<SavedOwner>(), "Construction handlers see the complete graph.");
    });
    var instance = store.Instantiate(graph, bindings);
    store.Events.Dispatch();
    Check.That(events == 2 && instance["first"].Get<SavedOwner>().Target == instance["second"] && instance["second"].Get<SavedOwner>().Target == instance["first"], "Graph references remap cycles within one instance.");
    Check.That(instance["second"].Get<OwnedBy>().Owner == external && instance["second"].HasTag<SavedVisible>(), "Explicit external bindings and tags survive construction.");
    Check.That(!external.TryDelete(), "Bound relationship policies remain active.");
    var other = store.Instantiate(graph, bindings);
    instance["first"].Update(new SavedBag([9]));
    Check.That(other["first"].Get<SavedBag>().Values[0] == 1 && other["first"].Get<SavedOwner>().Target == other["second"], "Instances own copied payloads and separate internal graphs.");
    var variant = graph.ToBuilder();
    variant["first"].Draft.Add(new SavedIdentity(10));
    variant["second"].Draft.Add(new SavedIdentity(10));
    var count = store.Count;
    Check.Throws<MutationException>(() => store.Instantiate(variant.Build(), bindings));
    Check.That(store.Count == count && store.ComponentIndex<SavedIdentity, int>()[10].Count == 0, "Within-graph unique conflicts publish no nodes or index memberships.");
    instance["first"].Delete();
    Check.That(!instance["second"].Has<SavedOwner>() && other["second"].Has<SavedOwner>(), "Deletion cleans only the affected instance's references.");
    using var foreign = StoreFactory.Create();
    Check.Throws<MutationException>(() => store.Instantiate(graph, new Dictionary<string, Entity> { ["owner"] = foreign.Create() }));
    var unbound = new PrefabGraphBuilder();
    unbound.Create("node").Draft.Add(new SavedOwner(external));
    Check.Throws<JsonException>(() => unbound.Build());
  }
}
