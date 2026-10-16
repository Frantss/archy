namespace Archy;

public sealed class PrefabNodeBuilder {
  public string Id { get; }
  public Entity Reference { get; }
  public PrefabBuilder Draft { get; }
  internal PrefabNodeBuilder(string id, Entity reference, PrefabBuilder draft) {
    this.Id = id;
    this.Reference = reference;
    this.Draft = draft;
  }
}

public sealed class PrefabGraphBuilder {
  private static int s_generation;
  private readonly uint _generation = unchecked((uint)Interlocked.Increment(ref s_generation));
  private int _nextId;
  private readonly Dictionary<string, PrefabNodeBuilder> _nodes = new(StringComparer.Ordinal);
  private readonly Dictionary<string, Entity> _externals = new(StringComparer.Ordinal);

  public PrefabGraphBuilder() {}
  internal PrefabGraphBuilder(PrefabGraph graph) {
    foreach (var node in graph.Nodes) {
      this._nodes.Add(node.Id, new(node.Id, node.Reference, new(node.Definition)));
    }

    foreach (var (name, reference) in graph.Externals) this._externals.Add(name, reference);

    this._nextId = graph.Nodes.Length + graph.Externals.Count;
  }

  public PrefabNodeBuilder this[string id] => this._nodes[id];
  public PrefabNodeBuilder Create(string id) {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    var node = new PrefabNodeBuilder(id, new(null, ++this._nextId, this._generation), new());
    this._nodes.Add(id, node);

    return node;
  }

  public Entity External(string name) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);

    if (this._externals.TryGetValue(name, out var existing)) return existing;

    var entity = new Entity(null, ++this._nextId, this._generation);
    this._externals.Add(name, entity);

    return entity;
  }

  public PrefabGraph Build() {
    var nodes = this._nodes.Values.Select(node => new PrefabNode(node.Id, node.Reference, node.Draft.Build().Definition)).ToArray();
    var graph = new PrefabGraph(nodes, new(this._externals, StringComparer.Ordinal));
    var references = graph.References();

    foreach (var node in nodes) {
      foreach (var value in node.Definition.Components.Values) _ = value.Remap(references, references);
    }

    return graph;
  }
}

public sealed class PrefabGraph {
  internal PrefabNode[] Nodes { get; }
  internal Dictionary<string, Entity> Externals { get; }
  public IReadOnlyList<string> NodeIds => this.Nodes.Select(node => node.Id).ToArray();
  public IReadOnlyList<string> RequiredBindings => this.Externals.Keys.ToArray();
  internal PrefabGraph(PrefabNode[] nodes, Dictionary<string, Entity> externals) { this.Nodes = nodes; this.Externals = externals; }
  public PrefabGraphBuilder ToBuilder() => new(this);
  internal ReferenceContext References() => new(this.Nodes.ToDictionary(node => "node:" + node.Id, node => node.Reference, StringComparer.Ordinal)
    .Concat(this.Externals.Select(pair => new KeyValuePair<string, Entity>("external:" + pair.Key, pair.Value)))
    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

internal sealed record PrefabNode(string Id, Entity Reference, EntityDefinition Definition);

public sealed class PrefabInstance {
  private readonly Dictionary<string, Entity> _entities;
  internal PrefabInstance(Dictionary<string, Entity> entities) => this._entities = entities;
  public Entity this[string id] => this._entities[id];
  public IReadOnlyList<Entity> Entities => this._entities.Values.ToArray();
}
