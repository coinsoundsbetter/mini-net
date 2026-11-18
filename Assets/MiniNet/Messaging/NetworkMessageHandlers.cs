using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MiniNet {
    internal sealed class NetworkMessageHandlers {
        private readonly Dictionary<uint, (Type Type, Action<int, BinaryReader, DeliveryMode> Invoke)> handlers =
            new Dictionary<uint, (Type, Action<int, BinaryReader, DeliveryMode>)>();
        private readonly Action<int, ArraySegment<byte>, DeliveryMode> receiveRaw;
        private readonly Action<int, string> reportError;

        internal NetworkMessageHandlers(Action<int, ArraySegment<byte>, DeliveryMode> receiveRaw,
            Action<int, string> reportError) {
            this.receiveRaw = receiveRaw;
            this.reportError = reportError;
        }

        internal void Register<T>(Action<int, T, DeliveryMode> handler) where T : struct, INetworkMessage {
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }
            NetworkMessageSerializer<T>.EnsureRegistered();
            uint id = NetworkMessageId<T>.Value;
            if (handlers.TryGetValue(id, out var existing) && existing.Type != typeof(T)) {
                throw new InvalidOperationException($"MiniNet message ID collision: {existing.Type} and {typeof(T)}.");
            }
            handlers[id] = (typeof(T), (connectionId, reader, mode) => {
                T message;
                try {
                    message = NetworkMessages.Read<T>(reader);
                } catch (Exception exception) when (exception is IOException ||
                                                     exception is FormatException ||
                                                     exception is DecoderFallbackException) {
                    reportError(connectionId, $"Invalid MiniNet message {id}: {exception.Message}");
                    return;
                }
                handler(connectionId, message, mode);
            });
        }

        internal void Receive(int connectionId, ArraySegment<byte> packet, DeliveryMode mode) {
            if (packet.Count > 0 && packet.Array[packet.Offset] == NetworkMessages.Raw) {
                receiveRaw(connectionId, new ArraySegment<byte>(packet.Array, packet.Offset + 1, packet.Count - 1), mode);
                return;
            }
            if (packet.Count < 1 + sizeof(uint) || packet.Array[packet.Offset] != NetworkMessages.Typed) {
                reportError(connectionId, "Invalid MiniNet message frame.");
                return;
            }

            using var stream = new MemoryStream(packet.Array, packet.Offset + 1, packet.Count - 1);
            using var reader = new BinaryReader(stream, StrictUtf8);
            uint id = reader.ReadUInt32();
            if (!handlers.TryGetValue(id, out var registered)) {
                reportError(connectionId, $"No MiniNet handler is registered for message ID {id}.");
                return;
            }
            registered.Invoke(connectionId, reader, mode);
        }

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    }
}
