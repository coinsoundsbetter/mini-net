using System;
using MiniNet.Replicate;

namespace MiniNet {
    public class Client {
        private readonly TransportClient transport;
        private readonly NetworkMessageHandlers messages;

        public event Action OnConnected;
        public event Action<ArraySegment<byte>, DeliveryMode> DataReceived;
        public event Action OnDisconnected;
        public event Action<string> Error;

        public ClientReplicateManager ReplicateManager { get; private set; } = new ClientReplicateManager();

        public ClientState State { get; private set; }

        public void Bind<TComponent, TImplementation>(Func<TComponent, TImplementation> create)
            where TComponent : GhostComponent, IRequiresClientImplementation where TImplementation : class =>
            ReplicateManager.Bind(create);

        public Client(TransportClient useTransport) {
            transport = useTransport ?? throw new ArgumentNullException(nameof(useTransport));
            messages = new NetworkMessageHandlers(
                (id, payload, mode) => DataReceived?.Invoke(payload, mode),
                (id, error) => Error?.Invoke(error));
            transport.Connected += () => {
                State = ClientState.Connected;
                OnConnected?.Invoke();
            };
            transport.DataReceived += HandlePacket;
            transport.Disconnected += () => {
                ReplicateManager.Reset();
                State = ClientState.Disconnected;
                OnDisconnected?.Invoke();
            };
            transport.Error += message => Error?.Invoke(message);
        }

        public void Connect(string serverAddress, ushort port) {
            if (State != ClientState.Disconnected) {
                throw new InvalidOperationException("Disconnect before starting another connection.");
            }

            State = ClientState.Connecting;
            try {
                transport.Connect(serverAddress, port);
            } catch {
                transport.Disconnect();
                State = ClientState.Disconnected;
                throw;
            }
        }

        public void Disconnect() {
            if (State != ClientState.Connecting && State != ClientState.Connected) {
                return;
            }

            State = ClientState.Disconnecting;
            transport.Disconnect();
        }

        public void Send(ArraySegment<byte> payload, DeliveryMode mode = DeliveryMode.Reliable) {
            SendPacket(NetworkMessages.PackRaw(payload), mode);
        }

        private void SendPacket(ArraySegment<byte> payload, DeliveryMode mode) {
            if (State != ClientState.Connected) {
                throw new InvalidOperationException("Connect before sending messages.");
            }

            transport.Send(payload, mode);
        }

        public void Send<T>(T payload, DeliveryMode mode = DeliveryMode.Reliable)
            where T : struct, INetworkMessage {
            SendPacket(NetworkMessages.Pack(payload), mode);
        }

        public void RegisterHandler<T>(Action<T, DeliveryMode> handler) where T : struct, INetworkMessage {
            if (handler == null) {
                throw new ArgumentNullException(nameof(handler));
            }
            messages.Register<T>((id, message, mode) => handler(message, mode));
        }

        public void TickIncoming() => transport.TickIncoming();
        private void HandlePacket(ArraySegment<byte> packet, DeliveryMode mode) {
            byte kind = packet.Count == 0 ? NetworkMessages.Raw : packet.Array[packet.Offset];
            if (kind != NetworkMessages.Spawn && kind != NetworkMessages.SpawnChild && kind != NetworkMessages.Update &&
                kind != NetworkMessages.Despawn && kind != NetworkMessages.OwnershipChanged) {
                messages.Receive(0, packet, mode);
                return;
            }
            Ghost ghost;
            try {
                ghost = ReplicateManager.ReadPacket(kind, packet);
            } catch (Exception error) when (error is System.IO.IOException || error is System.IO.InvalidDataException) {
                Disconnect();
                Error?.Invoke(error.Message);
                return;
            }
            ReplicateManager.Notify(kind, ghost);
        }
        public void TickOutgoing() => transport.TickOutgoing();
    }
}
