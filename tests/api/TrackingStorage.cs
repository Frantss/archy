using Archy.Adapters.Naive;
using Archy.Storage;

namespace Archy.Tests;

internal sealed class TrackingStorage : IEntityStorage {
  private readonly NaiveStorage _inner = new();
  public bool Disposed { get; private set; }

  public void Register<T>() where T : struct => this._inner.Register<T>();
  public IStorageArchetype CreateArchetype(ReadOnlySpan<Type> types) => this._inner.CreateArchetype(types);
  public void PrepareCapacity(IStorageArchetype archetype, int count) => this._inner.PrepareCapacity(archetype, count);
  public StorageLocation Create(IStorageArchetype archetype, StorageIdentity identity) =>
    this._inner.Create(archetype, identity);
  public StorageLocation Move(StorageLocation source, (IStorageArchetype Target, Type Type, bool Add) change) =>
    this._inner.Move(source, change);
  public void Remove(StorageLocation location) => this._inner.Remove(location);

  public void Dispose() {
    this._inner.Dispose();
    this.Disposed = true;
  }
}
