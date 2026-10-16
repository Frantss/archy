using Archy.Adapters.Naive;
using Archy.Storage;

namespace Archy.Tests;

internal static class NaiveStorageChecks {
  public static void Run() {
    ChunkLifetimes();
    Moves();
    Disposal();
  }

  private static void ChunkLifetimes() {
    using var storage = new NaiveStorage();
    storage.Register<QueryPosition>();
    storage.Register<QueryPosition>();
    var archetype = storage.CreateArchetype([typeof(QueryPosition)]);
    storage.PrepareCapacity(archetype, 150);
    Check.That(archetype.EntityCount == 0 && archetype.ChunkCount == 0,
      "Capacity preparation publishes no rows or active chunks.");
    var first = storage.Create(archetype, new() { Id = 1, Generation = uint.MaxValue });
    var firstChunk = archetype.GetChunk(first.ChunkIndex);
    var values = (QueryPosition[])firstChunk.GetComponents(typeof(QueryPosition));
    values[first.Row] = new(1);
    var population = firstChunk.Identities.Length * 2 + 1;
    var last = first;

    for (var i = 2; i <= population; i++) {
      last = storage.Create(archetype, new() { Id = i, Generation = uint.MaxValue });
      ((QueryPosition[])archetype.GetChunk(last.ChunkIndex).GetComponents(typeof(QueryPosition)))[last.Row] = new(i);
    }

    storage.PrepareCapacity(archetype, population);
    Check.That(archetype.EntityCount == population && firstChunk.Identities[first.Row].Generation == uint.MaxValue,
      "Growth and preparation preserve counts and full unsigned generations.");
    Check.That(ReferenceEquals(firstChunk, archetype.GetChunk(first.ChunkIndex))
      && ReferenceEquals(values, firstChunk.GetComponents(typeof(QueryPosition))),
      "Growth preserves escaped chunk objects and typed arrays.");
    var count = Enumerable.Range(0, archetype.ChunkCount).Sum(index => archetype.GetChunk(index).Count);
    Check.That(count == population, "Active chunk counts account for every live row.");
    var lastChunk = archetype.GetChunk(last.ChunkIndex);
    var lastValues = (QueryPosition[])lastChunk.GetComponents(typeof(QueryPosition));
    storage.Remove(first);
    Check.That(firstChunk.Identities[first.Row].Id == population && values[first.Row].Value == population,
      "Removal moves the final row and its value into the removed location.");
    Check.That(lastChunk.Count == 0 && lastChunk.Identities[last.Row].Id == 0
      && lastChunk.Identities[last.Row].Generation == 0 && lastValues[last.Row] == default,
      "Removal clears the vacated identity and component row across chunk boundaries.");
    var reused = storage.Create(archetype, new() { Id = population + 1, Generation = 2 });
    Check.That(reused.ChunkIndex == last.ChunkIndex && reused.Row == last.Row
      && ReferenceEquals(lastChunk, archetype.GetChunk(reused.ChunkIndex)) && lastValues[reused.Row] == default,
      "Regrowth reuses stable chunks with default component values.");

    while (archetype.EntityCount > 0) {
      var index = archetype.ChunkCount - 1;
      var chunk = archetype.GetChunk(index);
      storage.Remove(new() { Archetype = archetype, ChunkIndex = index, Row = chunk.Count - 1 });
    }

    Check.That(archetype.ChunkCount == 0 && firstChunk.Count == 0,
      "Deleting the final row leaves no active chunks.");
    var regrown = storage.Create(archetype, new() { Id = 1, Generation = 3 });
    Check.That(ReferenceEquals(firstChunk, archetype.GetChunk(regrown.ChunkIndex)) && values[regrown.Row] == default,
      "A fully emptied archetype regrows without replacing escaped arrays or retaining values.");
  }

  private static void Moves() {
    using var storage = new NaiveStorage();
    storage.Register<QueryPosition>();
    storage.Register<JournalPayload>();
    storage.Register<QueryVisible>();
    var source = storage.CreateArchetype([typeof(QueryPosition), typeof(JournalPayload)]);
    var tagged = storage.CreateArchetype([typeof(QueryPosition), typeof(JournalPayload), typeof(QueryVisible)]);
    var plain = storage.CreateArchetype([typeof(QueryPosition), typeof(QueryVisible)]);
    var first = storage.Create(source, new() { Id = 1, Generation = uint.MaxValue });
    var second = storage.Create(source, new() { Id = 2, Generation = 4 });
    var chunk = source.GetChunk(first.ChunkIndex);
    var positions = (QueryPosition[])chunk.GetComponents(typeof(QueryPosition));
    var payloads = (JournalPayload[])chunk.GetComponents(typeof(JournalPayload));
    var token = new JournalToken();
    positions[first.Row] = new(9);
    positions[second.Row] = new(10);
    payloads[first.Row] = new(token);
    payloads[second.Row] = new(new JournalToken());
    var moved = storage.Move(first, (tagged, typeof(QueryVisible), true));
    var movedChunk = tagged.GetChunk(moved.ChunkIndex);
    var movedPositions = (QueryPosition[])movedChunk.GetComponents(typeof(QueryPosition));
    var movedPayloads = (JournalPayload[])movedChunk.GetComponents(typeof(JournalPayload));
    Check.That(movedChunk.Identities[moved.Row].Id == 1
      && movedChunk.Identities[moved.Row].Generation == uint.MaxValue && movedPositions[moved.Row].Value == 9
      && ReferenceEquals(movedPayloads[moved.Row].Value, token),
      "Structural moves preserve identity, unsigned generation and retained component values.");
    Check.That(source.EntityCount == 1 && chunk.Identities[first.Row].Id == 2 && positions[first.Row].Value == 10,
      "Structural moves repair the vacated source row with the final source entity.");
    Check.That(payloads[second.Row].Value is null && chunk.Identities[second.Row].Id == 0,
      "Structural moves clear managed references and identities in the final source row.");
    var removed = storage.Move(moved, (plain, typeof(JournalPayload), false));
    Check.That(plain.EntityCount == 1 && tagged.EntityCount == 0
      && ((QueryPosition[])plain.GetChunk(removed.ChunkIndex).GetComponents(typeof(QueryPosition)))[removed.Row].Value == 9
      && movedPayloads[moved.Row].Value is null,
      "Removing a managed component retains other values and clears its old storage.");
    var restored = storage.Move(removed, (tagged, typeof(JournalPayload), true));
    Check.That(movedPayloads[restored.Row].Value is null
      && ReferenceEquals(movedChunk, tagged.GetChunk(restored.ChunkIndex)),
      "Adding a component into a reused chunk starts with its default value.");
  }

  private static void Disposal() {
    var storage = new NaiveStorage();
    storage.Register<JournalPayload>();
    var archetype = storage.CreateArchetype([typeof(JournalPayload)]);
    var location = storage.Create(archetype, new() { Id = 1, Generation = 1 });
    var chunk = archetype.GetChunk(location.ChunkIndex);
    var values = (JournalPayload[])chunk.GetComponents(typeof(JournalPayload));
    values[location.Row] = new(new JournalToken());
    storage.Dispose();
    storage.Dispose();
    Check.That(values[location.Row].Value is null && chunk.Identities[location.Row].Id == 0
      && chunk.Count == 0 && archetype.EntityCount == 0 && archetype.ChunkCount == 0,
      "Repeated disposal clears escaped payload/identity arrays and leaves no live population.");
    Check.Throws<ObjectDisposedException>(() => storage.Register<QueryPosition>());
    Check.Throws<ObjectDisposedException>(() => storage.CreateArchetype([]));
    Check.Throws<ObjectDisposedException>(() => storage.PrepareCapacity(archetype, 1));
    Check.Throws<ObjectDisposedException>(() => storage.Create(archetype, new() { Id = 2, Generation = 1 }));
    Check.Throws<ObjectDisposedException>(() => storage.Move(location, (archetype, typeof(QueryVisible), true)));
    Check.Throws<ObjectDisposedException>(() => storage.Remove(location));
  }
}
