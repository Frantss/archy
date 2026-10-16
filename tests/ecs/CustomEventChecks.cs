using Archy;

namespace Archy.Tests;

internal static class CustomEventChecks {
  public static void Run() {
    using var channels = new EntityStore();
    using var isolated = new EntityStore();
    GrowChannels<int>(channels, isolated, 40);
    using var store = new EntityStore();
    using var foreign = new EntityStore();
    var entity = store.Create();
    var other = foreign.Create();
    entity.Add(new QueryPosition(1));
    Check.That(entity.TryEmit(new Damage(1, other)), "Unobserved custom events are accepted and participant handles are historical data.");
    var log = new List<string>();
    var sequences = new List<long>();
    var modifications = 0;
    using var modified = entity.Events.Subscribe<EntityModified>(_ => modifications++);
    using var global = store.Events.Subscribe<CustomEvent<Damage>>(item => {
      log.Add($"global:{item.Data.Amount}"); sequences.Add(item.Sequence);
      Check.That(item.Entity == entity && item.Data.Source == other, "Store custom subscriptions identify the full originating entity.");
    });
    using var local = entity.Events.Subscribe<CustomEvent<Damage>>(item => {
      log.Add($"local:{item.Data.Amount}");

      if (item.Data.Amount == 1) entity.Emit(new Damage(2, other));
    });
    using var storeOnly = store.Events.Subscribe<StoreEvent<Damage>>(item => {
      log.Add("store-only"); sequences.Add(item.Sequence);
      Check.That(item.Store == store, "Store-only envelopes identify their store.");
    });
    foreach (var current in store.Query<QueryPosition>().Entities) current.Emit(new Damage(1, other));

    store.Emit(new Damage(9, other));
    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["global:1", "local:1", "store-only"]) && modifications == 0, "Custom channels stay distinct, route store first, and do not modify entities.");
    store.Events.Dispatch();
    Check.That(log.TakeLast(2).SequenceEqual(["global:2", "local:2"]) && sequences.SequenceEqual(sequences.Order()), "Handler emissions wait for the next finite batch.");
    Check.That(!store.TryEmit(other, new Damage(1, entity)) && !default(Entity).TryEmit(new Damage(1, other)), "TryEmit validates live same-store origins.");
    Check.Throws<MutationException>(() => store.Emit(other, new Damage(1, entity)));
    Check.Throws<InvalidOperationException>(() => entity.Events.Subscribe<StoreEvent<Damage>>(_ => {}));
    using var commands = store.CreateCommandBuffer();
    var reserved = commands.Create();
    Check.That(!reserved.TryEmit(new Damage(1, other)), "Reserved origins cannot emit before activation.");
    var canceled = 0;
    var subscription = entity.Events.Subscribe<CustomEvent<Damage>>(_ => canceled++);
    entity.Emit(new Damage(3, other));
    subscription.Dispose();
    var late = 0;
    using var lateSubscription = entity.Events.Subscribe<CustomEvent<Damage>>(_ => late++);
    store.Events.Dispatch();
    Check.That(canceled == 0 && late == 0, "Custom events capture subscriptions and respect pending unsubscribe.");
    CustomEvent<MutableMessage> retained = default;
    using var mutable = entity.Events.Subscribe<CustomEvent<MutableMessage>>(item => retained = item);
    var input = new[] { 4 };
    entity.Emit(new MutableMessage(input));
    input[0] = 90;
    store.Events.Dispatch();
    Check.That(retained.Data.Values[0] == 4, "Custom events own policy-copied payloads.");
    Check.Throws<InvalidOperationException>(() => entity.Emit(new UnsafeMessage([1])));
    entity.Emit(new Damage(4, other));
    entity.Delete();
    store.Events.Dispatch();
    Check.That(late == 1, "Queued custom events retain dead origin identity until deletion cleanup.");
    Check.That(!entity.TryEmit(new Damage(5, other)), "Deleted origins reject new emissions.");
  }

  private static void GrowChannels<T>(EntityStore store, EntityStore isolated, int remaining) {
    var entity = store.Create();
    var other = isolated.Create();
    var log = new List<string>();
    using var local = entity.Events.Subscribe<CustomEvent<ChannelMessage<T>>>(_ => log.Add("local"));
    using var global = store.Events.Subscribe<CustomEvent<ChannelMessage<T>>>(_ => log.Add("global"));
    using var storeOnly = store.Events.Subscribe<StoreEvent<ChannelMessage<T>>>(_ => log.Add("store"));
    var canceled = 0;
    var cancel = entity.Events.Subscribe<CustomEvent<ChannelMessage<T>>>(_ => canceled++);
    entity.Emit(new ChannelMessage<T>(1));
    other.Emit(new ChannelMessage<T>(2));
    isolated.Emit(new ChannelMessage<T>(3));
    cancel.Dispose();
    var late = 0;
    using var later = store.Events.Subscribe<CustomEvent<ChannelMessage<T>>>(_ => late++);
    store.Emit(new ChannelMessage<T>(4));
    isolated.Events.Dispatch();
    store.Events.Dispatch();
    Check.That(log.SequenceEqual(["global", "local", "store"]) && canceled == 0 && late == 0,
      "Typed channel growth preserves captured scope order, cancellation and store isolation.");
    entity.Emit(new ChannelMessage<T>(5));
    entity.Delete();
    var replacement = store.Create();
    replacement.Emit(new ChannelMessage<T>(6));
    store.Events.Dispatch();
    Check.That(log.Count(item => item == "local") == 2 && late == 2,
      "Channel cleanup retains queued origin delivery and cannot attach to a reused generation.");
    using var isolatedSubscription = other.Events.Subscribe<CustomEvent<ChannelMessage<T>>>(_ => canceled++);
    other.Emit(new ChannelMessage<T>(7));
    isolated.Events.Dispatch();
    Check.That(canceled == 1, "A second store lazily initializes an already assigned typed channel slot.");

    if (remaining > 0) GrowChannels<ChannelMessage<T>>(store, isolated, remaining - 1);
  }
}

internal readonly record struct Damage(int Amount, Entity Source) : ICustomEvent;
[EventCopy(typeof(MutableMessageCopy))]
internal readonly record struct MutableMessage(int[] Values) : ICustomEvent;
internal readonly record struct UnsafeMessage(int[] Values) : ICustomEvent;
internal sealed class MutableMessageCopy : ICustomEventCopy<MutableMessage> {
  public MutableMessage Copy(in MutableMessage value) => new([..value.Values]);
}

internal readonly record struct ChannelMessage<T>(int Value) : ICustomEvent;
