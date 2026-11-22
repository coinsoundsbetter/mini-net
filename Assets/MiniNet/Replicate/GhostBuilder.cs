using System;
using System.Collections.Generic;

namespace MiniNet.Replicate {
    public sealed class GhostBuilder {
        private readonly ushort id;
        private readonly List<Func<GhostComponent>> factories = new List<Func<GhostComponent>>();
        private readonly List<Type> componentTypes = new List<Type>();
        private readonly HashSet<Type> types = new HashSet<Type>();

        public GhostBuilder(ushort id) { this.id = id; }

        public GhostBuilder Component<T>() where T : GhostComponent, new() {
            if (!types.Add(typeof(T))) throw new InvalidOperationException("Duplicate component: " + typeof(T));
            componentTypes.Add(typeof(T));
            factories.Add(() => new T());
            return this;
        }

        public GhostTemplate Build() => new GhostTemplate(id, factories.ToArray(), componentTypes.ToArray());
    }

    public sealed class GhostTemplate {
        public ushort Id { get; }
        private readonly Func<GhostComponent>[] factories;

        internal IReadOnlyList<Type> ComponentTypes { get; }

        internal GhostTemplate(ushort id, Func<GhostComponent>[] factories, Type[] componentTypes) {
            Id = id;
            this.factories = factories;
            ComponentTypes = componentTypes;
        }

        internal Ghost Instantiate(uint netId, Ghost parent = null) {
            var ghost = new Ghost(netId, Id, parent);
            foreach (var create in factories) ghost.Attach(create());
            return ghost;
        }
    }
}
