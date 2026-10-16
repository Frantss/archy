namespace Archy;

public enum ChangeOperation { Added, Updated, Removed }
public readonly record struct EntityCreated(Entity Entity, long Sequence, EntitySnapshot Snapshot);
public readonly record struct EntityModified(Entity Entity, long Sequence, Type Type, ChangeOperation Operation);
public readonly record struct EntityDeleted(Entity Entity, long Sequence, EntitySnapshot Snapshot);
public readonly record struct ComponentAdded<T>(Entity Entity, long Sequence, T NewValue) where T : struct, IComponent;
public readonly record struct ComponentUpdated<T>(Entity Entity, long Sequence, T OldValue, T NewValue) where T : struct, IComponent;
public readonly record struct ComponentRemoved<T>(Entity Entity, long Sequence, T OldValue) where T : struct, IComponent;
public readonly record struct TagAdded<T>(Entity Entity, long Sequence) where T : struct, ITag;
public readonly record struct TagRemoved<T>(Entity Entity, long Sequence) where T : struct, ITag;

public sealed class EntitySnapshot {
  private readonly object[] _values;
  private readonly SnapshotShape _shape;
  public Entity Entity { get; }

  internal EntitySnapshot(Entity entity, object[] values, SnapshotShape shape) {
    this.Entity = entity;
    this._values = values;
    this._shape = shape;
  }

  public bool Has<T>() where T : struct, IComponent => this._shape.Columns.ContainsKey(typeof(T));
  public bool HasTag<T>() where T : struct, ITag => this._shape.Tags.Contains(typeof(T));
  public T Get<T>() where T : struct, IComponent => this._shape.Columns.TryGetValue(typeof(T), out var column)
    ? TypeCatalog.Component<T>().Copy((T)this._values[column]) : throw new MutationException("Snapshot component is absent.");
}

internal sealed class SnapshotShape {
  public Dictionary<Type, int> Columns { get; }
  public HashSet<Type> Tags { get; }

  public SnapshotShape(TypeDescriptor[] components, TypeDescriptor[] tags) {
    this.Columns = new(components.Length);
    this.Tags = tags.Select(type => type.Type).ToHashSet();

    for (var i = 0; i < components.Length; i++) this.Columns.Add(components[i].Type, i);
  }
}
