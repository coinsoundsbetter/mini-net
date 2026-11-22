using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MiniNet.Examples;
using MiniNet.Replicate;
using NUnit.Framework;

namespace MiniNet.Tests {
    public class GhostBindingTests {
        public sealed class ClientOnly : GhostComponent, IRequiresClientImplementation { }

        public sealed class TacticalTarget : GhostComponent {
            public int TargetId;
            public override void Serialize(BinaryWriter writer, bool initialState) => writer.Write(TargetId);
            public override void Deserialize(BinaryReader reader, bool initialState) => TargetId = reader.ReadInt32();
        }

        [Test]
        public void TeamOnlyChildFollowsMembershipAndParentVisibilityOverLoopback() {
            var server = new Server(new TransportServer());
            var client = new Client(new TransportClient());
            var enemy = new Client(new TransportClient());
            var playerTemplate = new GhostBuilder(20).Build();
            var tacticsTemplate = new GhostBuilder(21).Component<TacticalTarget>().Build();
            foreach (var peer in new[] { client, enemy }) {
                peer.ReplicateManager.RegisterTemplate(playerTemplate);
                peer.ReplicateManager.RegisterTemplate(tacticsTemplate);
            }

            // 队伍数据放在独立子模板中。visibleTo 收到服务端连接 ID，默认继承父对象可见范围。
            var team = new HashSet<int>();
            var manager = server.ReplicateManager;
            var player = manager.Spawn(playerTemplate);
            var tactics = manager.SpawnChild(player, tacticsTemplate, visibleTo: team.Contains);
            tactics.GetComponent<TacticalTarget>().TargetId = 42;
            int? sender = null;
            server.RemoteClientConnected += id => sender ??= id;
            int enemyChildSpawns = 0;
            enemy.ReplicateManager.Spawned += ghost => {
                if (ghost.NetId == tactics.NetId) enemyChildSpawns++;
            };
            var removed = new List<uint>();
            client.ReplicateManager.Despawned += ghost => removed.Add(ghost.NetId);

            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;
            try {
                server.Listen(port);
                client.Connect("127.0.0.1", port);
                PumpUntil(() => client.ReplicateManager.TryGetGhost(player.NetId, out _));
                Assert.IsFalse(client.ReplicateManager.TryGetGhost(tactics.NetId, out _));
                enemy.Connect("127.0.0.1", port);
                PumpUntil(() => enemy.ReplicateManager.TryGetGhost(player.NetId, out _));

                // 中途入队，无需修改字段；客户端首次收到完整的当前状态。
                team.Add(sender.Value);
                Ghost local = null;
                PumpUntil(() => client.ReplicateManager.TryGetGhost(tactics.NetId, out local));
                Assert.AreEqual(player.NetId, local.Parent.NetId);
                Assert.AreEqual(42, local.GetComponent<TacticalTarget>().TargetId);
                tactics.GetComponent<TacticalTarget>().TargetId = 73;
                PumpUntil(() => local.GetComponent<TacticalTarget>().TargetId == 73);

                // 退队只撤销子对象。父对象隐藏时，即使重新入队也不能越过父级过滤。
                team.Clear();
                PumpUntil(() => !client.ReplicateManager.TryGetGhost(tactics.NetId, out _));
                Assert.IsTrue(client.ReplicateManager.TryGetGhost(player.NetId, out _));
                manager.SetVisibility(player.NetId, id => false);
                team.Add(sender.Value);
                PumpUntil(() => !client.ReplicateManager.TryGetGhost(player.NetId, out _));
                Assert.IsFalse(client.ReplicateManager.TryGetGhost(tactics.NetId, out _));
                manager.SetVisibility(player.NetId, null);
                PumpUntil(() => client.ReplicateManager.TryGetGhost(tactics.NetId, out local));
                Assert.AreEqual(73, local.GetComponent<TacticalTarget>().TargetId);

                removed.Clear();
                Assert.IsTrue(manager.Despawn(player.NetId));
                PumpUntil(() => removed.Count == 2 && !enemy.ReplicateManager.TryGetGhost(player.NetId, out _));
                CollectionAssert.AreEqual(new[] { tactics.NetId, player.NetId }, removed);
                Assert.IsFalse(manager.Despawn(tactics.NetId));
                Assert.AreEqual(0, enemyChildSpawns);
            } finally {
                client.Disconnect();
                enemy.Disconnect();
                server.Shutdown();
            }

            void PumpUntil(Func<bool> complete) {
                var timeout = Stopwatch.StartNew();
                while (!complete()) {
                    if (timeout.ElapsedMilliseconds > 5000) Assert.Fail("Timed out waiting for child replication.");
                    server.TickIncoming();
                    client.TickIncoming();
                    enemy.TickIncoming();
                    server.TickOutgoing();
                    client.TickOutgoing();
                    enemy.TickOutgoing();
                    Thread.Sleep(1);
                }
            }
        }
        [Test]
        public void ValidatesOnlyLocalRequirementsAndAllowsRetry() {
            var template = new GhostBuilder(8).Component<ClientOnly>().Component<PlayerState>().Build();
            var server = new ServerReplicateManager();
            var client = new ClientReplicateManager();

            Assert.DoesNotThrow(() => server.Spawn(template));
            Assert.Throws<InvalidOperationException>(() => client.RegisterTemplate(template));
            client.Bind((ClientOnly component) => new object());
            Assert.DoesNotThrow(() => client.RegisterTemplate(template));
            Assert.Throws<InvalidOperationException>(() => client.Bind((ClientOnly component) => new object()));
        }

        [Test]
        public void ServerCreatesAnIndependentImplementationForEachGhost() {
            var server = new ServerReplicateManager();
            Assert.Throws<InvalidOperationException>(() => server.Spawn(Inventory.Template));
            server.Bind((Inventory inventory) => new InventoryServer(inventory));
            var first = server.Spawn(Inventory.Template).GetComponent<Inventory>();
            var second = server.Spawn(Inventory.Template).GetComponent<Inventory>();
            first.Coins = 5;
            first.GetImplementation<InventoryServer>().BuyPotion();

            Assert.AreEqual(1, first.Potions);
            Assert.AreEqual(0, second.Potions);
            Assert.Throws<InvalidOperationException>(() => first.GetImplementation<InventoryClient>());
        }

        [Test]
        public void ClientPurchaseUpdatesOnlyTheOwnedInventoryOverLoopback() {
            var server = new Server(new TransportServer());
            var client = new Client(new TransportClient());
            InventoryServer.Configure(server);
            InventoryClient.Configure(client);
            Ghost owned = null;
            Ghost other = null;
            server.RemoteClientConnected += sender => {
                owned = server.ReplicateManager.Spawn(Inventory.Template, sender);
                other = server.ReplicateManager.Spawn(Inventory.Template);
                owned.GetComponent<Inventory>().Coins = other.GetComponent<Inventory>().Coins = 10;
            };
            ushort port;
            using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                port = (ushort)((IPEndPoint)socket.Client.LocalEndPoint).Port;

            try {
                server.Listen(port);
                client.Connect("127.0.0.1", port);
                Ghost local = null;
                Ghost unowned = null;
                PumpUntil(() => owned != null && client.ReplicateManager.TryGetGhost(owned.NetId, out local)
                    && client.ReplicateManager.TryGetGhost(other.NetId, out unowned));
                var inventory = local.GetComponent<Inventory>();
                Assert.AreEqual(10, inventory.Coins);
                unowned.GetComponent<Inventory>().GetImplementation<InventoryClient>().BuyPotion();
                inventory.GetImplementation<InventoryClient>().BuyPotion();
                PumpUntil(() => inventory.Potions == 1);
                Assert.AreEqual(5, inventory.Coins);
                Assert.AreEqual(0, other.GetComponent<Inventory>().Potions);
                Assert.AreEqual(ClientState.Connected, client.State);
            } finally {
                client.Disconnect();
                server.Shutdown();
            }

            void PumpUntil(Func<bool> complete) {
                var timeout = Stopwatch.StartNew();
                while (!complete()) {
                    if (timeout.ElapsedMilliseconds > 5000) Assert.Fail("Timed out waiting for loopback replication.");
                    server.TickIncoming();
                    client.TickIncoming();
                    server.TickOutgoing();
                    client.TickOutgoing();
                    Thread.Sleep(1);
                }
            }
        }
    }
}
