package dev.lsp;

import org.bukkit.Location;
import org.bukkit.block.Block;
import org.bukkit.event.*;
import org.bukkit.event.block.*;
import org.bukkit.event.inventory.*;
import org.bukkit.inventory.DoubleChestInventory;
import org.bukkit.inventory.Inventory;

/** Local event suppression while a paste changes blocks. This is not a global tick freeze. */
public final class PasteEvents implements Listener {
    private final LspPlugin plugin;
    public PasteEvents(LspPlugin plugin) { this.plugin = plugin; }
    private boolean inside(Block block) { return plugin.protectedLocation(block.getLocation()); }
    private boolean inside(Inventory inventory) {
        if (inventory instanceof DoubleChestInventory chest) return inside(chest.getLeftSide()) || inside(chest.getRightSide());
        Location location = inventory.getLocation();
        return location != null && plugin.protectedLocation(location);
    }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void physics(BlockPhysicsEvent e) { if (inside(e.getBlock()) || inside(e.getSourceBlock())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST)
    public void redstone(BlockRedstoneEvent e) { if (inside(e.getBlock())) e.setNewCurrent(e.getOldCurrent()); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void flow(BlockFromToEvent e) { if (inside(e.getBlock()) || inside(e.getToBlock())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void dispense(BlockDispenseEvent e) { if (inside(e.getBlock())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void move(InventoryMoveItemEvent e) { if (inside(e.getSource()) || inside(e.getDestination())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void pickup(InventoryPickupItemEvent e) { if (inside(e.getInventory())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void open(InventoryOpenEvent e) { if (inside(e.getInventory())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void click(InventoryClickEvent e) { if (inside(e.getView().getTopInventory())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void drag(InventoryDragEvent e) { if (inside(e.getView().getTopInventory())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void breakBlock(BlockBreakEvent e) { if (inside(e.getBlock())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void placeBlock(BlockPlaceEvent e) { if (inside(e.getBlock())) e.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void extend(BlockPistonExtendEvent e) {
        if (inside(e.getBlock()) || inside(e.getBlock().getRelative(e.getDirection()))
            || e.getBlocks().stream().anyMatch(b -> inside(b) || inside(b.getRelative(e.getDirection())))) e.setCancelled(true);
    }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = true)
    public void retract(BlockPistonRetractEvent e) {
        if (inside(e.getBlock()) || inside(e.getBlock().getRelative(e.getDirection()))
            || e.getBlocks().stream().anyMatch(b -> inside(b) || inside(b.getRelative(e.getDirection().getOppositeFace())))) e.setCancelled(true);
    }
}
