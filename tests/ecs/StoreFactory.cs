using Archy.Adapters.Arch;
using Archy.Storage;

namespace Archy.Tests;

internal static class StoreFactory {
  public static IEntityStorage CreateStorage() => new ArchStorage();
  public static EntityStore Create() => new(CreateStorage());
}
