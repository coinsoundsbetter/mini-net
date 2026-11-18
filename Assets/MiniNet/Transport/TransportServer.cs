using System;
using MiniNet.Kcp2k;

namespace MiniNet {
    public sealed class TransportServer {
        private readonly KcpServer server;

        public event Action<int> ClientConnected;
        public event Action<int, ArraySegment<byte>, DeliveryMode> DataReceived;
        public event Action<int> ClientDisconnected;
        public event Action<int, string> Error;

        public TransportServer() {
            server = new KcpServer(
                (id, endpoint) => ClientConnected?.Invoke(id),
                (id, payload, channel) => DataReceived?.Invoke(id, payload, KcpDeliveryMode.FromKcp(channel)),
                id => ClientDisconnected?.Invoke(id),
                (id, code, message) => Error?.Invoke(id, $"{code}: {message}"),
                new KcpConfig());
        }

        public void Start(ushort port) => server.Start(port);
        public void Stop() => server.Stop();
        public void Disconnect(int connectionId) => server.Disconnect(connectionId);
        public void Send(int connectionId, ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable) =>
            server.Send(connectionId, payload, KcpDeliveryMode.ToKcp(mode));

        public void TickIncoming() => server.TickIncoming();
        public void TickOutgoing() => server.TickOutgoing();
    }
}
