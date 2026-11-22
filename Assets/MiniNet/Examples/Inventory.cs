using MiniNet.Replicate;

namespace MiniNet.Examples {
    public sealed class Inventory : GhostComponent, IRequiresServerImplementation, IRequiresClientImplementation {
        [GhostField] public int Coins;
        [GhostField] public int Potions;

        public static readonly GhostTemplate Template = new GhostBuilder(2).Component<Inventory>().Build();
    }

    public struct BuyPotion : INetworkMessage {
        public uint InventoryNetId;
    }
}
