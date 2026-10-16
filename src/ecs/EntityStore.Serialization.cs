namespace Archy;

public sealed partial class EntityStore {
  internal TypeDescriptor[] Composition(Entity entity) {
    var archetype = this._slots[entity.Id].Archetype!;

    return [..archetype.Components, ..archetype.Tags];
  }
}
