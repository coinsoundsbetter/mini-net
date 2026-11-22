namespace MiniNet.Examples {
    public sealed class InventoryServer {
        private readonly Inventory inventory;

        public InventoryServer(Inventory inventory) => this.inventory = inventory;

        public static void Configure(Server server) {
            server.Bind((Inventory inventory) => new InventoryServer(inventory));
            server.ReplicateManager.RegisterTemplate(Inventory.Template);
            server.RegisterHandler<BuyPotion>((sender, request, mode) => {
                if (server.ReplicateManager.TryGetOwnedGhost(sender, request.InventoryNetId, out var ghost)
                    && ghost.TryGetComponent<Inventory>(out var inventory))
                    inventory.GetImplementation<InventoryServer>().BuyPotion();
            });
        }

        public void BuyPotion() {
            const int price = 5;
            if (inventory.Coins < price) return;
            inventory.Coins -= price;
            inventory.Potions++;
        }
    }
}
