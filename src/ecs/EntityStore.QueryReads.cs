using System.Runtime.CompilerServices;

namespace Archy;

public sealed partial class EntityStore {
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private bool TryCachedRead<T>(Entity entity, out T value) where T : struct, IComponent {
    value = default;

    if (this._writeEntity != entity) return false;

    var values = this._readValues as T[] ?? this._readOtherValues as T[];

    if (values is null) return false;

    this.EnsureThread();

    if (this._disposed) return false;

    value = values[this._writeRow];

    return true;
  }

  private void RememberRead<T>(Entity entity, ref EntitySlot slot, T[] values) where T : struct, IComponent {
    if (!ReferenceEquals(this._writeChunk, slot.Chunk)) return;
    if (!ReferenceEquals(this._readValues, values) && !ReferenceEquals(this._readOtherValues, values)) {
      this._readOtherValues = this._readValues;
      this._readValues = values;
    }

    this._writeEntity = entity;
    this._writeRow = slot.Row;
    this._hasBoundAccess = true;
  }
}
