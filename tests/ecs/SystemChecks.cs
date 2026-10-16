using Archy;

namespace Archy.Tests;

internal static class SystemChecks {
  public static void Run() {
    using var store = new EntityStore();
    var entity = store.Create();
    entity.Add(new QueryPosition(1));
    var pipeline = new SystemPipeline(store);
    var order = new List<string>();
    CommandBuffer? handlerCommands = null;
    using var handler = entity.Events.Subscribe<CustomEvent<Damage>>(item => handlerCommands!.Upsert(entity, new QueryVelocity(item.Data.Amount)));
    var first = new TestSystem(context => {
      order.Add($"a{context.Tick.Number}");
      Check.That(context.Tick.Delta == 0.5 && context.Store == store, "System context supplies the injected store and tick.");

      if (context.Tick.Number == 1) Check.That(!entity.Has<QueryVelocity>(), "First system starts before handler commands.");
      else Check.That(entity.Get<QueryVelocity>().Value == 20, "Final handler commands commit at next tick opening.");

      handlerCommands = context.Commands;
      context.Commands.Upsert(entity, new QueryPosition(2));
      context.Commands.Emit(entity, new Damage(10, default));
    });
    var second = new TestSystem(context => {
      order.Add($"b{context.Tick.Number}");
      Check.That(entity.Get<QueryPosition>().Value == 2, "Later systems see preceding system commands.");

      if (context.Tick.Number == 1) Check.That(!entity.Has<QueryVelocity>(), "Handler commands wait for the next checkpoint.");

      context.Checkpoint();
      Check.That(entity.Get<QueryVelocity>().Value == 10, "Explicit post-traversal checkpoints commit pending handler commands.");
      using (var iterator = store.Query<QueryPosition>().Entities.GetEnumerator()) {
        Check.That(iterator.MoveNext(), "System traversal starts.");
        Check.Throws<InvalidOperationException>(() => context.Checkpoint());
      }

      context.Commands.Emit(entity, new Damage(20, default));
    });
    pipeline.Add(first).Add(second);
    pipeline.Update(new(1, 0.5));
    Check.That(entity.Get<QueryVelocity>().Value == 10, "Last system handler commands remain buffered after the tick.");
    pipeline.Update(new(2, 0.5));
    Check.That(order.SequenceEqual(["a1", "b1", "a2", "b2"]), "Systems execute in registration order each tick.");
    pipeline.Dispose();
    Check.That(first.Disposed && second.Disposed && entity.IsAlive, "Pipeline disposes owned systems and buffer while the caller retains its store.");
    Check.Throws<ObjectDisposedException>(() => handlerCommands!.Upsert(entity, new QueryPosition(1)));
  }

  private sealed class TestSystem : ISystem, IDisposable {
    private readonly Action<SystemContext> _update;
    public bool Disposed { get; private set; }
    public TestSystem(Action<SystemContext> update) => this._update = update;
    public void Update(SystemContext context) => this._update(context);
    public void Dispose() => this.Disposed = true;
  }
}
