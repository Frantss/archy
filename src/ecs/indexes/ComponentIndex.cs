using System.Collections;

namespace Archy;

public readonly struct ComponentIndex<T, TKey> where T : struct, IComponent where TKey : notnull {
  private readonly ValueIndex<T, TKey> _index;

  internal ComponentIndex(ValueIndex<T, TKey> index) => this._index = index;

  public EntityCollection this[TKey key] => this._index.Collection(key);

  public IReadOnlyList<TKey> Values => this._index.Values();

  public bool TryGet(TKey key, out Entity entity) {
    if (!this._index.Unique) throw new InvalidOperationException("Single-entity lookup requires a unique index.");

    return this._index.TryGet(key, out entity);
  }
}

public readonly struct EntityCollection : IReadOnlyList<Entity> {
  private readonly IEntityCollectionSource? _source;
  private readonly object? _key;

  internal EntityCollection(IEntityCollectionSource source, object key) {
    this._source = source;
    this._key = key;
  }

  public int Count => this._source?.Count(this._key!) ?? 0;
  public Entity this[int index] => this._source is {} source ? source.At(this._key!, index) : Array.Empty<Entity>()[index];
  public IEnumerator<Entity> GetEnumerator() => this._source?.Enumerate(this._key!) ?? Enumerable.Empty<Entity>().GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}

internal interface IEntityCollectionSource {
  int Count(object key);
  Entity At(object key, int index);
  IEnumerator<Entity> Enumerate(object key);
}
