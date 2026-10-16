using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archy;

public sealed class ReferenceContext {
  private readonly Dictionary<string, Entity> _read;
  private readonly Dictionary<Entity, string> _write;

  public ReferenceContext(IReadOnlyDictionary<string, Entity> entities) {
    ArgumentNullException.ThrowIfNull(entities);
    this._read = new(entities, StringComparer.Ordinal);
    this._write = entities.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);

    if (entities.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value == default))
      throw new ArgumentException("Reference IDs and handles must be nonempty.", nameof(entities));
  }

  public string? Write(Entity entity) => entity == default ? null : this._write.TryGetValue(entity, out var id)
    ? id : throw new JsonException("Entity has no document reference ID.");
  public Entity Read(string? id) => id is null ? default : this._read.TryGetValue(id, out var entity)
    ? entity : throw new JsonException($"Unresolved entity reference '{id}'.");
}

public sealed class ComponentCodecContext {
  public ReferenceContext? References { get; }
  public JsonSerializerOptions Options { get; }

  internal ComponentCodecContext(ReferenceContext? references) {
    this.References = references;
    this.Options = new() {
      IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    this.Options.Converters.Add(new EntityConverter(references));
    this.Options.MakeReadOnly(populateMissingResolver: true);
  }

  private sealed class EntityConverter : JsonConverter<Entity> {
    private readonly ReferenceContext? _references;
    public EntityConverter(ReferenceContext? references) => this._references = references;
    public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      (this._references ?? throw new JsonException("Entity values require a reference context.")).Read(reader.GetString());
    public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options) =>
      writer.WriteStringValue((this._references ?? throw new JsonException("Entity values require a reference context.")).Write(value));
  }
}
