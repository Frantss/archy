using Archy;

namespace Archy.Tests;

internal static class BoundReadChecks {
  public static void Run() {
    using var store = new EntityStore();
    using var foreignStore = new EntityStore();
    var foreign = foreignStore.Create();
    foreign.Add(new QueryPosition(9000));
    var stale = store.Create();
    stale.Add(new QueryPosition(-1));
    stale.Delete();
    var entities = Enumerable.Range(0, 5000).Select(i => {
      var entity = store.Create();
      entity.Add(new QueryPosition(i));
      entity.Add(new QueryVelocity(i + 1));

      return entity;
    }).ToArray();
    entities[0].AddTag<QueryFrozen>();
    entities[0].Add(new EventBag([1]));
    var query = store.Query<QueryPosition, QueryVelocity>();
    query.ForEachEntity((position, velocity, entity) => {
      Check.That(entity.Get<QueryPosition>() == position && entity.Get<QueryVelocity>() == velocity,
        "Traversal reads bind alternating typed columns across chunks.");
      entity.Update(new QueryPosition(position.Value + 10));
      Check.That(entity.Get<QueryPosition>().Value == position.Value + 10,
        "Bound reads observe the current value after method-driven updates.");
      Check.That(foreign.Get<QueryPosition>().Value == 9000 && !stale.TryGet<QueryPosition>(out _),
        "Bound reads preserve foreign-store and historical-handle behavior.");
      if (entity != entities[0]) return;

      store.Query<QueryPosition>().AllTags(Tags.Get<QueryFrozen>()).ForEachEntity((nested, current) => {
        Check.That(current.Get<QueryPosition>() == nested, "Nested read scopes bind their own rows.");
        var copy = current.Get<EventBag>();
        copy.Values[0] = 99;
        Check.That(current.Get<EventBag>().Values[0] == 1, "Read bindings retain owned copy policies.");
      });
      Check.Throws<ArgumentException>(() => store.Query<QueryPosition>().AllTags(Tags.Get<QueryFrozen>())
        .ForEachEntity((_, current) => { _ = current.Get<QueryPosition>(); throw new ArgumentException(); }));
      Check.That(entity.Get<QueryPosition>().Value == 10, "Exceptions restore an outer read scope.");
    });
    var total = 0;
    foreach (var entity in query.Entities) {
      total += entity.Get<QueryPosition>().Value - entity.Get<QueryVelocity>().Value;
      Check.That(entity.Get<QueryVelocity>().Value > 0, "Entity enumeration reads current component values.");
    }
    Check.That(total == 45000, "Ordinary entity reads cover every current row.");
    foreach (var (positions, velocities, handles) in query.Chunks) {
      for (var i = 0; i < handles.Count; i++) {
        var entity = handles[i];
        Check.That(entity.Get<QueryPosition>() == positions[i] && entity.Get<QueryVelocity>() == velocities[i],
          "Checked entity chunk access binds reads to the matching component view rows.");
      }
    }
    using var outer = query.Entities.GetEnumerator();
    using var inner = query.Entities.GetEnumerator();
    Check.That(outer.MoveNext() && inner.MoveNext(), "Interleaved entity traversals start.");
    _ = outer.Current.Get<QueryPosition>();
    Check.That(inner.MoveNext(), "The second traversal advances independently.");
    _ = inner.Current.Get<QueryVelocity>();
    outer.Dispose();
    Check.That(inner.Current.Get<QueryPosition>().Value >= 10, "Disposing another lease preserves current reads.");
    inner.Dispose();
    var currentEntity = entities[1];
    currentEntity.Delete();
    var replacement = store.Create();
    replacement.Add(new QueryPosition(123));
    Check.That(!currentEntity.TryGet<QueryPosition>(out _) && replacement.Get<QueryPosition>().Value == 123,
      "Released read bindings do not bypass generation checks after slot reuse.");
    Check.Throws<InvalidOperationException>(() => Task.Run(() => replacement.Get<QueryPosition>()).GetAwaiter().GetResult());
  }
}
