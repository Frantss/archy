using Archy.Adapters.Naive;
using Archy.Storage;

namespace Archy.Tests;

internal static class StoreFactory {
  public static IEntityStorage CreateStorage() => new NaiveStorage();
  public static EntityStore Create() => new(CreateStorage());
}
