using System;
using System.Collections.Generic;

namespace MiniNet.Replicate {
    public class Ghost {
        public uint NetId { get; }
        public Ghost Parent { get; }
        public ushort TemplateId { get; }
        private readonly List<GhostComponent> ordered = new List<GhostComponent>();
        internal IReadOnlyList<GhostComponent> OrderedComponents => ordered;

        public Ghost() { }
        internal Ghost(uint netId, ushort templateId, Ghost parent) {
            Parent = parent;
            NetId = netId;
            TemplateId = templateId;
        }

        internal void Attach(GhostComponent component) {
            m_Components.Add(component.GetType(), component);
            component.Owner = this;
            ordered.Add(component);
        }

        public T GetComponent<T>() where T : GhostComponent => (T)m_Components[typeof(T)];
        private Dictionary<Type, GhostComponent> m_Components = new Dictionary<Type, GhostComponent>();
        
        public bool TryGetComponent<T>(out T component) where T : GhostComponent {
            if (m_Components.TryGetValue(typeof(T), out var value)
                && value is T result)
            {
                component = result;
                return true;
            }

            component = default;
            return false;
        }
    }
}
