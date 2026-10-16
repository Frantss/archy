using Archy;

namespace Archy.Tests;

internal static class ReferenceChecks {
  public static void Run() {
    using var store = StoreFactory.Create();
    using var foreign = StoreFactory.Create();
    var target = store.Create();
    var source = store.Create();
    var blocker = store.Create();
    source.Add(new AttachedTo(target));
    blocker.Add(new OwnedBy(target));
    Check.That(store.ComponentIndex<AttachedTo, Entity>()[target].Single() == source, "References use ordinary reverse indexes.");
    Check.That(!source.TryUpsert(new AttachedTo(foreign.Create())) && !source.TryUpsert(new AttachedTo(default)), "Required references reject foreign and empty targets.");
    var log = new List<string>();
    using var clean = source.Events.Subscribe<ComponentRemoved<AttachedTo>>(_ => log.Add("cleanup"));
    using var deleted = target.Events.Subscribe<EntityDeleted>(_ => log.Add("deleted"));
    Check.That(!target.TryDelete() && source.Has<AttachedTo>() && target.IsAlive, "Restrict preflights all references before any cleanup.");
    store.Events.Dispatch();
    Check.That(log.Count == 0, "Rejected deletion produces no cleanup events.");
    blocker.Remove<OwnedBy>();
    target.Delete();
    store.Events.Dispatch();
    Check.That(!source.Has<AttachedTo>() && log.SequenceEqual(["cleanup", "deleted"]), "Source cleanup precedes target deletion.");
    Check.That(!source.TryAdd(new AttachedTo(target)), "Stale reference targets are invalid.");
    var self = store.Create();
    self.Add(new OwnedBy(self));
    Check.That(self.TryDelete(), "Self references do not restrict deletion.");
    source.Add(new OptionalTarget(default));
    Check.That(store.ComponentIndex<OptionalTarget, Entity>()[default].Single() == source, "Explicit optional roles accept empty references.");
    using var commands = store.CreateCommandBuffer();
    var parent = commands.Create();
    var child = commands.Create();
    commands.Add(child, new OwnedBy(parent));
    commands.Playback();
    Check.That(child.Get<OwnedBy>().Owner == parent, "Earlier buffered creations are valid targets when links commit.");
    var pending = commands.Create();
    Check.That(!source.TryAdd(new AttachedTo(pending)), "Immediate links reject uncommitted reservations.");
    commands.Clear();
    var edge = store.Create();
    edge.Add(new OwnedBy(parent));
    edge.Add(new AttachedTo(child));
    Check.That(edge.Has<OwnedBy>() && edge.Has<AttachedTo>(), "Edge entities express multiple relationship roles as separate components.");
    Check.Throws<InvalidOperationException>(() => source.Add(new UnmarkedReference(parent)));
    Check.Throws<InvalidOperationException>(() => source.Add(new EntityArray([parent])));
    Check.Throws<InvalidOperationException>(() => source.Add(new EntityMemory(new Entity[] { parent })));
    var atomicTarget = store.Create();
    var atomicSource = store.Create();
    atomicSource.Add(new AttachedTo(atomicTarget));
    atomicSource.Add(new FragileLink(atomicTarget));
    using var fragileSubscription = atomicSource.Events.Subscribe<ComponentRemoved<FragileLink>>(_ => {});
    FragileLinkCopy.Fail = true;
    Check.Throws<InvalidOperationException>(() => atomicTarget.TryDelete());
    Check.That(atomicSource.Has<AttachedTo>() && atomicSource.Has<FragileLink>() && atomicTarget.IsAlive, "Cleanup payload failure preserves all sources and target.");
    FragileLinkCopy.Fail = false;
    atomicTarget.Delete();
    Check.That(!atomicSource.Has<AttachedTo>() && !atomicSource.Has<FragileLink>(), "Multiple cleanup roles on one source remove every component once.");
  }
}

internal readonly partial record struct OwnedBy([property: Indexed, EntityReference] Entity Owner) : IComponent;
internal readonly partial record struct AttachedTo([property: Indexed, EntityReference(OnDelete = TargetDeletion.RemoveComponent)] Entity Target) : IComponent;
internal readonly partial record struct OptionalTarget([property: Indexed, EntityReference(Optional = true)] Entity Target) : IComponent;
internal readonly record struct UnmarkedReference(Entity Target) : IComponent;

[ComponentCopy(typeof(FragileLinkCopy))]
internal readonly partial record struct FragileLink([property: Indexed, EntityReference(OnDelete = TargetDeletion.RemoveComponent)] Entity Target) : IComponent;
internal sealed class FragileLinkCopy : IComponentCopy<FragileLink> {
  public static bool Fail { get; set; }
  public FragileLink Copy(in FragileLink value) => Fail ? throw new InvalidOperationException("Copy rejected.") : value;
}

[ComponentCopy(typeof(EntityArrayCopy))]
internal readonly record struct EntityArray(Entity[] Values) : IComponent;
internal sealed class EntityArrayCopy : IComponentCopy<EntityArray> {
  public EntityArray Copy(in EntityArray value) => new([..value.Values]);
}
[ComponentCopy(typeof(EntityMemoryCopy))]
internal readonly record struct EntityMemory(ReadOnlyMemory<Entity> Values) : IComponent;
internal sealed class EntityMemoryCopy : IComponentCopy<EntityMemory> {
  public EntityMemory Copy(in EntityMemory value) => new(value.Values.ToArray());
}
