using System.IO;
using System;
using System.Collections.Generic;

namespace MiniNet.Replicate {
    public abstract class GhostComponent {
        public Ghost Owner { get; internal set; }
        internal object LocalImplementation { private get; set; }

        public T GetImplementation<T>() where T : class =>
            LocalImplementation as T ?? throw new InvalidOperationException(
                $"{GetType().Name} has no local implementation of type {typeof(T).Name}.");

        private ulong dirtyMask;

        protected void SetDirty(ulong mask) {
            dirtyMask |= mask;
        }

        internal ulong DirtyMask => dirtyMask;
        internal void ClearDirty() => dirtyMask = 0;
        // Generated overrides return the concrete type whose complete layout they track.
        protected internal virtual Type DetectFieldChanges() => null;
        protected void TrackField<T>(ref T baseline, T value, ulong bit) {
            if (EqualityComparer<T>.Default.Equals(baseline, value)) return;
            baseline = value;
            SetDirty(bit);
        }

        protected bool IncludesField(ulong bit) => (dirtyMask & bit) != 0;
        internal void ReadDelta(BinaryReader reader) {
            dirtyMask = reader.ReadUInt64();
            try { Deserialize(reader, false); }
            finally { ClearDirty(); }
        }
        
        public virtual void Serialize(BinaryWriter writer, bool initialState) { }
        public virtual void Deserialize(BinaryReader reader, bool initialState) { }

        protected static void WriteGhostString(BinaryWriter writer, string value) {
            writer.Write(value != null);
            if (value != null) writer.Write(value);
        }

        protected static string ReadGhostString(BinaryReader reader) =>
            reader.ReadBoolean() ? reader.ReadString() : null;
    }
}
