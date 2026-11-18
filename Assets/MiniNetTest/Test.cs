using MiniNet;
using UnityEngine;
using UnityEngine.LowLevel;

namespace MiniNetTest {
    public class Test : MonoBehaviour {
        private Client mClient;
        private Server mServer;
        
        private void Start() {
            mServer = new Server();
            mServer.OnClientConnted += Server_OnClientConnected;
            mServer.OnClientDisconnected += Server_OnClientDisconnected;
            mServer.Listen(7799);
            
            mClient = new Client();
            mClient.OnConneted += Client_OnConnected;
            mClient.OnDisconnected += Client_OnDisconnected;
            mClient.Connect(NetworkConsts.LocalHost);
            
            PlayerLoop.
        }

        private void Server_OnClientDisconnected(int obj) {
        }

        private void Server_OnClientConnected(int obj) {
        }

        private void Client_OnConnected() {
        }

        private void Client_OnDisconnected() {
        }
    }
}