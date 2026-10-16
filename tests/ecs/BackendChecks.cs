using System.Reflection;
using System.Runtime.CompilerServices;
using Arch.Core;
using Archy;
using Store = Archy.EntityStore;

namespace Archy.Tests;

internal static class BackendChecks {
  public static void Run() {
    SignatureCollisions();
    ChunkPopulationChanges();
    NativeIdentityChecks.Run();
    using var store = new Store();
    var (payload, retained) = RemovePayload(store);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check.That(!payload.IsAlive, "Structural moves clear managed payloads in vacated Arch rows.");
    Check.That(store.Count == 1, "Payload cleanup preserves the remaining entity.");
    GC.KeepAlive(retained);
  }

  private static void ChunkPopulationChanges() {
    using var store = new Store();
    var entities = new List<Archy.Entity>();
    var present = new HashSet<Archy.Entity>();
    var query = store.Query<QueryPosition>().FreezeFilter();

    void Verify() {
      Check.That(query.Count == present.Count, "Cached query counts follow structural population changes.");
      Check.That(query.Entities.ToHashSet().SetEquals(present), "Traversal membership follows structural population changes.");
      Check.That(query.Chunks.Sum(chunk => chunk.Item1.Count) == present.Count,
        "Public chunk lengths follow structural population changes.");
      query.ForEachEntity((value, entity) => {
        Check.That(present.Contains(entity) && entity.Get<QueryPosition>() == value,
          "Swap removal keeps typed values aligned with public handles.");
      });
    }

    for (var cycle = 0; cycle < 3; cycle++) {
      for (var i = 0; i < 5000; i++) {
        var entity = store.Create();
        entity.Add(new QueryPosition(i));
        entities.Add(entity);
        present.Add(entity);
      }

      Verify();

      for (var i = 0; i < entities.Count; i += 3) {
        var entity = entities[i];

        if (entity.IsAlive && entity.TryRemove<QueryPosition>()) present.Remove(entity);
      }

      Verify();

      for (var i = entities.Count - 1; i >= 0; i -= 2) {
        var entity = entities[i];

        if (entity.TryDelete()) present.Remove(entity);
      }

      Verify();

      foreach (var entity in entities) {
        if (entity.IsAlive && entity.TryAdd(new QueryPosition(cycle))) present.Add(entity);
      }

      Verify();
    }
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static (WeakReference Payload, object Retained) RemovePayload(Store store) {
    var entity = store.Create();
    var token = new JournalToken();
    entity.Add(new JournalPayload(token));
    var escaped = store.Query<JournalPayload>().Chunks.First().Item1;
    entity.Remove<JournalPayload>();

    return (new(token), escaped);
  }

  private static void SignatureCollisions() {
    var identity = typeof(Store).Assembly.GetType("Archy.NativeIdentity")!;
    ComponentType[] shared = [Component.GetComponentType(identity), Component<QueryPosition>.ComponentType,
      Component<JournalPayload>.ComponentType, Component<IndexedGroup>.ComponentType];
    Type[] arguments = [typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double),
      typeof(decimal), typeof(bool), typeof(char), typeof(string), typeof(Guid), typeof(DateTime), typeof(uint),
      typeof(ulong), typeof(ushort), typeof(sbyte)];
    var markers = arguments.Concat(arguments.Select(type => type.MakeArrayType()))
      .Select(type => typeof(Marker<>).MakeGenericType(type)).ToArray();
    // Spread marker IDs across words so the collision search varies more than one bitset word.
    var types = markers.Select(type => {
      Component.GetComponentType(typeof(SignatureGap<>).MakeGenericType(type));

      return Component.GetComponentType(type);
    }).ToArray();
    var hashes = new Dictionary<int, uint>();
    var random = new Random(6173);
    var signature = new ComponentType[shared.Length + 8];
    shared.CopyTo(signature, 0);
    uint first = 0;
    uint second = 0;

    for (var attempt = 0; attempt < 1000000; attempt++) {
      uint mask = 0;

      while (System.Numerics.BitOperations.PopCount(mask) < 8) mask |= 1u << random.Next(32);

      var index = shared.Length;

      for (var i = 0; i < types.Length; i++) {
        if ((mask & (1u << i)) != 0) signature[index++] = types[i];
      }

      var hash = Component.GetHashCode(signature);

      if (hashes.TryGetValue(hash, out first) && first != mask) {
        second = mask;
        break;
      }

      hashes[hash] = mask;
    }

    Check.That(second != 0, "Collision fixture finds distinct native signatures with the same hash.");
    using var store = new Store();
    var left = store.Create();
    var right = store.Create();
    var token = new JournalToken();
    left.Add(new QueryPosition(11));
    right.Add(new QueryPosition(22));
    left.Add(new JournalPayload(token));
    right.Add(new JournalPayload(token));
    left.Add(new IndexedGroup { Value = 1 });
    right.Add(new IndexedGroup { Value = 2 });
    var add = typeof(BackendChecks).GetMethod(nameof(AddMarker), BindingFlags.NonPublic | BindingFlags.Static)!;
    var has = typeof(BackendChecks).GetMethod(nameof(HasMarker), BindingFlags.NonPublic | BindingFlags.Static)!;
    var count = typeof(BackendChecks).GetMethod(nameof(CountMarker), BindingFlags.NonPublic | BindingFlags.Static)!;

    for (var i = 0; i < markers.Length; i++) {
      var apply = add.MakeGenericMethod(markers[i]).CreateDelegate<Action<Archy.Entity>>();

      if ((first & (1u << i)) != 0) apply(left);
      if ((second & (1u << i)) != 0) apply(right);
    }

    for (var i = 0; i < markers.Length; i++) {
      var present = has.MakeGenericMethod(markers[i]).CreateDelegate<Func<Archy.Entity, bool>>();
      Check.That(present(left) == ((first & (1u << i)) != 0), "First colliding signature preserves component membership.");
      Check.That(present(right) == ((second & (1u << i)) != 0), "Second colliding signature preserves component membership.");
      var query = count.MakeGenericMethod(markers[i]).CreateDelegate<Func<Store, int>>();
      var expected = ((first & (1u << i)) != 0 ? 1 : 0) + ((second & (1u << i)) != 0 ? 1 : 0);
      Check.That(query(store) == expected, "Native query matching preserves colliding compositions.");
    }

    var sibling = store.Create();
    sibling.Add(new QueryPosition(33));
    sibling.Add(new JournalPayload(token));
    sibling.Add(new IndexedGroup { Value = 3 });

    for (var i = 0; i < markers.Length; i++) {
      if ((second & (1u << i)) != 0)
        add.MakeGenericMethod(markers[i]).CreateDelegate<Action<Archy.Entity>>()(sibling);
    }

    var events = new List<EntityModified>();
    using var subscription = store.Events.Subscribe<EntityModified>(events.Add);

    for (var i = 0; i < 3; i++) {
      right.AddTag<QueryVisible>();
      right.RemoveTag<QueryVisible>();
      var position = right.Get<QueryPosition>();
      var payload = right.Get<JournalPayload>();
      var siblingPosition = sibling.Get<QueryPosition>();
      Check.That(position.Value == 22 && ReferenceEquals(payload.Value, token),
        "Cross-world structural transitions preserve owned component columns and public identity.");
      Check.That(store.ComponentIndex<IndexedGroup, int>()[2].Single() == right,
        "Indexed membership survives transitions into and out of a colliding archetype.");
      Check.That(siblingPosition.Value == 33 && store.Query<QueryPosition>().Entities.Contains(sibling),
        "Cross-world swap removal repairs the remaining public handle's location.");
    }

    store.Events.Dispatch();
    Check.That(events.Count == 6 && events.All(item => item.Entity == right && item.Type == typeof(QueryVisible)),
      "Colliding native worlds retain store-scoped lifecycle event entity attribution.");
    right.Delete();
    sibling.Update(new QueryPosition(44));
    var leftPosition = left.Get<QueryPosition>();
    var remainingPosition = sibling.Get<QueryPosition>();
    Check.That(!right.IsAlive && leftPosition.Value == 11 && remainingPosition.Value == 44
      && store.Query<QueryPosition>().Count == 2,
      "Deleting from a colliding archetype preserves the other native world's population.");
  }

  private static void AddMarker<T>(Archy.Entity entity) where T : struct, IComponent => entity.Add(default(T));
  private static bool HasMarker<T>(Archy.Entity entity) where T : struct, IComponent => entity.Has<T>();
  private static int CountMarker<T>(Store store) where T : struct, IComponent => store.Query<T>().Count;
  private readonly record struct Marker<T> : IComponent;
  private readonly struct SignatureGap<T>;
}
