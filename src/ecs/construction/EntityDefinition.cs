using System.Text.Json;

namespace Archy;

internal sealed class EntityDefinition {
  public Dictionary<Type, ComponentValue> Components { get; } = new();
  public Dictionary<Type, TypeDescriptor> Tags { get; } = new();
}

internal abstract class ComponentValue {
  public abstract TypeDescriptor Descriptor { get; }
  public abstract object CopyValue();
  public abstract ComponentValue Clone();
  public abstract ComponentValue Remap(ReferenceContext source, ReferenceContext destination);
  public abstract ComponentPlacement Prepare(EntityStore store, Entity entity);
}

internal sealed class ComponentValue<T> : ComponentValue where T : struct, IComponent {
  private readonly T _value;
  public override TypeDescriptor Descriptor => TypeCatalog.Component<T>();
  public ComponentValue(in T value) => this._value = TypeCatalog.Component<T>().Copy(value);
  public override object CopyValue() => TypeCatalog.Component<T>().Copy(this._value);
  public override ComponentValue Clone() => new ComponentValue<T>(this._value);
  public override ComponentValue Remap(ReferenceContext source, ReferenceContext destination) {
    var descriptor = TypeCatalog.Component<T>();

    if (descriptor.ReferenceKey is not { } key) return this.Clone();

    var expected = destination.Read(source.Write(key(this._value)));
    var metadata = PersistentCatalog.Find(typeof(T));
    var codec = new JsonComponentCodec<T>();
    var data = metadata is null ? codec.Write(this._value, new(source)) : metadata.Write(this._value, new(source));
    var remapped = metadata is null ? codec.Read(data, new(destination)) : (T)metadata.Read(data, new(destination));

    if (key(remapped) != expected) throw new JsonException("Component codec did not preserve its declared entity-reference member.");

    return new ComponentValue<T>(remapped);
  }

  public override ComponentPlacement Prepare(EntityStore store, Entity entity) => store.PreparePlacement(entity, this._value);
}

internal sealed record ComponentPlacement(TypeDescriptor Descriptor, Func<object> Snapshot, Action Commit, PendingEvent? Added);
