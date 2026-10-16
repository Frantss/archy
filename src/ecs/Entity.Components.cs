using System.Runtime.CompilerServices;

namespace Archy;

public readonly partial record struct Entity {
  public T Get<T>() where T : struct, IComponent => this.RequireStore().Get<T>(this);

  public bool TryGet<T>(out T value) where T : struct, IComponent {
    if (this.Store is { } store) return store.TryGet(this, out value);

    value = default;

    return false;
  }

  public bool Has<T>() where T : struct, IComponent => this.Store?.Has<T>(this) == true;
  public bool HasTag<T>() where T : struct, ITag => this.Store?.HasTag<T>(this) == true;

  public void Add<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Add);
  public void Update<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Update);
  public void Upsert<T>(in T value) where T : struct, IComponent => this.Write(value, WriteMode.Upsert);
  public bool TryAdd<T>(in T value) where T : struct, IComponent => this.Store?.TryWrite(this, value, WriteMode.Add) == true;
  public bool TryUpdate<T>(in T value) where T : struct, IComponent => this.Store?.TryWrite(this, value, WriteMode.Update) == true;
  public bool TryUpsert<T>(in T value) where T : struct, IComponent => this.Store?.TryWrite(this, value, WriteMode.Upsert) == true;

  public void Remove<T>() where T : struct, IComponent {
    if (!this.TryRemove<T>()) throw new MutationException("Entity or component is absent.");
  }

  public bool TryRemove<T>() where T : struct, IComponent => this.Store?.TryRemove<T>(this) == true;

  public void AddTag<T>() where T : struct, ITag {
    if (!this.TryAddTag<T>()) throw new MutationException("Entity is absent or tag already exists.");
  }

  public bool TryAddTag<T>() where T : struct, ITag => this.Store?.TryTag<T>(this, true) == true;

  public void RemoveTag<T>() where T : struct, ITag {
    if (!this.TryRemoveTag<T>()) throw new MutationException("Entity or tag is absent.");
  }

  public bool TryRemoveTag<T>() where T : struct, ITag => this.Store?.TryTag<T>(this, false) == true;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void Write<T>(in T value, WriteMode mode) where T : struct, IComponent {
    if (!this.RequireStore().TryWrite(this, value, mode))
      throw new MutationException($"Cannot {mode} component {typeof(T).Name}: entity or component state does not match.");
  }
}

internal enum WriteMode { Add, Update, Upsert }
