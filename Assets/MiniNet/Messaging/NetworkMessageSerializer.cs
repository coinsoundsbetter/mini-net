using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace MiniNet {
    public static class NetworkMessageSerializer<T> where T : struct, INetworkMessage {
        private static volatile Action<BinaryWriter, T> writer;
        private static Func<BinaryReader, T> reader;

        // Called by the message's static constructor after weaving.
        public static void Register(Action<BinaryWriter, T> write, Func<BinaryReader, T> read) {
            if (write == null) {
                throw new ArgumentNullException(nameof(write));
            }
            reader = read ?? throw new ArgumentNullException(nameof(read));
            writer = write;
        }

        internal static void EnsureRegistered() {
            if (writer == null) {
                RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
            }
            if (writer == null) {
                throw new InvalidOperationException($"MiniNet Weaver did not generate a serializer for {typeof(T).FullName}.");
            }
        }

        internal static void Write(BinaryWriter buffer, T message) {
            EnsureRegistered();
            writer(buffer, message);
        }

        internal static T Read(BinaryReader buffer) {
            EnsureRegistered();
            return reader(buffer);
        }
    }
}
