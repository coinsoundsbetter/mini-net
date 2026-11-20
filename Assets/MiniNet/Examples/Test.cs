using MiniNet.Replicate;

namespace MiniNet.Examples {

    public class PlayerState : GhostComponent {
        [GhostField(Direction = ReplicateDirection.ServerToClient)] 
        public int Hp;
    }

    public class Te {

        public void CreatePlayer() {

            var ghost = new Ghost();
            
        }
    }
}
