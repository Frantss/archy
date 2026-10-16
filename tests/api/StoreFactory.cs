using Archy.Storage;

namespace Archy.Tests;

internal static class StoreFactory {
  public static IEntityStorage CreateStorage() => new MemoryStorage();
  public static EntityStore Create() => new(CreateStorage());
}
