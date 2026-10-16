namespace Archy;

public sealed partial class EntityStore {
  public CommandBuffer CreateCommandBuffer() { this.EnsureAccess(); return new(this); }
  internal void EnsureRecording() {
    this.EnsureAccess();

    if (this._preparing) throw new InvalidOperationException("Preparation callbacks cannot record commands.");
  }

  internal void EnsurePlayback() {
    this.EnsureAccess();
    this.EnsureMutation();
    this.EnsureStructural();
  }
}
