using System.Collections;

namespace Archy;

internal abstract class IndexState {
  public virtual TargetDeletion? ReferencePolicy => null;
  public virtual Entity[] Incoming(Entity target) => [];
  public abstract void Remove(Entity entity);
  public virtual void BeginStaging() {}
  public virtual void EndStaging() {}
  public abstract void Clear();
}

internal abstract class IndexState<T> : IndexState where T : struct, IComponent {
  public abstract bool PrepareWrite(Entity entity, in T value);
  public abstract void CommitWrite(Entity entity);
  public abstract Action? StageWrite(Entity entity, in T value);
}

internal sealed class ValueIndex<T, TKey> : IndexState<T>, IEntityCollectionSource where T : struct, IComponent where TKey : notnull {
  private static readonly object s_nullKey = new();
  private readonly EntityStore _store;
  private readonly IndexKey<T, TKey> _key;
  private readonly IEqualityComparer<TKey> _comparer;
  private readonly Dictionary<int, List<Bucket>> _buckets = new();
  private readonly Dictionary<TKey, Bucket>? _primitiveBuckets;
  private Dictionary<TKey, Bucket>? _primitiveStaged;
  private const int EntryPageBits = 8;
  private const int EntryPageSize = 1 << EntryPageBits;
  private Entry[]?[] _entries = [];
  private Bucket? _prepared;
  private readonly Stack<Bucket> _spareBuckets = new();
  private List<Entity>? _spareEntities;
  private List<Bucket>? _spareChain;
  private int _preparedHash;
  private readonly bool _optional;
  private Dictionary<int, List<Bucket>>? _staged;
  private readonly Dictionary<Bucket, Entity> _claims = new();
  public override TargetDeletion? ReferencePolicy { get; }

  public bool Unique { get; }

  public ValueIndex(EntityStore store, IndexKey<T, TKey> key, bool unique, IEqualityComparer<TKey>? comparer, TargetDeletion? reference, bool optional) {
    this._store = store;
    this.ReferencePolicy = reference;
    this._optional = optional;
    this._key = key;
    this.Unique = unique;
    this._comparer = comparer ?? EqualityComparer<TKey>.Default;

    if (comparer is not null && ReferenceEquals(comparer, EqualityComparer<TKey>.Default)
      && (typeof(TKey).IsPrimitive || typeof(TKey).IsEnum || typeof(TKey) == typeof(decimal) || typeof(TKey) == typeof(Guid)))
      this._primitiveBuckets = new();
  }

  public override bool PrepareWrite(Entity entity, in T value) {
    if (this._staged is null && this._primitiveStaged is null && this._prepared is { Attached: false } unused) this.Recycle(unused);

    this._prepared = null;
    var key = this._key(value);

    if (!typeof(TKey).IsValueType && key is null) return false;

    if (this.ReferencePolicy is not null) {
      var target = (Entity)(object)key;

      if (!(target == default && this._optional) && !this._store.ValidReference(target)) return false;
    }

    var hash = this.Hash(key);
    var bucket = this.Find(key, hash);

    if (bucket is null && this._primitiveStaged is not null) this._primitiveStaged.TryGetValue(key, out bucket);
    if (bucket is null && this._staged is not null && this._staged.TryGetValue(hash, out var staged)) bucket = this.Find(staged, key);

    if (this.Unique && bucket is not null && (bucket.Entities.Count > 0 && bucket.Entities[0] != entity
      || this._claims.TryGetValue(bucket, out var claim) && claim != entity)) return false;

    if (bucket is null) {
      bucket = this._spareBuckets.TryPop(out var spare) ? spare : new Bucket();
      bucket.Initialize(key, hash);
    }

    this.PrepareEntry(entity.Id);
    this._prepared = bucket;
    this._preparedHash = hash;

    return true;
  }

  public override void BeginStaging() {
    if (this._primitiveBuckets is not null) this._primitiveStaged = new();
    else this._staged = new();

    this._claims.Clear();
  }
  public override void EndStaging() {
    if (this._staged is {} staged) {
      foreach (var chain in staged.Values) {
        foreach (var bucket in chain) {
          if (!bucket.Attached) this.Recycle(bucket);
        }
      }
    }

    if (this._primitiveStaged is {} primitive) {
      foreach (var bucket in primitive.Values) {
        if (!bucket.Attached) this.Recycle(bucket);
      }
    }

    this._staged = null;
    this._primitiveStaged = null;
    this._claims.Clear();
    this._prepared = null;
  }

  public override Action? StageWrite(Entity entity, in T value) {
    if (!this.PrepareWrite(entity, value)) return null;

    var bucket = this._prepared!;
    var hash = this._preparedHash;

    if (this._primitiveStaged is not null) this._primitiveStaged[bucket.Key] = bucket;
    else {
      if (!this._staged!.TryGetValue(hash, out var chain)) this._staged.Add(hash, chain = []);
      if (!chain.Contains(bucket)) chain.Add(bucket);
    }

    if (this.Unique) this._claims[bucket] = entity;

    return () => this.Commit(entity, bucket, hash);
  }

  public override void CommitWrite(Entity entity) => this.Commit(entity, this._prepared!, this._preparedHash);

  private void Commit(Entity entity, Bucket bucket, int hash) {
    if (this.TryEntry(entity, out var current)) {
      if (ReferenceEquals(current.Bucket, bucket)) return;

      this.Remove(entity, current);
    }

    if (this._primitiveBuckets is not null) this._primitiveBuckets[bucket.Key] = bucket;
    else {
      if (!this._buckets.TryGetValue(hash, out var chain)) {
        chain = this._spareChain ?? [];
        this._spareChain = null;
        this._buckets.Add(hash, chain);
      }

      if (!chain.Contains(bucket)) chain.Add(bucket);
    }

    if (bucket.Entries is null) {
      bucket.Entries = this._spareEntities ?? [];
      this._spareEntities = null;
    }

    this.EntryAt(entity.Id) = new(bucket, bucket.Entities.Count, entity.Generation);
    bucket.Snapshot = null;
    bucket.Entities.Add(entity);
    bucket.Attached = true;
  }

  public override Entity[] Incoming(Entity target) => this.ReferencePolicy is null ? [] : this.Snapshot((TKey)(object)target);

  public override void Remove(Entity entity) {
    if (this.TryEntry(entity, out var entry)) this.Remove(entity, entry);
  }

  private void Remove(Entity entity, Entry entry) {
    this.EntryAt(entity.Id) = default;
    entry.Bucket!.Snapshot = null;
    var entities = entry.Bucket.Entities;
    var last = entities[^1];
    entities[entry.Position] = last;
    entities.RemoveAt(entities.Count - 1);

    if (last != entity) this.EntryAt(last.Id) = new(entry.Bucket, entry.Position, last.Generation);
    if (entities.Count != 0) return;

    entry.Bucket.Attached = false;
    entry.Bucket.Entries = null;
    this._spareEntities = entities;

    if (this._primitiveBuckets is not null) this._primitiveBuckets.Remove(entry.Bucket.Key);
    else {
      var chain = this._buckets[entry.Bucket.Hash];
      chain.Remove(entry.Bucket);

      if (chain.Count == 0) {
        this._buckets.Remove(entry.Bucket.Hash);
        this._spareChain = chain;
      }
    }

    if (ReferenceEquals(this._prepared, entry.Bucket)) this._prepared = null;

    this.Recycle(entry.Bucket);
  }

  private void Recycle(Bucket bucket) {
    bucket.Key = default!;
    bucket.View = null;
    bucket.Snapshot = null;

    if (bucket.Version != ulong.MaxValue && this._spareBuckets.Count < 64) this._spareBuckets.Push(bucket);
  }

  public EntityCollection Collection(TKey key) {
    this._store.EnsureAccess();
    using var preparation = this._store.Prepare();

    if (!typeof(TKey).IsValueType && key is null) return new(this, s_nullKey);

    var hash = this.Hash(key);

    var bucket = this.Find(key, hash);
    var view = bucket is null ? new KeyView(key, hash, null)
      : bucket.View ??= new KeyView(bucket.Key, bucket.Hash, bucket);

    return new(this, view);
  }

  public bool TryGet(TKey key, out Entity entity) {
    this._store.EnsureAccess();
    using var preparation = this._store.Prepare();
    var bucket = !typeof(TKey).IsValueType && key is null ? null : this.Find(key, this.Hash(key));
    entity = bucket is not null && bucket.Entities.Count == 1 ? bucket.Entities[0] : default;

    return entity != default;
  }

  private Bucket? Resolve(object key) {
    this._store.EnsureAccess();

    if (ReferenceEquals(key, s_nullKey)) return null;

    var view = (KeyView)key;

    if (view.Cached is { Attached: true } cached && cached.Version == view.Version) return cached;

    using var preparation = this._store.Prepare();
    var bucket = this.Find(view.Key, view.Hash);
    view.Cached = bucket;
    view.Version = bucket?.Version ?? 0;

    return bucket;
  }

  int IEntityCollectionSource.Count(object key) => this.Resolve(key)?.Entities.Count ?? 0;

  Entity IEntityCollectionSource.At(object key, int index) {
    var bucket = this.Resolve(key);

    if (bucket is null || (uint)index >= (uint)bucket.Entities.Count) return Array.Empty<Entity>()[index];

    return bucket.Entities[index];
  }

  IEnumerator<Entity> IEntityCollectionSource.Enumerate(object key) {
    var bucket = this.Resolve(key);

    if (bucket is null) return Enumerable.Empty<Entity>().GetEnumerator();

    return new BucketEnumerator(bucket.Snapshot ??= bucket.Entities.ToArray());
  }

  public Entity[] Snapshot(TKey key) {
    this._store.EnsureAccess();
    using var preparation = this._store.Prepare();

    if (!typeof(TKey).IsValueType && key is null) return [];

    return this.Find(key, this.Hash(key))?.Entities.ToArray() ?? [];
  }

  public Entity[] Range(TKey minimum, TKey maximum) {
    this._store.EnsureAccess();
    using var preparation = this._store.Prepare();
    var comparer = Comparer<TKey>.Default;
    var matches = new HashSet<Entity>();

    if (this._primitiveBuckets is not null) {
      foreach (var bucket in this._primitiveBuckets.Values) {
        if (comparer.Compare(bucket.Key, minimum) >= 0 && comparer.Compare(bucket.Key, maximum) <= 0)
          matches.UnionWith(bucket.Entities);
      }
    } else {
      foreach (var chain in this._buckets.Values) {
        foreach (var bucket in chain) {
          if (comparer.Compare(bucket.Key, minimum) >= 0 && comparer.Compare(bucket.Key, maximum) <= 0)
            matches.UnionWith(bucket.Entities);
        }
      }
    }

    return [..matches];
  }

  public TKey[] Values() {
    this._store.EnsureAccess();

    return this._primitiveBuckets is not null ? this._primitiveBuckets.Keys.ToArray()
      : this._buckets.Values.SelectMany(chain => chain).Select(bucket => bucket.Key).ToArray();
  }

  public override void Clear() {
    this._buckets.Clear();
    this._primitiveBuckets?.Clear();
    this._entries = [];
    this._prepared = null;
    this._spareEntities = null;
    this._spareChain = null;
    this._spareBuckets.Clear();
  }

  private int Hash(TKey key) => this._primitiveBuckets is not null ? 0 : this._comparer.GetHashCode(key);

  private Bucket? Find(TKey key, int hash) {
    if (this._primitiveBuckets is not null)
      return this._primitiveBuckets.TryGetValue(key, out var bucket) ? bucket : null;
    if (!this._buckets.TryGetValue(hash, out var chain)) return null;

    return this.Find(chain, key);
  }

  private Bucket? Find(List<Bucket> chain, TKey key) {
    foreach (var bucket in chain) {
      if (this._comparer.Equals(bucket.Key, key)) return bucket;
    }

    return null;
  }

  private sealed class KeyView {
    public TKey Key { get; }
    public int Hash { get; }
    public Bucket? Cached;
    public ulong Version;

    public KeyView(TKey key, int hash, Bucket? bucket) {
      this.Key = key;
      this.Hash = hash;
      this.Cached = bucket;
      this.Version = bucket?.Version ?? 0;
    }
  }

  private sealed class Bucket {
    public bool Attached;
    public int Hash;
    public TKey Key = default!;
    public ulong Version;
    public KeyView? View;
    private static readonly List<Entity> s_empty = [];
    public List<Entity>? Entries;
    public Entity[]? Snapshot;
    public List<Entity> Entities => this.Entries ?? s_empty;

    public void Initialize(TKey key, int hash) {
      this.Key = key;
      this.Hash = hash;
      this.Version++;
    }
  }

  private sealed class BucketEnumerator : IEnumerator<Entity> {
    private Entity[]? _snapshot;
    private readonly int _count;
    private int _position = -1;

    public BucketEnumerator(Entity[] entities) {
      this._count = entities.Length;
      this._snapshot = entities;
    }

    public Entity Current => this._snapshot is {} snapshot && (uint)this._position < (uint)this._count
      ? snapshot[this._position] : throw new InvalidOperationException();
    object IEnumerator.Current => this.Current;
    public bool MoveNext() => this._snapshot is not null && ++this._position < this._count;
    public void Reset() {
      ObjectDisposedException.ThrowIf(this._snapshot is null, this);
      this._position = -1;
    }
    public void Dispose() {
      this._snapshot = null;
    }
  }

  private void PrepareEntry(int id) {
    var page = id >> EntryPageBits;

    if (page >= this._entries.Length)
      Array.Resize(ref this._entries, Math.Max(page + 1, this._entries.Length * 2 + 4));

    this._entries[page] ??= new Entry[EntryPageSize];
  }

  private ref Entry EntryAt(int id) => ref this._entries[id >> EntryPageBits]![id & (EntryPageSize - 1)];

  private bool TryEntry(Entity entity, out Entry entry) {
    entry = default;
    var page = entity.Id >> EntryPageBits;

    if (!ReferenceEquals(entity.Store, this._store) || entity.Id <= 0 || (uint)page >= (uint)this._entries.Length
      || this._entries[page] is null) return false;

    entry = this.EntryAt(entity.Id);

    return entry.Bucket is not null && entry.Generation == entity.Generation;
  }

  private readonly record struct Entry(Bucket? Bucket, int Position, uint Generation);
}
