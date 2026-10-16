namespace Archy.Tests;

internal static class Check {
  public static int Count { get; private set; }

  public static void That(bool condition, string message) {
    if (!condition) throw new InvalidOperationException(message);

    Count++;
  }

  public static void Throws<T>(Action action) where T : Exception {

    try {
      action();
    }
    catch (T) {
      Count++;

      return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
  }
}
