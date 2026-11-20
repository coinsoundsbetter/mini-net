using System.IO;

namespace MiniNet.Replicate {
    public abstract class GhostComponent {
        public Ghost Owner { get; internal set; }
        private ulong dirtyMask;

        protected void SetDirty(ulong mask) {
            dirtyMask |= mask;
        }

        internal ulong DirtyMask => dirtyMask;
        internal void ClearDirty() => dirtyMask = 0;
        
        public virtual void Serialize(BinaryWriter writer, bool initialState) { }
        public virtual void Deserialize(BinaryReader reader, bool initialState) { }
    }
}
