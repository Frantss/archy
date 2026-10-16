using System.Text.Json;

namespace Archy;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class ComponentKeyAttribute : Attribute {
  public string Key { get; }
  public int Version { get; set; } = 1;
  public Type? Codec { get; set; }
  public ComponentKeyAttribute(string key) => this.Key = key;
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TagNameAttribute : Attribute {
  public string Key { get; }
  public TagNameAttribute(string key) => this.Key = key;
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class TransientAttribute : Attribute;

public interface IComponentCodec<T> where T : struct, IComponent {
  JsonElement Write(in T value, ComponentCodecContext context);
  T Read(JsonElement data, ComponentCodecContext context);
}

public sealed class JsonComponentCodec<T> : IComponentCodec<T> where T : struct, IComponent {
  public JsonElement Write(in T value, ComponentCodecContext context) => JsonSerializer.SerializeToElement(value, context.Options);
  public T Read(JsonElement data, ComponentCodecContext context) => data.Deserialize<T>(context.Options);
}
