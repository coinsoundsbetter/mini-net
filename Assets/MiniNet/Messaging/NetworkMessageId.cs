namespace MiniNet {
    internal static class NetworkMessageId<T> where T : struct, INetworkMessage {
        internal static readonly uint Value = CalculateId();

        private static uint CalculateId() {
            uint hash = 2166136261;
            foreach (char character in typeof(T).FullName) {
                hash = unchecked((hash ^ character) * 16777619);
            }
            return hash;
        }
    }
}
