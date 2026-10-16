namespace Archy;

public sealed partial class EntityStore {
  public PrefabInstance Instantiate(PrefabGraph prefab, IReadOnlyDictionary<string, Entity>? bindings = null) {
    ArgumentNullException.ThrowIfNull(prefab);
    this.EnsurePlayback();
    bindings ??= new Dictionary<string, Entity>();

    if (bindings.Count != prefab.Externals.Count || prefab.Externals.Keys.Any(name => !bindings.TryGetValue(name, out var entity) || !this.IsAlive(entity)))
      throw new MutationException("Every external prefab binding must name a live entity in the destination store.");

    var external = bindings.ToArray();
    var entities = this.Construct(prefab.Nodes.Length, handles => {
      var destination = new ReferenceContext(prefab.Nodes.Select((node, index) => (Key: "node:" + node.Id, Entity: handles[index]))
        .Concat(external.Select(pair => (Key: "external:" + pair.Key, Entity: pair.Value)))
        .ToDictionary(item => item.Key, item => item.Entity, StringComparer.Ordinal));
      var source = prefab.References();

      return prefab.Nodes.Select(node => {
        var definition = new EntityDefinition();

        foreach (var (type, value) in node.Definition.Components) definition.Components.Add(type, value.Remap(source, destination));
        foreach (var (type, tag) in node.Definition.Tags) definition.Tags.Add(type, tag);

        return definition;
      }).ToArray();
    });

    return new(prefab.Nodes.Select((node, index) => (node.Id, Entity: entities[index])).ToDictionary(item => item.Id, item => item.Entity, StringComparer.Ordinal));
  }
}
