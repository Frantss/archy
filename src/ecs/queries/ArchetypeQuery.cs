namespace Archy;

public sealed partial class EntityStore {
  public ArchetypeQuery Query() => new(this);
  public ArchetypeQuery<T1> Query<T1>() where T1 : struct, IComponent => new(this);
  public ArchetypeQuery<T1, T2> Query<T1, T2>() where T1 : struct, IComponent where T2 : struct, IComponent => new(this);
  public ArchetypeQuery<T1, T2, T3> Query<T1, T2, T3>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent => new(this);
  public ArchetypeQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>() where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent => new(this);
}

public sealed class ArchetypeQuery : QueryBase<ArchetypeQuery> {
  internal ArchetypeQuery(EntityStore store) : base(store, []) {{}}
  public IEnumerable<EntityChunk> Chunks => this.Batches(static batch => new EntityChunk(batch));
  public void ForEachEntity(Action<Entity> action) {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      using var writes = traversal.BindWrites(chunk);
      if (chunk.Rows is {} selected) {
        foreach (var row in selected) action(traversal.CallbackEntityAt(chunk, row));

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        if (traversal.Includes(chunk, row)) action(traversal.CallbackEntityAt(chunk, row));
      }
    }
  }
}

public sealed class ArchetypeQuery<T1> : QueryBase<ArchetypeQuery<T1>> where T1 : struct, IComponent {
  internal ArchetypeQuery(EntityStore store) : base(store, [TypeCatalog.Component<T1>()]) {}
  public IEnumerable<(ComponentChunk<T1>, EntityChunk)> Chunks => this.Batches(
    static batch => (new ComponentChunk<T1>(batch), new EntityChunk(batch))
  );
  public void ForEachEntity(Action<T1, Entity> action) {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      using var writes = traversal.BindWrites(chunk);
      var values1 = traversal.Bind<T1>(chunk);

      if (chunk.Rows is {} selected) {
        foreach (var row in selected) action(values1.Read(row), traversal.CallbackEntityAt(chunk, row));

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        if (traversal.Includes(chunk, row))
          action(values1.Read(row), traversal.CallbackEntityAt(chunk, row));
      }
    }
  }
}

public sealed class ArchetypeQuery<T1, T2> : QueryBase<ArchetypeQuery<T1, T2>> where T1 : struct, IComponent where T2 : struct, IComponent {
  internal ArchetypeQuery(EntityStore store) : base(store, [TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>()]) {}
  public IEnumerable<(ComponentChunk<T1>, ComponentChunk<T2>, EntityChunk)> Chunks => this.Batches(
    static batch => (new ComponentChunk<T1>(batch), new ComponentChunk<T2>(batch), new EntityChunk(batch))
  );
  public void ForEachEntity(Action<T1, T2, Entity> action) {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      using var writes = traversal.BindWrites(chunk);
      var values1 = traversal.Bind<T1>(chunk);
      var values2 = traversal.Bind<T2>(chunk);

      if (chunk.Rows is {} selected) {
        foreach (var row in selected) action(values1.Read(row), values2.Read(row), traversal.CallbackEntityAt(chunk, row));

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        if (traversal.Includes(chunk, row))
          action(values1.Read(row), values2.Read(row), traversal.CallbackEntityAt(chunk, row));
      }
    }
  }
}

public sealed class ArchetypeQuery<T1, T2, T3> : QueryBase<ArchetypeQuery<T1, T2, T3>> where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent {
  internal ArchetypeQuery(EntityStore store) : base(store, [TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>(), TypeCatalog.Component<T3>()]) {}
  public IEnumerable<(ComponentChunk<T1>, ComponentChunk<T2>, ComponentChunk<T3>, EntityChunk)> Chunks => this.Batches(
    static batch => (
      new ComponentChunk<T1>(batch),
      new ComponentChunk<T2>(batch),
      new ComponentChunk<T3>(batch),
      new EntityChunk(batch)
    )
  );
  public void ForEachEntity(Action<T1, T2, T3, Entity> action) {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      using var writes = traversal.BindWrites(chunk);
      var values1 = traversal.Bind<T1>(chunk);
      var values2 = traversal.Bind<T2>(chunk);
      var values3 = traversal.Bind<T3>(chunk);

      if (chunk.Rows is {} selected) {
        foreach (var row in selected) action(values1.Read(row), values2.Read(row), values3.Read(row), traversal.CallbackEntityAt(chunk, row));

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        if (traversal.Includes(chunk, row))
          action(values1.Read(row), values2.Read(row), values3.Read(row), traversal.CallbackEntityAt(chunk, row));
      }
    }
  }
}

public sealed class ArchetypeQuery<T1, T2, T3, T4> : QueryBase<ArchetypeQuery<T1, T2, T3, T4>> where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent {
  internal ArchetypeQuery(EntityStore store) : base(store, [TypeCatalog.Component<T1>(), TypeCatalog.Component<T2>(), TypeCatalog.Component<T3>(), TypeCatalog.Component<T4>()]) {}
  public IEnumerable<(ComponentChunk<T1>, ComponentChunk<T2>, ComponentChunk<T3>, ComponentChunk<T4>, EntityChunk)> Chunks => this.Batches(
    static batch => (
      new ComponentChunk<T1>(batch),
      new ComponentChunk<T2>(batch),
      new ComponentChunk<T3>(batch),
      new ComponentChunk<T4>(batch),
      new EntityChunk(batch)
    )
  );
  public void ForEachEntity(Action<T1, T2, T3, T4, Entity> action) {
    using var traversal = this.Traverse();

    foreach (var chunk in traversal) {
      using var writes = traversal.BindWrites(chunk);
      var values1 = traversal.Bind<T1>(chunk);
      var values2 = traversal.Bind<T2>(chunk);
      var values3 = traversal.Bind<T3>(chunk);
      var values4 = traversal.Bind<T4>(chunk);

      if (chunk.Rows is {} selected) {
        foreach (var row in selected) action(values1.Read(row), values2.Read(row), values3.Read(row), values4.Read(row), traversal.CallbackEntityAt(chunk, row));

        continue;
      }

      for (var row = 0; row < chunk.Storage.Count; row++) {
        if (traversal.Includes(chunk, row))
          action(values1.Read(row), values2.Read(row), values3.Read(row), values4.Read(row), traversal.CallbackEntityAt(chunk, row));
      }
    }
  }
}
