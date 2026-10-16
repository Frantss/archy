namespace Archy;

public sealed partial class EntityStore {
  public Entity Instantiate(Prefab prefab) {
    ArgumentNullException.ThrowIfNull(prefab);

    return this.Construct(1, _ => [PrefabBuilder.Clone(prefab.Definition)])[0];
  }
}
