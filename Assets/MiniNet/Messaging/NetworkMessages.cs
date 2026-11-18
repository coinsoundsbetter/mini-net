using System;
using System.IO;
using System.Text;

namespace MiniNet {
    internal static class NetworkMessages {
        internal const byte Raw = 0;
        internal const byte Typed = 1;

        internal static ArraySegment<byte> Pack<T>(T message) where T : struct, INetworkMessage {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Typed);
            writer.Write(NetworkMessageId<T>.Value);
            NetworkMessageSerializer<T>.Write(writer, message);
            return new ArraySegment<byte>(stream.GetBuffer(), 0, (int)stream.Length);
        }

        internal static ArraySegment<byte> PackRaw(ArraySegment<byte> payload) {
            if (payload.Count == 0) {
                throw new ArgumentException("Payload must contain at least one byte.", nameof(payload));
            }
            var packet = new byte[checked(payload.Count + 1)];
            Buffer.BlockCopy(payload.Array, payload.Offset, packet, 1, payload.Count);
            return new ArraySegment<byte>(packet);
        }

        internal static T Read<T>(BinaryReader reader) where T : struct, INetworkMessage {
            T message = NetworkMessageSerializer<T>.Read(reader);
            if (reader.BaseStream.Position != reader.BaseStream.Length) {
                throw new InvalidDataException("Unexpected trailing bytes in MiniNet message.");
            }
            return message;
        }
    }
}
