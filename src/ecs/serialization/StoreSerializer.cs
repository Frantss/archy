using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Archy;

public sealed partial class StoreSerializer {
  private readonly Dictionary<string, PersistentType> _keys = new(StringComparer.Ordinal);
  private readonly Dictionary<Type, PersistentType> _types = new();
  private readonly Dictionary<(string Key, int Version), Func<JsonElement, JsonElement>> _migrations = new();
  private static readonly JsonSerializerOptions s_json = new() {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  public StoreSerializer(params Assembly[] assemblies) {
    foreach (var assembly in assemblies.Distinct()) {
      RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

      foreach (var type in PersistentCatalog.In(assembly)) {
        if (!this._keys.TryAdd(type.Key, type)) throw new InvalidOperationException($"Duplicate persistence key '{type.Key}'.");

        this._types.Add(type.Type, type);
      }
    }
  }

  public void RegisterMigration<T>(int fromVersion, Func<JsonElement, JsonElement> migrate) where T : struct, IComponent {
    ArgumentNullException.ThrowIfNull(migrate);
    var type = this.Type(typeof(T));

    if (fromVersion < 1 || fromVersion >= type.Version) throw new ArgumentOutOfRangeException(nameof(fromVersion));

    this._migrations.Add((type.Key, fromVersion), migrate);
  }

  public string WriteComponent<T>(in T value, ReferenceContext? references = null) where T : struct, IComponent {
    var type = this.Type(typeof(T));
    var envelope = new ComponentDocument(type.Key, type.Version, type.Write(value, new(references)));

    return JsonSerializer.Serialize(envelope, s_json);
  }

  public T ReadComponent<T>(string json, ReferenceContext? references = null) where T : struct, IComponent {
    var envelope = JsonSerializer.Deserialize<ComponentDocument>(json, s_json) ?? throw new JsonException("Missing component.");
    var (type, value) = this.Decode(envelope, new(references));

    if (type.Type != typeof(T)) throw new JsonException("Component key does not match the requested type.");

    return (T)value;
  }

  private (PersistentType Type, object Value) Decode(ComponentDocument envelope, ComponentCodecContext context) {
    if (string.IsNullOrWhiteSpace(envelope.Key) || !this._keys.TryGetValue(envelope.Key, out var type) || type.IsTag)
      throw new JsonException("Unknown component key.");
    if (envelope.Version < 1 || envelope.Version > type.Version || envelope.Data.ValueKind is JsonValueKind.Undefined)
      throw new JsonException("Invalid component version or data.");

    var data = envelope.Data;

    for (var version = envelope.Version; version < type.Version; version++) {
      if (!this._migrations.TryGetValue((type.Key, version), out var migration)) throw new JsonException("Component version requires an explicit migration.");

      data = migration(data).Clone();
    }

    return (type, type.Read(data, context));
  }

  private PersistentType Type(Type type) => this._types.TryGetValue(type, out var metadata) ? metadata
    : throw new JsonException($"Type '{type.Name}' has no selected generated persistence metadata.");

  private sealed record ComponentDocument(string Key, int Version, JsonElement Data);
}
