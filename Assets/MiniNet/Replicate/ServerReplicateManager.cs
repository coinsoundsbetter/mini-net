using System;
using System.Collections.Generic;
using System.IO;

namespace MiniNet.Replicate {
    public class ServerReplicateManager {
        private sealed class Entry {
            internal readonly Ghost Ghost;
            internal int? Owner;
            internal Func<int, bool> VisibleTo;
            internal byte[][] Snapshot;
            internal readonly byte[][] DeltaBase;
            internal readonly byte[][] Delta;
            internal Entry(Ghost ghost) {
                Ghost = ghost;
                DeltaBase = new byte[ghost.OrderedComponents.Count][];
                Delta = new byte[ghost.OrderedComponents.Count][];
            }
        }

        private readonly SortedDictionary<uint, Entry> ghosts = new SortedDictionary<uint, Entry>();
        private readonly GhostBindings<IRequiresServerImplementation> bindings = new GhostBindings<IRequiresServerImplementation>();

        public void Bind<TComponent, TImplementation>(Func<TComponent, TImplementation> create)
            where TComponent : GhostComponent, IRequiresServerImplementation where TImplementation : class =>
            bindings.Bind(create);

        private readonly Dictionary<ushort, GhostTemplate> templates = new Dictionary<ushort, GhostTemplate>();
        // Each connection retains references to the immutable component snapshots last sent to it.
        private readonly Dictionary<int, Dictionary<uint, (byte[][] Snapshot, bool Owned)>> observers =
            new Dictionary<int, Dictionary<uint, (byte[][], bool)>>();
        private readonly MemoryStream scratch = new MemoryStream();
        private readonly BinaryWriter writer;
        private readonly List<uint> removed = new List<uint>();
        private readonly HashSet<uint> visible = new HashSet<uint>();
        private uint nextId;
    
        public ServerReplicateManager() { writer = new BinaryWriter(scratch); }

        public void RegisterTemplate(GhostTemplate template) {
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (templates.TryGetValue(template.Id, out var existing)) {
                if (existing != template) throw new InvalidOperationException("A different template already uses this ID.");
                return;
            }
            bindings.Validate(template);
            templates.Add(template.Id, template);
        }

        public Ghost Spawn(GhostTemplate template, int? ownerConnectionId = null) =>
            Create(template, null, ownerConnectionId, null);

        public Ghost SpawnChild(Ghost parent, GhostTemplate template, Func<int, bool> visibleTo = null, int? ownerConnectionId = null) {
            if (parent == null || !ghosts.TryGetValue(parent.NetId, out var entry) || entry.Ghost != parent)
                throw new ArgumentException("Parent must be a live ghost on this server.", nameof(parent));
            return Create(template, parent, ownerConnectionId, visibleTo);
        }

        // Evaluated per connection each flush. A child must also pass every ancestor filter.
        public void SetVisibility(uint netId, Func<int, bool> visibleTo) => ghosts[netId].VisibleTo = visibleTo;

        private Ghost Create(GhostTemplate template, Ghost parent, int? ownerConnectionId, Func<int, bool> visibleTo) {
            if (template == null) throw new ArgumentNullException(nameof(template));
            ValidateOwner(ownerConnectionId);
            RegisterTemplate(template);
            var ghost = template.Instantiate(checked(++nextId), parent);
            bindings.Attach(ghost);
            ghosts.Add(ghost.NetId, new Entry(ghost) { Owner = ownerConnectionId, VisibleTo = visibleTo });
            return ghost;
        }

        public bool Despawn(uint netId) {
            if (!ghosts.ContainsKey(netId)) return false;
            var subtree = new HashSet<uint> { netId };
            foreach (var entry in ghosts.Values) {
                var parent = entry.Ghost.Parent;
                if (parent != null && subtree.Contains(parent.NetId)) subtree.Add(entry.Ghost.NetId);
            }
            foreach (uint id in subtree) ghosts.Remove(id);
            return true;
        }

        private void ValidateOwner(int? ownerConnectionId) {
            if (ownerConnectionId.HasValue && !observers.ContainsKey(ownerConnectionId.Value))
                throw new ArgumentException("Owner must be a connected client.", nameof(ownerConnectionId));
        }

        public void SetOwner(uint netId, int? ownerConnectionId) {
            ValidateOwner(ownerConnectionId);
            ghosts[netId].Owner = ownerConnectionId;
        }

        public bool TryGetOwner(uint netId, out int? ownerConnectionId) {
            bool found = ghosts.TryGetValue(netId, out var entry);
            ownerConnectionId = entry?.Owner;
            return found;
        }

        public bool TryGetOwnedGhost(int sender, uint netId, out Ghost ghost) {
            ghost = null;
            if (!ghosts.TryGetValue(netId, out var entry) || entry.Owner != sender) return false;
            ghost = entry.Ghost;
            return true;
        }

        internal void AddConnection(int connection) =>
            observers.Add(connection, new Dictionary<uint, (byte[][], bool)>());

        internal void ForgetConnection(int connection) {
            observers.Remove(connection);
            foreach (var entry in ghosts.Values)
                if (entry.Owner == connection) entry.Owner = null;
        }
        internal void Reset() {
            ghosts.Clear();
            observers.Clear();
            templates.Clear();
        }

        // Capture once per tick regardless of observer count. Only changed bytes are retained.
        private void Capture(Entry entry) {
            var components = entry.Ghost.OrderedComponents;
            var previous = entry.Snapshot;
            var current = previous ?? new byte[components.Count][];
            for (int i = 0; i < components.Count; i++) {
                var component = components[i];
                bool tracked = component.DetectFieldChanges() == component.GetType();
                if (tracked && previous != null && component.DirtyMask == 0) continue;
                scratch.SetLength(0);
                component.Serialize(writer, true);
                var bytes = scratch.GetBuffer().AsSpan(0, checked((int)scratch.Length));
                if (previous != null && bytes.SequenceEqual(previous[i])) continue;
                if (previous != null && ReferenceEquals(current, previous))
                    current = (byte[][])previous.Clone();
                current[i] = bytes.ToArray();
                if (tracked && previous != null) {
                    scratch.SetLength(0);
                    writer.Write(component.DirtyMask);
                    component.Serialize(writer, false);
                    entry.Delta[i] = scratch.ToArray();
                    entry.DeltaBase[i] = previous[i];
                }
            }
            entry.Snapshot = current;
            foreach (var component in components) component.ClearDirty();
        }

        internal void Flush(Action<int, ArraySegment<byte>, DeliveryMode> send) {
            if (observers.Count == 0) return;
            foreach (var entry in ghosts.Values) Capture(entry);
            foreach (var observer in observers) {
                int connection = observer.Key;
                var known = observer.Value;
                // IDs increase at spawn, so parents precede descendants.
                visible.Clear();
                foreach (var entry in ghosts.Values) {
                    var parent = entry.Ghost.Parent;
                    if ((parent == null || visible.Contains(parent.NetId)) &&
                        (entry.VisibleTo == null || entry.VisibleTo(connection)))
                        visible.Add(entry.Ghost.NetId);
                }
                removed.Clear();
                foreach (var id in known.Keys)
                    if (!visible.Contains(id)) removed.Add(id);
                removed.Sort((left, right) => right.CompareTo(left));
                foreach (uint id in removed) {
                    BeginPacket(NetworkMessages.Despawn, id);
                    send(connection, new ArraySegment<byte>(scratch.ToArray()), DeliveryMode.Reliable);
                    known.Remove(id);
                }

                foreach (var entry in ghosts.Values) {
                    var ghost = entry.Ghost;
                    if (!visible.Contains(ghost.NetId)) continue;
                    var current = entry.Snapshot;
                    bool owned = entry.Owner == connection;
                    if (!known.TryGetValue(ghost.NetId, out var last)) {
                        BeginPacket(ghost.Parent == null ? NetworkMessages.Spawn : NetworkMessages.SpawnChild, ghost.NetId);
                        if (ghost.Parent != null) writer.Write(ghost.Parent.NetId);
                        writer.Write(ghost.TemplateId);
                        writer.Write(owned);
                        foreach (var bytes in current) writer.Write(bytes);
                    } else {
                        var previous = last.Snapshot;
                        if (owned != last.Owned) {
                            BeginPacket(NetworkMessages.OwnershipChanged, ghost.NetId);
                            writer.Write(owned);
                            send(connection, new ArraySegment<byte>(scratch.ToArray()), DeliveryMode.Reliable);
                            known[ghost.NetId] = (previous, owned);
                        }
                        if (ReferenceEquals(previous, current)) continue;
                        BeginPacket(NetworkMessages.Update, ghost.NetId);
                        for (int i = 0; i < current.Length; i++) {
                            if (ReferenceEquals(previous[i], current[i])) continue;
                            // A lagging observer may need a full component to catch up.
                            bool delta = ReferenceEquals(previous[i], entry.DeltaBase[i]);
                            writer.Write(delta ? ~i : i);
                            writer.Write(delta ? entry.Delta[i] : current[i]);
                        }
                    }
                    send(connection, new ArraySegment<byte>(scratch.ToArray()), DeliveryMode.Reliable);
                    known[ghost.NetId] = (current, owned);
                }
            }
        }

        private void BeginPacket(byte kind, uint id) {
            scratch.SetLength(0);
            writer.Write(kind);
            writer.Write(id);
        }
    }
}
