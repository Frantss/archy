using System.Text;
using System.Text.Json;

namespace Archy;

public sealed partial class StoreSerializer {
  public string WritePrefab(Prefab prefab) {
    ArgumentNullException.ThrowIfNull(prefab);
    var context = new ComponentCodecContext(new ReferenceContext(new Dictionary<string, Entity>()));

    return JsonSerializer.Serialize(new PrefabDocument(1, [], [this.EncodeDefinition("root", prefab.Definition, context)]), s_json);
  }

  public string WritePrefab(PrefabGraph prefab) {
    ArgumentNullException.ThrowIfNull(prefab);
    var context = new ComponentCodecContext(prefab.References());
    var nodes = prefab.Nodes.Select(node => this.EncodeDefinition(node.Id, node.Definition, context)).ToArray();

    return JsonSerializer.Serialize(new PrefabDocument(1, prefab.Externals.Keys.ToArray(), nodes), s_json);
  }

  public void WritePrefab(Prefab prefab, Stream stream) => WriteText(stream, this.WritePrefab(prefab));
  public void WritePrefab(PrefabGraph prefab, Stream stream) => WriteText(stream, this.WritePrefab(prefab));
  public Prefab ReadPrefab(Stream stream) => this.ReadPrefab(ReadText(stream));
  public PrefabGraph ReadPrefabGraph(Stream stream) => this.ReadPrefabGraph(ReadText(stream));

  public Prefab ReadPrefab(string json) {
    var graph = this.ReadPrefabGraph(json);

    if (graph.Nodes.Length != 1 || graph.Externals.Count != 0) throw new JsonException("Single prefabs require one node and no external bindings.");

    var definition = new EntityDefinition();
    var empty = new ReferenceContext(new Dictionary<string, Entity>());

    foreach (var (type, value) in graph.Nodes[0].Definition.Components) definition.Components.Add(type, value.Remap(graph.References(), empty));
    foreach (var (type, tag) in graph.Nodes[0].Definition.Tags) definition.Tags.Add(type, tag);

    return new(definition);
  }

  public PrefabGraph ReadPrefabGraph(string json) {
    var document = JsonSerializer.Deserialize<PrefabDocument>(json, s_json) ?? throw new JsonException("Missing prefab document.");

    if (document.FormatVersion != 1 || document.Entities is null || document.Bindings is null
      || document.Entities.Any(node => node is null || string.IsNullOrWhiteSpace(node.Id) || node.Components is null || node.Tags is null)
      || document.Entities.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != document.Entities.Length
      || document.Bindings.Any(string.IsNullOrWhiteSpace) || document.Bindings.Distinct(StringComparer.Ordinal).Count() != document.Bindings.Length)
      throw new JsonException("Invalid prefab format, node IDs or binding names.");

    var symbols = new PrefabGraphBuilder();

    foreach (var node in document.Entities) symbols.Create(node.Id);
    foreach (var name in document.Bindings) symbols.External(name);

    var empty = symbols.Build();
    var context = new ComponentCodecContext(empty.References());
    var nodes = document.Entities.Select((node, index) => new PrefabNode(node.Id, empty.Nodes[index].Reference, this.DecodeNode(node, context))).ToArray();
    var result = new PrefabGraph(nodes, new(empty.Externals, StringComparer.Ordinal));

    return result.ToBuilder().Build();
  }

  private EntityDocument EncodeDefinition(string id, EntityDefinition definition, ComponentCodecContext context) {
    var components = definition.Components.Values.Select(value => {
      var type = this.Type(value.Descriptor.Type);

      return new ComponentDocument(type.Key, type.Version, type.Write(value.CopyValue(), context));
    }).OrderBy(component => component.Key, StringComparer.Ordinal).ToArray();
    var tags = definition.Tags.Values.Select(tag => this.Type(tag.Type).Key).Order(StringComparer.Ordinal).ToArray();

    return new(id, components, tags);
  }

  private static void WriteText(Stream stream, string text) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Write(Encoding.UTF8.GetBytes(text));
  }

  private static string ReadText(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

    return reader.ReadToEnd();
  }

  private sealed record PrefabDocument(int FormatVersion, string[] Bindings, EntityDocument[] Entities);
}
