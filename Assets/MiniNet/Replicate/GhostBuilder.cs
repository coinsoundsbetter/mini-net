using System.Collections.Generic;

namespace MiniNet.Replicate {
    public sealed class GhostBuilder {
        
        private readonly List<GhostComponent> components =
            new List<GhostComponent>();

        private readonly List<SyncAudience> audiences =
            new List<SyncAudience>();

        private readonly HashSet<Type> componentTypes =
            new HashSet<Type>();
    }
}