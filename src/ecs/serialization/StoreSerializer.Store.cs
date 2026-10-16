using System.Globalization;
using System.Text;
using System.Text.Json;
using Archy.Storage;

namespace Archy;

public sealed partial class StoreSerializer {
  public string WriteStore(EntityStore store) {
    store.EnsurePlayback();
    using var preparation = store.Prepare();
    var handles = store.Query().Entities.OrderBy(entity => entity.Id).ToArray();
    var ids = handles.Select((entity, index) => (entity, id: (index + 1).ToString(CultureInfo.InvariantCulture)))
      .ToDictionary(item => item.id, item => item.entity, StringComparer.Ordinal);
    var context = new ComponentCodecContext(new ReferenceContext(ids));
    var nodes = handles.Select(entity => {
      var composition = store.Composition(entity);
      var components = composition.Where(type => !type.IsTag).Select(type => {
        var metadata = this.Type(type.Type);

        return new ComponentDocument(metadata.Key, metadata.Version, metadata.Write(type.SnapshotValue(store, entity), context));
      }).OrderBy(component => component.Key, StringComparer.Ordinal).ToArray();
      var tags = composition.Where(type => type.IsTag).Select(type => this.Type(type.Type).Key).Order(StringComparer.Ordinal).ToArray();

      return new EntityDocument(context.References!.Write(entity)!, components, tags);
    }).ToArray();

    return JsonSerializer.Serialize(new StoreDocument(1, nodes), s_json);
  }

  public void WriteStore(EntityStore store, Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var snapshot = Encoding.UTF8.GetBytes(this.WriteStore(store));
    stream.Write(snapshot);
  }

  public async Task WriteStoreAsync(EntityStore store, Stream stream, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(stream);
    var snapshot = Encoding.UTF8.GetBytes(this.WriteStore(store));
    await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
  }

  public EntityStore ReadStore(Stream stream, Func<IEntityStorage> createStorage) {
    ArgumentNullException.ThrowIfNull(stream);
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

    return this.ReadStore(reader.ReadToEnd(), createStorage);
  }

  public EntityStore ReadStore(string json, Func<IEntityStorage> createStorage) {
    ArgumentNullException.ThrowIfNull(createStorage);

    var document = JsonSerializer.Deserialize<StoreDocument>(json, s_json) ?? throw new JsonException("Missing store document.");

    if (document.FormatVersion != 1 || document.Entities is null) throw new JsonException("Unsupported store format.");
    if (document.Entities.Any(node => node is null || string.IsNullOrWhiteSpace(node.Id) || node.Components is null || node.Tags is null)
      || document.Entities.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != document.Entities.Length)
      throw new JsonException("Invalid or duplicate entity IDs.");

    var store = new EntityStore(createStorage());

    try {
      store.Construct(document.Entities.Length, entities => {
        var handles = document.Entities.Select((node, index) => (node.Id, Entity: entities[index]))
          .ToDictionary(item => item.Id, item => item.Entity, StringComparer.Ordinal);
        var context = new ComponentCodecContext(new ReferenceContext(handles));

        return document.Entities.Select(node => this.DecodeNode(node, context)).ToArray();
      });

      return store;
    } catch {
      store.Dispose();
      throw;
    }
  }

  private EntityDefinition DecodeNode(EntityDocument node, ComponentCodecContext context) {
    var definition = new EntityDefinition();
    var keys = new HashSet<string>(StringComparer.Ordinal);

    foreach (var component in node.Components) {
      if (component is null || !keys.Add(component.Key)) throw new JsonException("Duplicate component key.");

      var (type, value) = this.Decode(component, context);
      definition.Components.Add(type.Type, type.Value(value));
    }

    foreach (var key in node.Tags) {
      if (key is null || !keys.Add(key) || !this._keys.TryGetValue(key, out var type) || !type.IsTag)
        throw new JsonException("Unknown or duplicate tag key.");

      definition.Tags.Add(type.Type, type.Descriptor);
    }

    return definition;
  }

  private sealed record StoreDocument(int FormatVersion, EntityDocument[] Entities);
  private sealed record EntityDocument(string Id, ComponentDocument[] Components, string[] Tags);
}
