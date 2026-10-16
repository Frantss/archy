namespace Archy;

public enum TargetDeletion { Restrict, RemoveComponent }

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method)]
public sealed class EntityReferenceAttribute : Attribute {
  public TargetDeletion OnDelete { get; set; } = TargetDeletion.Restrict;
  public bool Optional { get; set; }
}
