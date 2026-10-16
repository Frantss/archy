namespace Archy;

public sealed partial class EntityStore {
  private readonly Dictionary<int, IndexState> _indexes = new();
  private bool _hasReferenceIndexes;

  public ComponentIndex<T, TKey> ComponentIndex<T, TKey>() where T : struct, IComponent where TKey : notnull {
    return new(this.Index<T, TKey>());
  }

  internal ValueIndex<T, TKey> Index<T, TKey>() where T : struct, IComponent where TKey : notnull {
    this.EnsureAccess();
    using var preparation = this.Prepare();
    var state = this.GetIndex(TypeCatalog.Component<T>());

    if (state is not ValueIndex<T, TKey> typed)
      throw new InvalidOperationException("Component has no generated index with the requested key type.");

    return typed;
  }

  private IndexState<T>? GetIndex<T>(ComponentDescriptor<T> descriptor) where T : struct, IComponent {
    if (descriptor.IndexFactory is null) {
      if (descriptor.RequiresReference) throw new InvalidOperationException("Reference components require generated metadata.");

      return null;
    }

    if (!this._indexes.TryGetValue(descriptor.Id, out var index)) {
      index = descriptor.IndexFactory(this);
      this._indexes.Add(descriptor.Id, index);
      if (index.ReferencePolicy is not null) this._hasReferenceIndexes = true;

      if (this._construction is not null) index.BeginStaging();
    }

    return (IndexState<T>)index;
  }
}
