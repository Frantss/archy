namespace Archy;

public sealed class MutationException : InvalidOperationException {
  public MutationException(string message) : base(message) { }
}
