using MiniNet.Replicate;

namespace MiniNet.Examples {
    public class PlayerState : GhostComponent {
        [GhostField(Direction = ReplicateDirection.ServerToClient)]
        public int Hp;

    }

    public static class PlayerExample {
        public static readonly GhostTemplate Template = new GhostBuilder(1)
            .Component<PlayerState>()
            .Build();

        public static Ghost SpawnPlayer(Server server, int hp) {
            var player = server.ReplicateManager.Spawn(Template);
            player.GetComponent<PlayerState>().Hp = hp;
            return player;
        }
    }
}
