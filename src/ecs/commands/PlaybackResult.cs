namespace Archy;

public enum CommandOutcome { Succeeded, Failed, Canceled }

public readonly record struct CommandTicket {
  internal object Stream { get; }
  internal int Index { get; }
  internal CommandTicket(object stream, int index) { this.Stream = stream; this.Index = index; }
}

public sealed class PlaybackResult {
  private readonly Dictionary<CommandTicket, CommandOutcome> _outcomes;
  internal PlaybackResult(Dictionary<CommandTicket, CommandOutcome> outcomes) => this._outcomes = outcomes;
  public CommandOutcome Outcome(CommandTicket ticket) => this._outcomes.TryGetValue(ticket, out var outcome)
    ? outcome : throw new ArgumentException("Ticket does not belong to this playback.", nameof(ticket));
  public bool Get(CommandTicket ticket) => this.Outcome(ticket) switch {
    CommandOutcome.Succeeded => true,
    CommandOutcome.Failed => false,
    _ => throw new OperationCanceledException("Command was not executed.")
  };
}

public sealed class PlaybackException : InvalidOperationException {
  public int CommandIndex { get; }
  public PlaybackResult Result { get; }
  internal PlaybackException(int index, PlaybackResult result, Exception cause)
    : base($"Command {index} failed; earlier commands remain committed.", cause) {
    this.CommandIndex = index;
    this.Result = result;
  }
}
