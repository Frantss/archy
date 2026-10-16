namespace Archy.Storage;

public interface IEntityStorage : IDisposable {
  void Register<T>() where T : struct;
  IStorageArchetype CreateArchetype(ReadOnlySpan<Type> types);
  void PrepareCapacity(IStorageArchetype archetype, int count);
  StorageLocation Create(IStorageArchetype archetype, StorageIdentity identity);
  StorageLocation Move(StorageLocation source, (IStorageArchetype Target, Type Type, bool Add) change);
  void Remove(StorageLocation location);
}

public interface IStorageArchetype {
  int EntityCount { get; }
  int ChunkCount { get; }
  IStorageChunk GetChunk(int index);
}

public interface IStorageChunk {
  int Count { get; }
  StorageIdentity[] Identities { get; }
  Array GetComponents(Type type);
}

public readonly struct StorageIdentity {
  public int Id { get; init; }
  public uint Generation { get; init; }
}

public readonly struct StorageLocation {
  public IStorageArchetype Archetype { get; init; }
  public int ChunkIndex { get; init; }
  public int Row { get; init; }
}
