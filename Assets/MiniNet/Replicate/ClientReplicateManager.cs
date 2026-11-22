using System;
using System.Collections.Generic;
using System.IO;

namespace MiniNet.Replicate {
    public class ClientReplicateManager {
        private readonly GhostBindings<IRequiresClientImplementation> bindings = new GhostBindings<IRequiresClientImplementation>();

        public void Bind<TComponent, TImplementation>(Func<TComponent, TImplementation> create)
            where TComponent : GhostComponent, IRequiresClientImplementation where TImplementation : class =>
            bindings.Bind(create);

        private readonly Dictionary<ushort, GhostTemplate> templates = new Dictionary<ushort, GhostTemplate>();
        private readonly Dictionary<uint, Ghost> ghosts = new Dictionary<uint, Ghost>();
        public event Action<Ghost> Spawned;
        public event Action<Ghost> Updated;
        public event Action<Ghost> Despawned;
        public event Action<Ghost, bool> OwnershipChanged;
        private readonly HashSet<uint> owned = new HashSet<uint>();

        public void RegisterTemplate(GhostTemplate template) {
            bindings.Validate(template);
            templates.Add(template.Id, template);
        }
        public bool TryGetGhost(uint id, out Ghost ghost) => ghosts.TryGetValue(id, out ghost);
        public bool IsOwned(uint netId) => owned.Contains(netId);
        internal void Reset() {
            ghosts.Clear();
            owned.Clear();
        }

        internal Ghost ReadPacket(byte kind, ArraySegment<byte> packet) {
            using var stream = new MemoryStream(packet.Array, packet.Offset + 1, packet.Count - 1);
            using var reader = new BinaryReader(stream);
            uint id = reader.ReadUInt32();
            if (kind != NetworkMessages.Spawn && kind != NetworkMessages.SpawnChild) {
                if (!ghosts.TryGetValue(id, out var existing))
                    throw new InvalidDataException("Unknown ghost ID: " + id);
                if (kind == NetworkMessages.Despawn) {
                    if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected despawn payload.");
                    ghosts.Remove(id);
                    owned.Remove(id);
                } else if (kind == NetworkMessages.OwnershipChanged) {
                    bool isOwned = reader.ReadBoolean();
                    if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected ownership payload.");
                    if (isOwned) owned.Add(id);
                    else owned.Remove(id);
                } else {
                    int lastIndex = -1;
                    while (stream.Position < stream.Length) {
                        int index = reader.ReadInt32();
                        bool delta = index < 0;
                        if (delta) index = ~index;
                        if (index <= lastIndex || index >= existing.OrderedComponents.Count)
                            throw new InvalidDataException("Invalid update component index: " + index);
                        if (delta) existing.OrderedComponents[index].ReadDelta(reader);
                        else existing.OrderedComponents[index].Deserialize(reader, true);
                        lastIndex = index;
                    }
                    if (lastIndex < 0) throw new InvalidDataException("Empty ghost update.");
                }
                return existing;
            }
            Ghost parent = null;
            if (kind == NetworkMessages.SpawnChild && !ghosts.TryGetValue(reader.ReadUInt32(), out parent))
                throw new InvalidDataException("Unknown parent ghost.");
            ushort templateId = reader.ReadUInt16();
            bool initiallyOwned = reader.ReadBoolean();
            if (!templates.TryGetValue(templateId, out var template))
                throw new InvalidDataException("Unknown ghost template: " + templateId);
            if (ghosts.ContainsKey(id)) throw new InvalidDataException("Duplicate ghost ID: " + id);
            var ghost = template.Instantiate(id, parent);
            foreach (var component in ghost.OrderedComponents) component.Deserialize(reader, true);
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected spawn payload.");
            bindings.Attach(ghost);
            ghosts.Add(id, ghost);
            if (initiallyOwned) owned.Add(id);
            return ghost;
        }

        internal void Notify(byte kind, Ghost ghost) {
            switch (kind) {
                case NetworkMessages.SpawnChild:
                case NetworkMessages.Spawn: Spawned?.Invoke(ghost); break;
                case NetworkMessages.Update: Updated?.Invoke(ghost); break;
                case NetworkMessages.Despawn: Despawned?.Invoke(ghost); break;
                case NetworkMessages.OwnershipChanged: OwnershipChanged?.Invoke(ghost, IsOwned(ghost.NetId)); break;
            }
        }
    }
}
