namespace Archy;

public sealed class PrefabBuilder {
  private readonly EntityDefinition _definition;
  private bool _preparing;
  public PrefabBuilder() => this._definition = new();
  internal PrefabBuilder(EntityDefinition definition) => this._definition = Clone(definition);

  public bool Has<T>() where T : struct, IComponent => this._definition.Components.ContainsKey(typeof(T));
  public bool HasTag<T>() where T : struct, ITag => this._definition.Tags.ContainsKey(typeof(T));
  public T Get<T>() where T : struct, IComponent => this._definition.Components.TryGetValue(typeof(T), out var value)
    ? (T)value.CopyValue() : throw new MutationException("Prefab component is absent.");
  public void Add<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Add);
  public void Update<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Update);
  public void Upsert<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Upsert);
  public bool TryAdd<T>(in T value) where T : struct, IComponent => this.TryWrite(value, WriteMode.Add);
  public bool TryUpdate<T>(in T value) where T : struct, IComponent => this.TryWrite(value, WriteMode.Update);
  public bool TryUpsert<T>(in T value) where T : struct, IComponent => this.TryWrite(value, WriteMode.Upsert);
  public void Remove<T>() where T : struct, IComponent {
    if (!this.TryRemove<T>()) throw new MutationException("Prefab component is absent.");
  }

  public bool TryRemove<T>() where T : struct, IComponent { this.EnsureEdit(); return this._definition.Components.Remove(typeof(T)); }
  public void AddTag<T>() where T : struct, ITag {
    if (!this.TryAddTag<T>()) throw new MutationException("Prefab tag is already present.");
  }

  public bool TryAddTag<T>() where T : struct, ITag {
    this.EnsureEdit();

    return this._definition.Tags.TryAdd(typeof(T), TypeCatalog.Tag<T>());
  }

  public void RemoveTag<T>() where T : struct, ITag {
    if (!this.TryRemoveTag<T>()) throw new MutationException("Prefab tag is absent.");
  }

  public bool TryRemoveTag<T>() where T : struct, ITag { this.EnsureEdit(); return this._definition.Tags.Remove(typeof(T)); }
  public Prefab Build() {
    this.EnsureEdit();
    this._preparing = true;

    try { return new(Clone(this._definition)); } finally { this._preparing = false; }
  }

  private void Write<T>(in T value, WriteMode mode) where T : struct, IComponent {
    if (!this.TryWrite(value, mode)) throw new MutationException("Prefab component state does not match the operation.");
  }

  private bool TryWrite<T>(in T value, WriteMode mode) where T : struct, IComponent {
    this.EnsureEdit();
    var exists = this.Has<T>();

    if (mode == WriteMode.Add && exists || mode == WriteMode.Update && !exists) return false;

    ComponentValue<T> copy;
    this._preparing = true;

    try { copy = new(value); } finally { this._preparing = false; }

    this._definition.Components[typeof(T)] = copy;

    return true;
  }

  private void EnsureEdit() {
    if (this._preparing) throw new InvalidOperationException("Copy policies cannot reenter prefab edits.");
  }

  internal static EntityDefinition Clone(EntityDefinition source) {
    var result = new EntityDefinition();

    foreach (var (type, value) in source.Components) result.Components.Add(type, value.Clone());
    foreach (var (type, tag) in source.Tags) result.Tags.Add(type, tag);

    return result;
  }
}

public sealed class Prefab {
  internal EntityDefinition Definition { get; }
  internal Prefab(EntityDefinition definition) => this.Definition = definition;
  public PrefabBuilder ToBuilder() => new(this.Definition);
}
