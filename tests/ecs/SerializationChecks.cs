using System.Text.Json;
using Archy;

namespace Archy.Tests;

internal static class SerializationChecks {
  public static void Run() {
    var serializer = new StoreSerializer(typeof(SavedIdentity).Assembly);
    using var store = StoreFactory.Create();
    var entity = store.Create();
    entity.Add(new SavedIdentity(1));
    var json = serializer.WriteComponent(new SavedIdentity(2));
    var decoded = serializer.ReadComponent<SavedIdentity>(json);
    Check.That(decoded.Value == 2 && entity.Get<SavedIdentity>().Value == 1, "Standalone decode does not mutate live state.");
    Check.That(json.Contains("saved.identity", StringComparison.Ordinal) && json.Contains("version", StringComparison.Ordinal), "Component envelopes contain declared stable keys and versions.");
    entity.Upsert(decoded);
    Check.That(store.ComponentIndex<SavedIdentity, int>()[2].Single() == entity, "Applying decoded values maintains generated indexes.");
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedIdentity>(json.Replace("saved.identity", "missing", StringComparison.Ordinal)));
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedIdentity>(json.Replace("\"version\":1", "\"version\":8", StringComparison.Ordinal)));
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedIdentity>("{}"));
    Check.Throws<JsonException>(() => serializer.WriteComponent(new QueryPosition(1)));
    Check.Throws<JsonException>(() => serializer.WriteComponent(new TransientData(1)));
    const string old = "{\"key\":\"saved.versioned\",\"version\":1,\"data\":{\"old\":3}}";
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedVersioned>(old));
    serializer.RegisterMigration<SavedVersioned>(1, value => JsonSerializer.SerializeToElement(new { value = value.GetProperty("old").GetInt32() + 1 }));
    Check.That(serializer.ReadComponent<SavedVersioned>(old).Value == 4, "Explicit migrations decode earlier schema versions.");
    Check.That(serializer.ReadComponent<SavedCustom>(serializer.WriteComponent(new SavedCustom(8))).Value == 8, "Declared custom codecs participate in component envelopes.");
    var values = new[] { 4 };
    var savedBag = serializer.ReadComponent<SavedBag>(serializer.WriteComponent(new SavedBag(values)));
    values[0] = 99;
    Check.That(savedBag.Values[0] == 4, "Serialization round trips own mutable payloads through copy policies.");
    var target = store.Create();
    Check.Throws<JsonException>(() => serializer.WriteComponent(new SavedOwner(target)));
    var references = new ReferenceContext(new Dictionary<string, Entity> { ["target"] = target });
    var linked = serializer.WriteComponent(new SavedOwner(target), references);
    Check.That(!linked.Contains("generation", StringComparison.OrdinalIgnoreCase) && serializer.ReadComponent<SavedOwner>(linked, references).Target == target, "Standalone references use explicit document mappings.");
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedOwner>(linked));
    Check.Throws<JsonException>(() => serializer.ReadComponent<SavedOwner>(linked, new(new Dictionary<string, Entity>())));
  }
}

[ComponentKey("saved.identity")]
internal readonly partial record struct SavedIdentity([property: Indexed(Unique = true)] int Value) : IComponent;
[ComponentKey("saved.versioned", Version = 2)]
internal readonly record struct SavedVersioned(int Value) : IComponent;
[ComponentKey("saved.bag"), ComponentCopy(typeof(SavedBagCopy))]
internal readonly record struct SavedBag(int[] Values) : IComponent;
internal sealed class SavedBagCopy : IComponentCopy<SavedBag> {
  public SavedBag Copy(in SavedBag value) => new([..value.Values]);
}
[ComponentKey("saved.owner")]
internal readonly partial record struct SavedOwner([property: Indexed, EntityReference(OnDelete = TargetDeletion.RemoveComponent)] Entity Target) : IComponent;
[TagName("saved.visible")]
internal readonly struct SavedVisible : ITag;
[Transient]
internal readonly record struct TransientData(int Value) : IComponent;

[ComponentKey("saved.custom", Codec = typeof(SavedCustomCodec))]
internal readonly record struct SavedCustom(int Value) : IComponent;
internal sealed class SavedCustomCodec : IComponentCodec<SavedCustom> {
  public JsonElement Write(in SavedCustom value, ComponentCodecContext context) => JsonSerializer.SerializeToElement(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
  public SavedCustom Read(JsonElement data, ComponentCodecContext context) => new(int.Parse(data.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
}
