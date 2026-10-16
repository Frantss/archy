using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Archy;

internal static class PersistentCatalog {
  private static readonly ConcurrentDictionary<Type, PersistentType> s_types = new();
  internal static void Add(PersistentType type) {
    if (!s_types.TryAdd(type.Type, type)) throw new InvalidOperationException("Persistence type registered twice.");
  }

  internal static PersistentType? Find(Type type) => s_types.GetValueOrDefault(type);
  internal static IEnumerable<PersistentType> In(Assembly assembly) => s_types.Values.Where(type => type.Type.Assembly == assembly);
}

internal abstract class PersistentType {
  public string Key { get; }
  public int Version { get; }
  public TypeDescriptor Descriptor { get; }
  public Type Type => this.Descriptor.Type;
  public bool IsTag => this.Descriptor.IsTag;
  public bool HasReferences => ValueSafety.EntityReferences(this.Type) != 0;

  protected PersistentType(string key, int version, TypeDescriptor descriptor) {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
    this.Key = key;
    this.Version = version;
    this.Descriptor = descriptor;

    if (this.Type.IsDefined(typeof(TransientAttribute), false)) throw new InvalidOperationException("Transient types cannot declare persistence keys.");
  }

  public virtual JsonElement Write(object value, ComponentCodecContext context) => throw new JsonException("Tags have no component data.");
  public virtual object Read(JsonElement data, ComponentCodecContext context) => throw new JsonException("Tags have no component data.");
  public abstract void Apply(EntityStore store, Entity entity, object? value);
  public virtual ComponentValue Value(object value) => throw new JsonException("Tags have no value.");
}

internal sealed class PersistentComponent<T> : PersistentType where T : struct, IComponent {
  private readonly IComponentCodec<T> _codec;
  private readonly ComponentDescriptor<T> _descriptor;
  public PersistentComponent(string key, int version, IComponentCodec<T>? codec) : base(key, version, TypeCatalog.Component<T>()) {
    this._codec = codec ?? new JsonComponentCodec<T>();
    this._descriptor = TypeCatalog.Component<T>();
  }

  public override JsonElement Write(object value, ComponentCodecContext context) {
    this.Validate(context);
    var copy = this._descriptor.Copy((T)value);

    return this._codec.Write(copy, context).Clone();
  }

  public override object Read(JsonElement data, ComponentCodecContext context) {
    this.Validate(context);
    var value = this._codec.Read(data, context);

    return this._descriptor.Copy(value);
  }

  public override void Apply(EntityStore store, Entity entity, object? value) => entity.Add((T)value!);
  public override ComponentValue Value(object value) => new ComponentValue<T>((T)value);
  private void Validate(ComponentCodecContext context) {
    if (this.HasReferences && context.References is null) throw new JsonException("Reference-bearing components require a reference context.");
  }
}

internal sealed class PersistentTag<T> : PersistentType where T : struct, ITag {
  public PersistentTag(string key) : base(key, 1, TypeCatalog.Tag<T>()) {}
  public override void Apply(EntityStore store, Entity entity, object? value) => entity.AddTag<T>();
}

public static partial class GeneratedComponents {
  public static void RegisterComponent<T>(string key, int version, IComponentCodec<T>? codec = null) where T : struct, IComponent =>
    PersistentCatalog.Add(new PersistentComponent<T>(key, version, codec));
  public static void RegisterTag<T>(string key) where T : struct, ITag => PersistentCatalog.Add(new PersistentTag<T>(key));
}
