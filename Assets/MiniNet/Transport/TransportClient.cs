using System;
using MiniNet.Kcp2k;

namespace MiniNet {
    public sealed class TransportClient {
        private readonly KcpClient client;

        public event Action Connected;
        public event Action<ArraySegment<byte>, DeliveryMode> DataReceived;
        public event Action Disconnected;
        public event Action<string> Error;

        public TransportClient() {
            client = new KcpClient(
                () => Connected?.Invoke(),
                (payload, channel) => DataReceived?.Invoke(payload, KcpDeliveryMode.FromKcp(channel)),
                () => Disconnected?.Invoke(),
                (code, message) => Error?.Invoke($"{code}: {message}"),
                new KcpConfig());
        }

        public void Connect(string address, ushort port) => client.Connect(address, port);
        public void Disconnect() => client.Disconnect();
        public void Send(ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable) =>
            client.Send(payload, KcpDeliveryMode.ToKcp(mode));
        public void TickIncoming() => client.TickIncoming();
        public void TickOutgoing() => client.TickOutgoing();
    }
}
