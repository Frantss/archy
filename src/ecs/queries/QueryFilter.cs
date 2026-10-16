namespace Archy;

public sealed class QueryFilter {
  private readonly Action _edit;
  private readonly EntityStore _store;
  private readonly List<Func<Entity[]>> _values = [];
  private readonly Group _components = new();
  private readonly Group _tags = new();

  internal QueryFilter(EntityStore store, Action edit) { this._store = store; this._edit = edit; }

  public QueryFilter HasValue<T, TKey>(TKey key) where T : struct, IComponent where TKey : notnull {
    this._edit();
    var index = this._store.Index<T, TKey>();
    this._values.Add(() => index.Snapshot(key));

    return this;
  }

  public QueryFilter ValueInRange<T, TKey>(TKey minimum, TKey maximum) where T : struct, IComponent where TKey : notnull {
    this._edit();
    var index = this._store.Index<T, TKey>();
    this._values.Add(() => index.Range(minimum, maximum));

    return this;
  }

  internal HashSet<Entity>? Candidates() => this._values.Count == 0 ? null : this._values.SelectMany(select => select()).ToHashSet();

  public QueryFilter AllComponents(ComponentTypes types) => this.Set(this._components, 0, types.Types);
  public QueryFilter AnyComponents(ComponentTypes types) => this.Set(this._components, 1, types.Types);
  public QueryFilter WithoutAllComponents(ComponentTypes types) => this.Set(this._components, 2, types.Types);
  public QueryFilter WithoutAnyComponents(ComponentTypes types) => this.Set(this._components, 3, types.Types);
  public QueryFilter AllTags(Tags types) => this.Set(this._tags, 0, types.Types);
  public QueryFilter AnyTags(Tags types) => this.Set(this._tags, 1, types.Types);
  public QueryFilter WithoutAllTags(Tags types) => this.Set(this._tags, 2, types.Types);
  public QueryFilter WithoutAnyTags(Tags types) => this.Set(this._tags, 3, types.Types);

  internal bool Matches(Archetype archetype) => this._components.Matches(archetype.ComponentMask)
    && this._tags.Matches(archetype.TagMask);

  private QueryFilter Set(Group group, int position, TypeDescriptor[]? types) {
    this._edit();
    group.Masks[position] = types is { Length: > 0 } ? new(types) : null;

    return this;
  }

  private sealed class Group {
    public TypeMask?[] Masks { get; } = new TypeMask?[4];

    public bool Matches(TypeMask mask) {
      var all = this.Masks[0];
      var any = this.Masks[1];
      var positive = all is null && any is null || all is not null && mask.HasAll(all) || any is not null && mask.HasAny(any);

      return positive && (this.Masks[2] is not { } withoutAll || !mask.HasAll(withoutAll))
        && (this.Masks[3] is not { } withoutAny || !mask.HasAny(withoutAny));
    }
  }
}
