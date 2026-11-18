using System;

namespace MiniNet {
    public class Client {
        public event Action OnConneted;
        public event Action DataReceived;
        public event Action OnDisconnected;

        public void Connect(string serverAddress) {
            
        }

        public void Disconnect() {
            
        }
    }
}