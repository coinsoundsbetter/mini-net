using System;

namespace MiniNet.Replicate {

    public enum ReplicateDirection {
        ServerToClient,
        ClientToServer,
    }
    
    [AttributeUsage(AttributeTargets.Field)]
    public class GhostFieldAttribute : Attribute {
        public ReplicateDirection Direction;
    }
}