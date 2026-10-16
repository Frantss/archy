namespace Archy;

public abstract class QueryBase<TSelf> where TSelf : QueryBase<TSelf> {
  private readonly EntityStore _store;
  private readonly TypeMask _required;
  private readonly TypeDescriptor[] _requiredTypes;
  private Archetype[] _plan = [];
  private Dictionary<Archetype, int>? _planOrder;
  private int _storeVersion = -1;
  private int _active;
  private bool _frozen;

  public QueryFilter Filter { get; }
  public IEnumerable<Entity> Entities => this.Iterate();
  public int Count {
    get {
      using var traversal = this.Traverse(selectRows: false);

      return traversal.Count;
    }
  }

  private protected QueryBase(EntityStore store, TypeDescriptor[] required) {
    this._store = store;
    this._required = new(required);
    this._requiredTypes = required;
    this.Filter = new(store, this.Edit);
  }

  public TSelf AllComponents(ComponentTypes types) { this.Filter.AllComponents(types); return (TSelf)this; }
  public TSelf AnyComponents(ComponentTypes types) { this.Filter.AnyComponents(types); return (TSelf)this; }
  public TSelf WithoutAllComponents(ComponentTypes types) { this.Filter.WithoutAllComponents(types); return (TSelf)this; }
  public TSelf WithoutAnyComponents(ComponentTypes types) { this.Filter.WithoutAnyComponents(types); return (TSelf)this; }
  public TSelf AllTags(Tags types) { this.Filter.AllTags(types); return (TSelf)this; }
  public TSelf AnyTags(Tags types) { this.Filter.AnyTags(types); return (TSelf)this; }
  public TSelf WithoutAllTags(Tags types) { this.Filter.WithoutAllTags(types); return (TSelf)this; }
  public TSelf WithoutAnyTags(Tags types) { this.Filter.WithoutAnyTags(types); return (TSelf)this; }

  public TSelf HasValue<T, TKey>(TKey key) where T : struct, IComponent where TKey : notnull {
    this.Filter.HasValue<T, TKey>(key);

    return (TSelf)this;
  }

  public TSelf ValueInRange<T, TKey>(TKey minimum, TKey maximum) where T : struct, IComponent where TKey : notnull {
    this.Filter.ValueInRange<T, TKey>(minimum, maximum);

    return (TSelf)this;
  }

  public TSelf FreezeFilter() {
    this._store.EnsureAccess();
    this._frozen = true;

    return (TSelf)this;
  }

  private void Edit() {
    this._store.EnsureAccess();

    if (this._frozen || this._active > 0) throw new InvalidOperationException("Query filter is frozen or in use.");

    this._storeVersion = -1;
  }

  private protected Traversal Traverse(bool selectRows = true) {
    this._store.EnsureAccess();
    this._active++;
    this._store.BeginQuery();

    try {
      if (this._storeVersion != this._store.ArchetypeVersion) {
        this._plan = this._store.MatchArch(this._requiredTypes, this.Filter);
        this._storeVersion = this._store.ArchetypeVersion;
        this._planOrder = null;
      }

      var candidates = this.Filter.Candidates();
      var population = 0;

      if (candidates is not null) {
        foreach (var archetype in this._plan) population += archetype.Count;
      }

      var sparse = candidates is not null && candidates.Count < population / 8;
      var chunks = sparse && selectRows ? this.SelectCandidates(candidates!) : null;

      return new(this, this._plan, candidates, sparse, chunks);
    } catch {
      this.EndTraversal();
      throw;
    }
  }

  private bool Matches(QueryLocation location) => location.Archetype.ComponentMask.HasAll(this._required)
    && this.Filter.Matches(location.Archetype);

  private QueryChunk[] SelectCandidates(HashSet<Entity> candidates) {
    this._planOrder ??= this._plan.Select((archetype, index) => (archetype, index))
      .ToDictionary(item => item.archetype, item => item.index);
    var groups = new Dictionary<StorageChunk, (Archetype Archetype, List<int> Rows)>();

    foreach (var entity in candidates) {
      if (!this._store.TryQueryLocation(entity, out var location) || !this.Matches(location)) continue;

      if (!groups.TryGetValue(location.Chunk, out var group)) {
        group = (location.Archetype, []);
        groups.Add(location.Chunk, group);
      }

      group.Rows.Add(location.Row);
    }

    return groups.Select(pair => {
      pair.Value.Rows.Sort();

      return new QueryChunk(pair.Value.Archetype, pair.Key, pair.Value.Rows.ToArray());
    }).OrderBy(chunk => this._planOrder[chunk.Archetype])
      .ThenBy(chunk => chunk.Archetype.Chunks.IndexOf(chunk.Storage)).ToArray();
  }

  private void EndTraversal() {
    this._active--;
    this._store.ExitQuery();
  }

  internal IEnumerable<TResult> Batches<TResult>(Func<QueryBatch, TResult> project) {
    using var traversal = this.Traverse();
    using var lease = new QueryLease(this._store);

    foreach (var chunk in traversal) {
      var rows = chunk.Rows;

      if (rows is null && traversal.Candidates is not null) {
        var selected = new List<int>();

        for (var row = 0; row < chunk.Storage.Count; row++) {
          if (traversal.Includes(chunk, row)) selected.Add(row);
        }

        if (selected.Count == 0) continue;

        rows = selected.ToArray();
      }

      yield return project(new(chunk.Storage, lease, rows));
    }
  }

  private IEnumerable<Entity> Iterate() {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      if (chunk.Rows is {} selected) {
        foreach (var row in selected) {
          this._store.EnsureAccess();
          yield return traversal.EntityAt(chunk, row);
        }

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        this._store.EnsureAccess();

        if (traversal.Includes(chunk, row)) yield return traversal.EntityAt(chunk, row);
      }
    }
  }

  internal readonly struct Traversal : IDisposable {
    private readonly QueryBase<TSelf> _owner;
    private readonly Archetype[] _plan;
    private readonly bool _sparse;
    private readonly QueryChunk[]? _selected;
    public HashSet<Entity>? Candidates { get; }

    public Traversal(QueryBase<TSelf> owner, Archetype[] plan, HashSet<Entity>? candidates, bool sparse, QueryChunk[]? selected) {
      this._owner = owner;
      this._plan = plan;
      this.Candidates = candidates;
      this._sparse = sparse;
      this._selected = selected;
    }

    public int Count {
      get {
        var count = 0;

        if (this.Candidates is null) {
          foreach (var archetype in this._plan) count += archetype.Count;
        } else if (this._sparse) {
          foreach (var entity in this.Candidates) {
            if (this._owner._store.TryQueryLocation(entity, out var location) && this._owner.Matches(location)) count++;
          }
        } else {
          foreach (var chunk in this) {
            for (var row = 0; row < chunk.Storage.Count; row++) {
              if (this.Includes(chunk, row)) count++;
            }
          }
        }

        return count;
      }
    }

    public bool Includes(QueryChunk chunk, int row) => this.Candidates is null
      || this.Candidates.Contains(this.EntityAt(chunk, row));
    public Entity EntityAt(QueryChunk chunk, int row) => chunk.Storage.EntityAt(this._owner._store, row);
    public Entity CallbackEntityAt(QueryChunk chunk, int row) {
      var entity = chunk.Storage.EntityAt(this._owner._store, row);
      this._owner._store.SelectBoundWrite(chunk.Storage, row, entity);

      return entity;
    }
    public EntityStore.QueryWrites BindWrites(QueryChunk chunk) => this._owner._store.BindWrites(chunk.Storage);
    public BoundColumn<T> Bind<T>(QueryChunk chunk) where T : struct, IComponent =>
      new(this._owner._store, chunk.Archetype, chunk.Storage);
    public ChunkEnumerator GetEnumerator() => new(this._plan, this._selected);
    public void Dispose() => this._owner.EndTraversal();
  }

  internal struct ChunkEnumerator {
    private readonly Archetype[] _plan;
    private int _archetype;
    private int _chunk;
    private readonly QueryChunk[]? _selected;
    public QueryChunk Current { get; private set; }

    public ChunkEnumerator(Archetype[] plan, QueryChunk[]? selected) {
      this._plan = plan;
      this._selected = selected;
      this._archetype = 0;
      this._chunk = 0;
    }

    public bool MoveNext() {
      if (this._selected is {} selected) {
        if (this._chunk == selected.Length) return false;

        this.Current = selected[this._chunk++];

        return true;
      }

      while (this._archetype < this._plan.Length) {
        var archetype = this._plan[this._archetype];

        if (this._chunk < archetype.Chunks.Count) {
          this.Current = new(archetype, archetype.Chunks[this._chunk++]);

          return true;
        }

        this._archetype++;
        this._chunk = 0;
      }

      return false;
    }
  }
}

internal readonly struct QueryBatch {
  public StorageChunk Chunk { get; }
  public QueryLease Lease { get; }
  public int[]? Rows { get; }
  public int Count { get; }
  public Archetype Archetype => this.Chunk.Owner;

  public QueryBatch(StorageChunk chunk, QueryLease lease, int[]? rows) {
    this.Chunk = chunk;
    this.Lease = lease;
    this.Rows = rows;
    this.Count = rows?.Length ?? chunk.Count;
  }
  public Entity EntityAt(int index) {
    this.Check();
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this.Count);

    var row = this.Rows?[index] ?? index;
    var entity = this.Chunk.EntityAt(this.Lease.Store, row);
    this.Lease.Store.SelectChunkWrite(this.Lease, this.Chunk, row, entity);

    return entity;
  }

  public void Check() => this.Lease.Check();
}

internal sealed class QueryLease : IDisposable {
  public EntityStore Store { get; }
  public bool Active { get; private set; } = true;
  public QueryLease(EntityStore store) => this.Store = store;

  public void Check() {
    this.Store.EnsureAccess();

    if (!this.Active) throw new InvalidOperationException("Query chunk escaped its traversal.");
  }

  public void Dispose() {
    if (!this.Active) return;

    this.Active = false;
    this.Store.EndChunkWrites(this);
  }
}

internal readonly record struct QueryChunk(Archetype Archetype, StorageChunk Storage, int[]? Rows = null);
