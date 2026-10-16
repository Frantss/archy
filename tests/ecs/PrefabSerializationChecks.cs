using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class PrefabSerializationChecks {
  public static void Run() {
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    var graphDraft = new PrefabGraphBuilder();
    var root = graphDraft.Create("root");
    var child = graphDraft.Create("child");
    root.Draft.Add(new SavedOwner(child.Reference));
    child.Draft.Add(new SavedOwner(root.Reference));
    root.Draft.Add(new SavedExternal(graphDraft.External("world")));
    root.Draft.Add(new SavedBag([1, 2]));
    child.Draft.AddTag<SavedVisible>();
    var json = serializer.WritePrefab(graphDraft.Build());
    Check.That(json.Contains("node:child", StringComparison.Ordinal) && json.Contains("external:world", StringComparison.Ordinal)
      && !json.Contains("generation", StringComparison.OrdinalIgnoreCase), "Prefab files use local node and external binding IDs without runtime handles.");
    var graph = serializer.ReadPrefabGraph(json);
    Check.That(graph.RequiredBindings.SequenceEqual(["world"]) && graph.NodeIds.SequenceEqual(["root", "child"]), "Prefab files retain node identities and explicit binding requirements.");
    using var store = StoreFactory.Create();
    var external = store.Create();
    var bindings = new Dictionary<string, Entity> { ["world"] = external };
    var creations = 0;
    using var events = store.Events.Subscribe<EntityCreated>(item => {
      creations++;
      Check.That(item.Snapshot.Get<SavedOwner>().Target.IsAlive, "Restored prefab events see complete instances.");
    });
    var first = store.Instantiate(graph, bindings);
    var second = store.Instantiate(graph, bindings);
    store.Events.Dispatch();
    Check.That(creations == 4 && first["root"].Get<SavedOwner>().Target == first["child"] && second["child"].Get<SavedOwner>().Target == second["root"], "A restored graph can instantiate twice with independent remapping and events.");
    Check.That(first["root"].Get<SavedExternal>().Target == external && second["child"].HasTag<SavedVisible>(), "Restored graph instances preserve bindings and tags.");
    first["root"].Update(new SavedBag([9]));
    Check.That(second["root"].Get<SavedBag>().Values.SequenceEqual([1, 2]), "Serialized templates retain copied values between instances.");
    Check.Throws<JsonException>(() => serializer.ReadPrefabGraph(json.Replace("node:child", "node:missing", StringComparison.Ordinal)));
    Check.Throws<JsonException>(() => serializer.ReadPrefabGraph(json.Replace("saved.bag", "missing.type", StringComparison.Ordinal)));
    Check.Throws<JsonException>(() => serializer.ReadPrefabGraph(json.Replace("\"formatVersion\":1", "\"formatVersion\":9", StringComparison.Ordinal)));
    Check.Throws<JsonException>(() => serializer.ReadPrefab(json));
    var draft = new PrefabBuilder();
    draft.Add(new SavedVersioned(5));
    var old = serializer.WritePrefab(draft.Build()).Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal).Replace("\"value\":5", "\"old\":5", StringComparison.Ordinal);
    Check.Throws<JsonException>(() => serializer.ReadPrefab(old));
    serializer.RegisterMigration<SavedVersioned>(1, data => JsonSerializer.SerializeToElement(new { value = data.GetProperty("old").GetInt32() + 1 }));
    var migrated = serializer.ReadPrefab(old);
    using var other = StoreFactory.Create();
    Check.That(other.Instantiate(migrated).Get<SavedVersioned>().Value == 6, "Prefab files reuse registered component migrations.");
    using var stream = new MemoryStream();
    serializer.WritePrefab(graph, stream);
    stream.Position = 0;
    Check.That(serializer.ReadPrefabGraph(stream).NodeIds.Count == 2 && stream.CanRead, "Prefab stream overloads preserve caller ownership.");
  }
}

[ComponentKey("saved.external")]
internal readonly partial record struct SavedExternal([property: Indexed, EntityReference] Entity Target) : IComponent;
