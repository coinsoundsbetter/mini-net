using System;

namespace MiniNet {
    public class Server {
        public event Action<int> OnClientConnted;
        public event Action<int> OnClientDisconnected;
        public event Action<byte[]> OnDataReceived;

        public void Listen(int port) {
            
        }

        public void Shutdown() {
            
        }
    }
}
