namespace Archy;

public interface IComponent { }
public interface ITag { }

public interface IComponentCopy<T> where T : struct, IComponent {
  T Copy(in T value);
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class ComponentCopyAttribute : Attribute {
  public Type PolicyType { get; }

  public ComponentCopyAttribute(Type policyType) => this.PolicyType = policyType;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ImmutableComponentValueAttribute : Attribute { }
