using System.Collections.Concurrent;
using System.Reflection;
using Archy.Storage;

namespace Archy;

internal static class TypeCatalog {
  private static readonly ConcurrentDictionary<Type, TypeDescriptor> s_types = new();
  private static int s_nextId;

  public static ComponentDescriptor<T> Component<T>() where T : struct, IComponent =>
    Volatile.Read(ref ComponentCache<T>.Ready) ?? ComponentCache<T>.Value.Value;
  public static ComponentDescriptor<T>? ReadyComponent<T>() where T : struct, IComponent =>
    Volatile.Read(ref ComponentCache<T>.Ready);

  public static TypeDescriptor Tag<T>() where T : struct, ITag => TagCache<T>.Value.Value;

  private static ComponentDescriptor<T> CreateComponent<T>() where T : struct, IComponent {
    if (typeof(ITag).IsAssignableFrom(typeof(T)))
      throw new InvalidOperationException("A type cannot be both a component and a tag.");

    var descriptor = (ComponentDescriptor<T>)s_types.GetOrAdd(typeof(T), _ => new ComponentDescriptor<T>(Interlocked.Increment(ref s_nextId)));
    Volatile.Write(ref ComponentCache<T>.Ready, descriptor);

    return descriptor;
  }

  private static TypeDescriptor CreateTag<T>() where T : struct, ITag {
    if (typeof(IComponent).IsAssignableFrom(typeof(T)) || typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length != 0)
      throw new InvalidOperationException("Tags must be presence-only types without component data.");

    return s_types.GetOrAdd(typeof(T), _ => new TagDescriptor<T>(Interlocked.Increment(ref s_nextId)));
  }

  private static class ComponentCache<T> where T : struct, IComponent {
    internal static readonly Lazy<ComponentDescriptor<T>> Value = new(CreateComponent<T>);
    internal static ComponentDescriptor<T>? Ready;
  }

  private static class TagCache<T> where T : struct, ITag {
    internal static readonly Lazy<TypeDescriptor> Value = new(CreateTag<T>);
  }
}

internal abstract class TypeDescriptor {
  public int Id { get; }
  public Type Type { get; }
  public bool IsTag { get; }

  public TypeDescriptor(int id, Type type, bool isTag) {
    this.Id = id;
    this.Type = type;
    this.IsTag = isTag;
  }

  public virtual object SnapshotValue(EntityStore store, Entity entity) => throw new InvalidOperationException("Tags have no value.");
  public virtual PendingEvent? AdditionEvent(StoreEvents events, Entity entity) => null;
  public virtual PendingEvent? RemovalEvent(StoreEvents events, Entity entity) => null;

  public virtual ComponentColumn CreateColumn() => throw new InvalidOperationException("Tags have no value column.");
  public abstract void RegisterStorage(IEntityStorage storage);
}

internal sealed class ComponentDescriptor<T> : TypeDescriptor where T : struct, IComponent {
  private readonly IComponentCopy<T>? _copy;
  public bool HasCopyPolicy => this._copy is not null;
  public bool RequiresReference { get; }
  public IndexKey<T, Entity>? ReferenceKey { get; set; }
  public Func<EntityStore, IndexState<T>>? IndexFactory { get; set; }

  public ComponentDescriptor(int id) : base(id, typeof(T), false) {
    var referenceCount = ValueSafety.EntityReferences(typeof(T));
    var reference = typeof(T).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
      .Any(member => member.IsDefined(typeof(EntityReferenceAttribute), false));

    this.RequiresReference = reference;

    if (referenceCount > 1 || referenceCount > 0 && !reference)
      throw new InvalidOperationException("Entity fields require one generated EntityReference role per component.");

    var policy = typeof(T).GetCustomAttribute<ComponentCopyAttribute>();

    if (policy is not null) {
      this._copy = Activator.CreateInstance(policy.PolicyType, nonPublic: true) as IComponentCopy<T>
        ?? throw new InvalidOperationException($"Copy policy must implement IComponentCopy<{typeof(T).Name}>.");
    }
    else if (!ValueSafety.IsImmutable(typeof(T))) {
      throw new InvalidOperationException($"{typeof(T).Name} contains mutable references and requires a copy policy.");
    }
  }

  public T Copy(in T value) => this._copy is null ? value : this._copy.Copy(value);

  public override object SnapshotValue(EntityStore store, Entity entity) => store.Get<T>(entity);
  public override PendingEvent? RemovalEvent(StoreEvents events, Entity entity) => events.Removed<T>(entity);

  public override ComponentColumn CreateColumn() => new ComponentColumn<T>();
  public override void RegisterStorage(IEntityStorage storage) => storage.Register<T>();
}

internal static class ValueSafety {
  public static int EntityReferences(Type type) => EntityReferences(type, []);

  private static int EntityReferences(Type type, HashSet<Type> path) {
    if (type == typeof(Entity)) return 1;
    if (type == typeof(string) || type.IsPrimitive || type.IsEnum || !path.Add(type)) return 0;

    try {
      if (type.IsArray) return EntityReferences(type.GetElementType()!, path) > 0 ? 2 : 0;
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>))
        return EntityReferences(type.GenericTypeArguments[0], path) > 0 ? 2 : 0;

      var count = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Sum(field => EntityReferences(field.FieldType, path));

      return !type.IsValueType && count > 0 ? 2 : count;
    } finally { path.Remove(type); }
  }

  public static bool IsImmutable(Type type) {
    if (type == typeof(string) || type == typeof(Entity) || type.IsPrimitive || type.IsEnum) return true;
    if (type.IsDefined(typeof(ImmutableComponentValueAttribute), false)) return true;
    if (!type.IsValueType) return false;

    return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
      .All(field => IsImmutable(field.FieldType));
  }
}

internal sealed class TagDescriptor<T> : TypeDescriptor where T : struct, ITag {
  public TagDescriptor(int id) : base(id, typeof(T), true) {}
  public override void RegisterStorage(IEntityStorage storage) => storage.Register<T>();
  public override PendingEvent? AdditionEvent(StoreEvents events, Entity entity) =>
    events.AddedTag<T>(entity);
  public override PendingEvent? RemovalEvent(StoreEvents events, Entity entity) =>
    events.RemovedTag<T>(entity);
}
