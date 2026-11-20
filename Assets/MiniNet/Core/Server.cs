using System;
using System.Collections.Generic;
using MiniNet.Replicate;

namespace MiniNet {
    public class Server {
        private TransportServer transport;
        private readonly HashSet<int> connections = new HashSet<int>();
        private readonly NetworkMessageHandlers messages;
        public event Action<int> RemoteClientConnected;
        public event Action<int> RemoteClientDisconnected;
        public event Action<int, ArraySegment<byte>, DeliveryMode> DataReceived;
        public event Action<int, string> Error;
        public ServerState State { get; private set; }
        public ServerReplicateManager ReplicateManager { get; private set; } = new ServerReplicateManager();

        public Server(TransportServer useTransport) {
            transport = useTransport ?? throw new ArgumentNullException(nameof(useTransport));
            messages = new NetworkMessageHandlers(
                (id, payload, mode) => DataReceived?.Invoke(id, payload, mode), HandleError);
            transport.ClientConnected += HandleClientConnected;
            transport.ClientDisconnected += HandleClientDisconnected;
            transport.DataReceived += messages.Receive;
            transport.Error += HandleError;
        }

        public void Listen(ushort port) {
            try {
                transport.Start(port);
                State = ServerState.Started;
            } catch {
                transport.Stop();
                throw;
            }
        }

        public void Shutdown() {
            if (State == ServerState.Stopped) {
                return;
            }

            transport.Stop();
            State = ServerState.Stopped;
            var disconnected = new int[connections.Count];
            connections.CopyTo(disconnected);
            connections.Clear();
            foreach (int id in disconnected) {
                RemoteClientDisconnected?.Invoke(id);
            }
        }

        public void Send(int connectionId, ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable) {
            SendPacket(connectionId, NetworkMessages.PackRaw(payload), mode);
        }

        public void Send<T>(int connectionId, T payload, DeliveryMode mode = DeliveryMode.Reliable)
            where T : struct, INetworkMessage {
            SendPacket(connectionId, NetworkMessages.Pack(payload), mode);
        }

        public void RegisterHandler<T>(Action<int, T, DeliveryMode> handler) where T : struct, INetworkMessage {
            messages.Register(handler);
        }

        private void SendPacket(int connectionId, ArraySegment<byte> payload, DeliveryMode mode) {
            if (!connections.Contains(connectionId)) {
                throw new InvalidOperationException("目标客户端未连接");
            }
            
            transport.Send(connectionId, payload, mode);
        }

        public void DisconnectClient(int connectionId) {
            if (connections.Contains(connectionId)) {
                transport.Disconnect(connectionId);
            }
        }

        public void TickIncoming() {
            if (State == ServerState.Started) {
                transport.TickIncoming();
            }
        }

        public void TickOutgoing() {
            if (State == ServerState.Started) {
                transport.TickOutgoing();
            }
        }

        private void HandleClientConnected(int id) {
            connections.Add(id);
            RemoteClientConnected?.Invoke(id);
        }

        private void HandleClientDisconnected(int id) {
            if (connections.Remove(id)) {
                RemoteClientDisconnected?.Invoke(id);
            }
        }

        private void HandleError(int id, string message) => Error?.Invoke(id, message);
    }
}
