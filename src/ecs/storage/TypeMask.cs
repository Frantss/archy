namespace Archy;

internal sealed class TypeMask {
  private readonly ulong[] _words;

  public TypeMask(TypeDescriptor[] types) {
    this._words = types.Length == 0 ? [] : new ulong[(types.Max(type => type.Id) >> 6) + 1];

    foreach (var type in types) this._words[type.Id >> 6] |= 1UL << (type.Id & 63);
  }

  public bool Has(int id) => (id >> 6) < this._words.Length && (this._words[id >> 6] & (1UL << (id & 63))) != 0;

  public bool HasAll(TypeMask other) {

    for (var i = 0; i < other._words.Length; i++) {
      var word = i < this._words.Length ? this._words[i] : 0;

      if ((word & other._words[i]) != other._words[i]) return false;
    }

    return true;
  }

  public bool HasAny(TypeMask other) {

    for (var i = 0; i < Math.Min(this._words.Length, other._words.Length); i++) {
      if ((this._words[i] & other._words[i]) != 0) return true;
    }

    return false;
  }
}
