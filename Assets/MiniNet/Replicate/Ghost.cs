using System;
using System.Collections.Generic;

namespace MiniNet.Replicate {
    public class Ghost {
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
