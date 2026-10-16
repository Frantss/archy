namespace Archy;

public readonly partial record struct Entity {
  public EntityStore? Store { get; }
  public int Id { get; }
  public uint Generation { get; }
  public bool IsAlive => this.Store?.IsAlive(this) == true;

  internal Entity(EntityStore? store, int id, uint generation) {
    this.Store = store;
    this.Id = id;
    this.Generation = generation;
  }

  public void Delete() => this.RequireStore().Delete(this);

  public bool TryDelete() => this.Store?.TryDelete(this) == true;

  internal EntityStore RequireStore() => this.Store ?? throw new MutationException("Entity has no store.");

  public override string ToString() => $"Entity({this.Id}:{this.Generation})";
}
