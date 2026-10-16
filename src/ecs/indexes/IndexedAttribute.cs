namespace Archy;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method)]
public sealed class IndexedAttribute : Attribute {
  public bool Unique { get; set; }
  public Type? Comparer { get; set; }
}

public delegate TKey IndexKey<T, out TKey>(in T component) where T : struct, IComponent;

[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static partial class GeneratedComponents {
  public static void RegisterIndex<T, TKey>(IndexKey<T, TKey> key, bool unique = false, IEqualityComparer<TKey>? comparer = null,
    TargetDeletion? reference = null, bool optional = false)
    where T : struct, IComponent where TKey : notnull {
    ArgumentNullException.ThrowIfNull(key);

    if (!ValueSafety.IsImmutable(typeof(TKey))) throw new InvalidOperationException("Index keys must be immutable.");

    var descriptor = TypeCatalog.Component<T>();

    if (descriptor.IndexFactory is not null) throw new InvalidOperationException("Component index is already registered.");

    if (reference is not null && (typeof(TKey) != typeof(Entity) || comparer is not null))
      throw new InvalidOperationException("References require an Entity key with its default comparer.");

    if (reference is { } policy && !Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(reference));

    if (reference is not null) descriptor.ReferenceKey = (in T value) => (Entity)(object)key(value);

    descriptor.IndexFactory = store => new ValueIndex<T, TKey>(store, key, unique, comparer, reference, optional);
  }
}
