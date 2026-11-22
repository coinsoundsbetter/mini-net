using System;

namespace MiniNet.Examples {
    public sealed class InventoryClient {
        private readonly Inventory inventory;
        private readonly Action<BuyPotion> send;

        public InventoryClient(Inventory inventory, Action<BuyPotion> send) {
            this.inventory = inventory;
            this.send = send;
        }

        public static void Configure(Client client) {
            client.Bind((Inventory inventory) => new InventoryClient(inventory, request => client.Send(request)));
            client.ReplicateManager.RegisterTemplate(Inventory.Template);
        }

        public void BuyPotion() => send(new BuyPotion { InventoryNetId = inventory.Owner.NetId });
    }
}
