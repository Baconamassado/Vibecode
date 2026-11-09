package dev.lsp.paste;

import java.util.*;
import org.bukkit.*;
import org.bukkit.block.*;
import org.bukkit.block.data.BlockData;
import org.bukkit.block.data.Directional;
import org.bukkit.block.data.type.Stairs;
import org.bukkit.block.structure.Mirror;
import org.bukkit.block.structure.StructureRotation;
import org.bukkit.entity.Player;
import dev.lsp.LspLimits;
import dev.lsp.nbt.Nbt;
import dev.lsp.paper.PaperNbtBridge;
import dev.lsp.paper.PaperTicks;

/** Main-thread-only preflight and paste, advanced one bounded operation at a time. */
public final class PasteJob {
    public record ChunkKey(int x, int z) {}
    public record Bounds(int minX, int minY, int minZ, int maxX, int maxY, int maxZ) {
        boolean includes(Vec p) { return p.x() >= minX && p.x() <= maxX && p.y() >= minY && p.y() <= maxY && p.z() >= minZ && p.z() <= maxZ; }
    }
    private enum Phase { PALETTES, TILES, PREPARE_ENTITIES, BLOCKS, ENTITIES, TICKS, DONE }
    private final UUID owner;
    private final World world;
    private final PastePlan plan;
    private final PaperNbtBridge bridge;
    private final List<List<BlockData>> palettes = new ArrayList<>();
    private final List<List<BlockState>> defaults = new ArrayList<>();
    private final List<Map<Integer, BlockState>> tiles = new ArrayList<>();
    private final List<List<PasteEntity>> entities = new ArrayList<>();
    private final List<BitSet> applied = new ArrayList<>();
    private PaperTicks tickScheduler;
    private final List<Bounds> bounds = new ArrayList<>();
    private final Set<ChunkKey> chunks = new LinkedHashSet<>();
    private final long started = System.nanoTime();
    private Phase phase = Phase.PALETTES;
    private int regionIndex, itemIndex, placed, restored, spawned, scheduled;
    private Iterator<Map.Entry<Integer, Nbt>> tileIterator;
    private boolean mutated;

    public PasteJob(Player player, PastePlan plan, PaperNbtBridge bridge, LspLimits limits) {
        this.owner = player.getUniqueId(); this.world = player.getWorld(); this.plan = plan; this.bridge = bridge;
        if (plan.ignoreBlocks()) phase = Phase.PREPARE_ENTITIES;
        for (var region : plan.regions()) {
            palettes.add(new ArrayList<>()); defaults.add(new ArrayList<>()); tiles.add(new HashMap<>());
            entities.add(new ArrayList<>());
            applied.add(new BitSet());
            for (Nbt tag : region.entities()) {
                Location target = PasteEntity.destination(tag, region, world);
                if (plan.layers().includes(blockPosition(target))) {
                    validateEntityTarget(target);
                    reserveChunk(target.getBlockX() >> 4, target.getBlockZ() >> 4, limits);
                }
            }
            if (plan.ignoreBlocks()) continue;
            List<Vec> corners = region.corners();
            int minX = Integer.MAX_VALUE, minY = minX, minZ = minX;
            int maxX = Integer.MIN_VALUE, maxY = maxX, maxZ = maxX;
            for (Vec p : corners) {
                Nbt.require(p.y() >= world.getMinHeight() && p.y() < world.getMaxHeight(), "Region exceeds world height");
                Nbt.require(world.getWorldBorder().isInside(new Location(world, p.x() + .5, p.y(), p.z() + .5)), "Region outside world border");
                minX = Math.min(minX, p.x()); minY = Math.min(minY, p.y()); minZ = Math.min(minZ, p.z());
                maxX = Math.max(maxX, p.x()); maxY = Math.max(maxY, p.y()); maxZ = Math.max(maxZ, p.z());
            }
            bounds.add(new Bounds(minX, minY, minZ, maxX, maxY, maxZ));
            for (int x = minX >> 4; x <= (maxX >> 4); x++) for (int z = minZ >> 4; z <= (maxZ >> 4); z++) {
                reserveChunk(x, z, limits);
            }
        }
    }
    private void reserveChunk(int x, int z, LspLimits limits) {
        chunks.add(new ChunkKey(x, z));
        Nbt.require(chunks.size() <= limits.chunks(), "Paste exceeds chunk limit");
        Nbt.require(world.isChunkLoaded(x, z), "All destination chunks must already be loaded");
    }

    private static Vec blockPosition(Location target) {
        return new Vec(target.getBlockX(), target.getBlockY(), target.getBlockZ());
    }

    private void validateEntityTarget(Location target) {
        Nbt.require(target.getY() >= world.getMinHeight() && target.getY() < world.getMaxHeight()
            && world.getWorldBorder().isInside(target), "Entity destination outside world bounds");
    }
    public UUID owner() { return owner; }
    public World world() { return world; }
    public Set<ChunkKey> chunks() { return Collections.unmodifiableSet(chunks); }
    public boolean expired(long now, long timeout) { return now - started > timeout; }
    public boolean modifying() { return phase == Phase.BLOCKS || phase == Phase.ENTITIES || phase == Phase.TICKS; }
    public boolean done() { return phase == Phase.DONE; }
    public int placed() { return placed; }
    public boolean mutated() { return mutated; }
    public int restored() { return restored; }
    public int spawned() { return spawned; }
    public int scheduled() { return scheduled; }
    public String status() { return phase + ": " + placed + " blocos, " + restored + " NBT restaurados"; }
    public boolean contains(Location location) {
        if (!modifying() || location.getWorld() != world) return false;
        Vec p = new Vec(location.getBlockX(), location.getBlockY(), location.getBlockZ());
        return plan.layers().includes(p) && bounds.stream().anyMatch(b -> b.includes(p));
    }

    public void step() throws ReflectiveOperationException {
        if (phase == Phase.DONE) return;
        var region = plan.regions().get(regionIndex);
        switch (phase) {
            case PALETTES -> {
                if (itemIndex < region.palette().size()) {
                    BlockData data = Bukkit.createBlockData(region.palette().get(itemIndex++));
                    BlockData transformed = transform(data, region);
                    palettes.get(regionIndex).add(transformed);
                    defaults.get(regionIndex).add(transformed.createBlockState());
                } else advanceRegion(Phase.TILES);
            }
            case TILES -> {
                if (tileIterator == null) tileIterator = region.tiles().entrySet().iterator();
                if (tileIterator.hasNext()) {
                    var tile = tileIterator.next();
                    BlockData data = palettes.get(regionIndex).get(region.packed().get(tile.getKey()));
                    BlockState template = data.createBlockState();
                    Nbt.require(template instanceof TileState, "Block entity NBT attached to a non-block-entity block");
                    Vec p = region.position(tile.getKey());
                    Nbt tag = tile.getValue().with("x", Nbt.integer(p.x())).with("y", Nbt.integer(p.y())).with("z", Nbt.integer(p.z()));
                    tiles.get(regionIndex).put(tile.getKey(), bridge.prepare(template, tag));
                } else { tileIterator = null; advanceRegion(Phase.PREPARE_ENTITIES); }
            }
            case PREPARE_ENTITIES -> {
                if (tickScheduler == null && !region.ticks().isEmpty()) tickScheduler = new PaperTicks(world);
                if (itemIndex < region.entities().size()) {
                    Nbt tag = region.entities().get(itemIndex++);
                    Location target = PasteEntity.destination(tag, region, world);
                    if (plan.layers().includes(blockPosition(target))) {
                        entities.get(regionIndex).add(PasteEntity.prepare(tag, target, region));
                    }
                } else advanceRegion(Phase.BLOCKS);
            }
            case BLOCKS -> {
                if (!plan.ignoreBlocks() && itemIndex < region.volume()) place(region, itemIndex++);
                else advanceRegion(Phase.ENTITIES);
            }
            case ENTITIES -> {
                var prepared = entities.get(regionIndex);
                if (itemIndex < prepared.size()) {
                    PasteEntity entity = prepared.get(itemIndex++);
                    validateEntityTarget(entity.location());
                    entity.spawn();
                    mutated = true;
                    spawned++;
                } else advanceRegion(Phase.TICKS);
            }
            case TICKS -> {
                if (itemIndex < region.ticks().size()) {
                    PastePlan.Tick tick = region.ticks().get(itemIndex++);
                    Vec destination = region.position(tick.index());
                    if (applied.get(regionIndex).get(tick.index()) &&
                        tickScheduler.schedule(destination, tick, world.getGameTime())) scheduled++;
                } else advanceRegion(Phase.DONE);
            }
            default -> { }
        }
    }
    private void advanceRegion(Phase next) {
        itemIndex = 0;
        if (++regionIndex >= plan.regions().size()) { regionIndex = 0; phase = next; }
    }

    private void place(PastePlan.Region region, int index) {
        Vec p = region.position(index);
        if (!plan.layers().includes(p)) return;
        int paletteId = region.packed().get(index);
        BlockData data = palettes.get(regionIndex).get(paletteId);
        Material material = data.getMaterial();
        if (material == Material.STRUCTURE_VOID) return;
        if (plan.replace().equals("with_non_air") && material.isAir()) return;
        Block block = world.getBlockAt(p.x(), p.y(), p.z());
        if (plan.replace().equals("none") && !block.getType().isAir()) return;
        BlockState template = tiles.get(regionIndex).get(index);
        boolean equal = block.getBlockData().equals(data);
        // Equal block state never means equal inventory/NBT.
        if (equal && template == null && plan.changedOnly()) return;
        Nbt.require(world.getWorldBorder().isInside(block.getLocation().add(.5, .5, .5)), "World border changed during paste");
        BlockState old = block.getState();
        mutated = true;
        if (old instanceof TileState && (!equal || template == null)) {
            if (old instanceof Container container) {
                // Snapshot is block-local, including for one half of a double chest.
                container.getSnapshotInventory().clear();
                Nbt.require(old.update(true, false), "Cannot clear replaced container");
            }
            block.setType(Material.BARRIER, false);
        }
        block.setBlockData(data, false);
        // Includes same-state restores and a default reset for block entities without NBT.
        BlockState replacement = template != null ? template : defaults.get(regionIndex).get(paletteId);
        if (replacement instanceof TileState) {
            Nbt.require(replacement.copy(block.getLocation()).update(true, false), "Cannot restore block entity");
        }
        placed++;
        applied.get(regionIndex).set(index);
        if (template != null) restored++;
    }

    private static BlockData transform(BlockData original, PastePlan.Region r) {
        BlockData data = original.clone();
        Mirror[] mirrors = {Mirror.NONE, Mirror.LEFT_RIGHT, Mirror.FRONT_BACK};
        StructureRotation[] rotations = {StructureRotation.NONE, StructureRotation.CLOCKWISE_90,
            StructureRotation.CLOCKWISE_180, StructureRotation.COUNTERCLOCKWISE_90};
        data.mirror(mirrors[r.mainMirror()]); data.mirror(mirrors[r.blockMirrorSub()]);
        data.rotate(rotations[r.combinedRotation()]);
        boolean reflection = (r.mainMirror() != 0) ^ (r.subMirror() != 0);
        // Correct vanilla mirror special cases using the complete geometric transform.
        if (original instanceof Directional facing && data instanceof Directional result) {
            BlockFace old = facing.getFacing();
            Vec direction = new Vec(old.getModX(), old.getModY(), old.getModZ())
                .transform(r.mainMirror(), r.mainRotation()).transform(r.subMirror(), r.subRotation());
            for (BlockFace candidate : result.getFaces()) {
                if (candidate.getModX() == direction.x() && candidate.getModY() == direction.y() && candidate.getModZ() == direction.z()) {
                    result.setFacing(candidate); break;
                }
            }
        }
        if (original instanceof org.bukkit.block.data.type.Chest chest && data instanceof org.bukkit.block.data.type.Chest result) {
            var type = chest.getType();
            result.setType(!reflection || type == org.bukkit.block.data.type.Chest.Type.SINGLE ? type
                : type == org.bukkit.block.data.type.Chest.Type.LEFT ? org.bukkit.block.data.type.Chest.Type.RIGHT : org.bukkit.block.data.type.Chest.Type.LEFT);
        }
        if (original instanceof Stairs stairs && data instanceof Stairs result) {
            result.setShape(!reflection ? stairs.getShape() : switch (stairs.getShape()) {
                case INNER_LEFT -> Stairs.Shape.INNER_RIGHT;
                case INNER_RIGHT -> Stairs.Shape.INNER_LEFT;
                case OUTER_LEFT -> Stairs.Shape.OUTER_RIGHT;
                case OUTER_RIGHT -> Stairs.Shape.OUTER_LEFT;
                default -> Stairs.Shape.STRAIGHT;
            });
        }
        return data;
    }
}
