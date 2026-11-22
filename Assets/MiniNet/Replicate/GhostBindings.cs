using System;
using System.Collections.Generic;

namespace MiniNet.Replicate {
    public interface IRequiresServerImplementation { }
    public interface IRequiresClientImplementation { }

    internal sealed class GhostBindings<TRequirement> {
        private readonly Dictionary<Type, Func<GhostComponent, object>> factories =
            new Dictionary<Type, Func<GhostComponent, object>>();

        internal void Bind<TComponent, TImplementation>(Func<TComponent, TImplementation> create)
            where TComponent : GhostComponent where TImplementation : class {
            if (create == null) throw new ArgumentNullException(nameof(create));
            if (factories.ContainsKey(typeof(TComponent)))
                throw new InvalidOperationException($"Duplicate {typeof(TRequirement).Name} binding for {typeof(TComponent).Name}.");
            factories.Add(typeof(TComponent), component => create((TComponent)component));
        }

        internal void Validate(GhostTemplate template) {
            if (template == null) throw new ArgumentNullException(nameof(template));
            foreach (Type type in template.ComponentTypes) {
                if (typeof(TRequirement).IsAssignableFrom(type) && !factories.ContainsKey(type))
                    throw new InvalidOperationException($"Template {template.Id}: {type.Name} requires a binding for {typeof(TRequirement).Name}.");
            }
        }

        internal void Attach(Ghost ghost) {
            foreach (GhostComponent component in ghost.OrderedComponents) {
                if (factories.TryGetValue(component.GetType(), out var create))
                    component.LocalImplementation = create(component) ?? throw new InvalidOperationException(
                        $"The implementation factory for {component.GetType().Name} returned null.");
            }
        }
    }
}
