using System.Reflection;

namespace Archy;

public interface ICustomEvent;
public interface ICustomEventCopy<T> where T : struct, ICustomEvent {
  T Copy(in T value);
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class EventCopyAttribute : Attribute {
  public Type PolicyType { get; }
  public EventCopyAttribute(Type policyType) => this.PolicyType = policyType;
}

public readonly record struct CustomEvent<T>(Entity Entity, long Sequence, T Data) where T : struct, ICustomEvent;
public readonly record struct StoreEvent<T>(EntityStore Store, long Sequence, T Data) where T : struct, ICustomEvent;

internal sealed class CustomPayload<T> where T : struct, ICustomEvent {
  private static readonly Lazy<CustomPayload<T>> s_instance = new(() => new());
  private readonly ICustomEventCopy<T>? _policy;
  public static CustomPayload<T> Instance => s_instance.Value;

  private CustomPayload() {
    var attribute = typeof(T).GetCustomAttribute<EventCopyAttribute>();

    if (attribute is not null) this._policy = Activator.CreateInstance(attribute.PolicyType, nonPublic: true) as ICustomEventCopy<T>
      ?? throw new InvalidOperationException("Event copy policy has an incompatible payload type.");
    else if (!ValueSafety.IsImmutable(typeof(T))) throw new InvalidOperationException("Custom payloads require immutable values or a copy policy.");
  }

  public T Copy(in T value) => this._policy is null ? value : this._policy.Copy(value);
}
