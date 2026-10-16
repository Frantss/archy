using Archy;

namespace Archy.Tests;

internal static class WorkflowChecks {
  public static void Run() {
    var serializer = new StoreSerializer(typeof(WorkflowPose).Assembly);
    var builder = new PrefabGraphBuilder();
    var actor = builder.Create("actor");
    var item = builder.Create("item");
    actor.Draft.Add(new WorkflowPose(0, 0));
    actor.Draft.Add(new WorkflowSpeed(1));
    actor.Draft.AddTag<WorkflowActive>();
    item.Draft.Add(new WorkflowOwner(actor.Reference));
    var template = serializer.ReadPrefabGraph(serializer.WritePrefab(builder.Build()));
    using var store = new EntityStore();
    var first = store.Instantiate(template);
    var second = store.Instantiate(template);
    var trace = new List<string>();
    using var global = store.Events.Subscribe<CustomEvent<WorkflowStep>>(item => trace.Add($"global:{item.Entity.Id}"));
    using var local = first["actor"].Events.Subscribe<CustomEvent<WorkflowStep>>(item => trace.Add($"local:{item.Entity.Id}"));
    var oldValues = new List<int>();
    using var changes = store.Events.Subscribe<ComponentUpdated<WorkflowPose>>(item => oldValues.Add(item.OldValue.X));
    CommandBuffer? commands = null;
    using var eventCommands = first["actor"].Events.Subscribe<CustomEvent<WorkflowStep>>(_ => commands!.Upsert(first["actor"], new WorkflowSpeed(2)));
    using var pipeline = new SystemPipeline(store);
    pipeline.Add(new WorkflowMovement(store, first["actor"], buffer => commands = buffer));
    pipeline.Add(new WorkflowVerify(first["actor"]));
    pipeline.Update(new(1, 1));
    Check.That(oldValues.SequenceEqual([0, 0]) && first["actor"].Get<WorkflowPose>().X == 1, "Combined systems commit value changes with owned lifecycle snapshots.");
    Check.That(trace.Count == 3 && trace.IndexOf($"global:{first["actor"].Id}") < trace.IndexOf($"local:{first["actor"].Id}"), "Combined workflow routes store subscriptions before entity subscriptions.");
    Check.That(store.Query<WorkflowPose>().HasValue<WorkflowPose, (int, int)>((0, 0)).Count == 2, "Private computed indexes remain consistent through system commands.");
    Check.That(store.ComponentIndex<WorkflowOwner, Entity>()[first["actor"]].Count == 2, "Buffered creation links a new entity through ordinary indexed components.");
    using var restored = serializer.ReadStore(serializer.WriteStore(store));
    Check.That(restored.Count == 5 && restored.Query<WorkflowPose>().AllTags(Tags.Get<WorkflowActive>()).Count == 2, "The composed workflow restores generated values, tags and indexes.");

    foreach (var linked in restored.Query<WorkflowOwner>().Entities) {
      var owner = linked.Get<WorkflowOwner>();
      Check.That(owner.Target.Store == restored && owner.Target.IsAlive, "Restored workflow links point only into the restored store.");
    }

    first["actor"].Delete();
    Check.That(!first["item"].Has<WorkflowOwner>() && second["item"].Has<WorkflowOwner>(), "Relationship cleanup remains isolated between restored prefab instances.");
  }

  private sealed class WorkflowMovement : ISystem {
    private readonly ArchetypeQuery<WorkflowPose, WorkflowSpeed> _moving;
    private readonly Action<CommandBuffer> _commands;
    private readonly Entity _spawnOwner;
    public WorkflowMovement(EntityStore store, Entity spawnOwner, Action<CommandBuffer> commands) {
      this._moving = store.Query<WorkflowPose, WorkflowSpeed>().AllTags(Tags.Get<WorkflowActive>()).FreezeFilter();
      this._commands = commands;
      this._spawnOwner = spawnOwner;
    }

    public void Update(SystemContext context) {
      this._commands(context.Commands);
      this._moving.ForEachEntity((pose, speed, entity) => {
        context.Commands.Upsert(entity, pose with { X = pose.X + speed.Value });
        context.Commands.Emit(entity, new WorkflowStep(context.Tick.Number));
      });
      var spawned = context.Commands.Create();
      context.Commands.Add(spawned, new WorkflowOwner(this._spawnOwner));
    }
  }

  private sealed class WorkflowVerify : ISystem {
    private readonly Entity _actor;
    public WorkflowVerify(Entity actor) => this._actor = actor;
    public void Update(SystemContext context) {
      Check.That(this._actor.Get<WorkflowPose>().X == 1 && this._actor.Get<WorkflowSpeed>().Value == 1, "Next system sees system writes while handler commands await the next checkpoint.");
      context.Checkpoint();
      Check.That(this._actor.Get<WorkflowSpeed>().Value == 2, "Explicit checkpoint commits handler-recorded commands in the combined workflow.");
    }
  }
}

[ComponentKey("workflow.pose")]
internal readonly partial record struct WorkflowPose(int X, int Y) : IComponent {
  [Indexed] private (int, int) Cell() => (this.X / 4, this.Y / 4);
}
[ComponentKey("workflow.speed")]
internal readonly record struct WorkflowSpeed(int Value) : IComponent;
[ComponentKey("workflow.owner")]
internal readonly partial record struct WorkflowOwner([property: Indexed, EntityReference(OnDelete = TargetDeletion.RemoveComponent)] Entity Target) : IComponent;
[TagName("workflow.active")]
internal readonly struct WorkflowActive : ITag;
internal readonly record struct WorkflowStep(long Tick) : ICustomEvent;
