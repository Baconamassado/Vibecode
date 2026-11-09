package dev.lsp.paste;

import java.util.*;
import dev.lsp.LspLimits;
import dev.lsp.nbt.Nbt;
import static dev.lsp.nbt.Nbt.require;

/** Pure decoding: no Bukkit calls, disk access, or world mutations. */
public record PastePlan(List<Region> regions, String replace, boolean changedOnly,
                        LayerFilter layers, int volume, boolean ignoreBlocks) {
    public static PastePlan decode(Nbt request, LspLimits limits, int dataVersion) {
        require(request.text("Task", "").equals("LitematicaPaste"), "Only LitematicaPaste is supported");
        boolean ignoreBlocks = request.flag("IgnoreBlocks", false);
        Vec origin = Vec.array(request.get("Origin"));
        checkCoordinate(origin);
        int mainMirror = transform(request, "Mirror", 2), mainRotation = transform(request, "Rotation", 3);
        String replace = request.text("ReplaceMode", "none").toLowerCase(Locale.ROOT);
        require(Set.of("none", "all", "with_non_air").contains(replace), "Unknown replace mode");
        String layerBehavior = request.text("PasteLayerBehavior", "all").toLowerCase(Locale.ROOT);
        require(Set.of("all", "rendered_only").contains(layerBehavior), "Unknown layer behavior");
        LayerFilter layers = layerBehavior.equals("all") ? LayerFilter.all()
            : LayerFilter.decode(request.get("RenderLayerRange"));
        Nbt schematic = request.get("Schematics");
        int version = schematic.get("Version").number();
        require(version >= 2 && version <= 7, "Unsupported schematic format version");
        require(schematic.get("MinecraftDataVersion").number() == dataVersion,
            "Schematic data version differs from server; convert/save it with Litematica for 26.1.2");
        Nbt placements = request.compoundOrEmpty("SubRegions");
        List<Region> regions = new ArrayList<>();
        int total = 0, totalTiles = 0, totalPalette = 0, totalEntities = 0, totalTicks = 0;
        for (var e : schematic.get("Regions").entries().entrySet()) {
            require(!Thread.currentThread().isInterrupted(), "Paste cancelled");
            require(regions.size() < 1024, "Too many regions");
            Nbt source = e.getValue();
            Nbt placement = placements.compoundOrEmpty(e.getKey());
            if (!placement.flag("Enabled", true)) continue;
            Vec size = Vec.compound(source.get("Size"));
            int sx = dimension(size.x(), limits.blocks()), sy = dimension(size.y(), limits.blocks()), sz = dimension(size.z(), limits.blocks());
            long volume = (long) sx * sy * sz;
            require(volume <= limits.blocks() - total, "Schematic exceeds block limit");
            total += (int) volume;
            Vec regionPosition = placement.entries().containsKey("Pos") ? Vec.array(placement.get("Pos"))
                : Vec.compound(source.get("Position"));
            checkCoordinate(regionPosition);
            Vec anchor = origin.add(regionPosition.transform(mainMirror, mainRotation));
            int subMirror = transform(placement, "Mirror", 2), subRotation = transform(placement, "Rotation", 3);
            Vec offset = new Vec(size.x() < 0 ? 1 - sx : 0, size.y() < 0 ? 1 - sy : 0, size.z() < 0 ? 1 - sz : 0);
            List<String> palette = new ArrayList<>();
            for (Nbt state : source.get("BlockStatePalette").list()) {
                require(++totalPalette <= limits.palette(), "Too many palette entries");
                palette.add(blockString(state));
            }
            PackedPalette packed = new PackedPalette(source.get("BlockStates").longs(), palette.size(), (int) volume);
            // Validate every index before any world operation; arrays may span long boundaries.
            for (int i = 0; i < volume; i++) {
                if ((i & 4095) == 0) require(!Thread.currentThread().isInterrupted(), "Paste cancelled");
                packed.get(i);
            }
            Map<Integer, Nbt> tiles = new HashMap<>();
            for (Nbt tile : source.listOrEmpty("TileEntities")) {
                require(++totalTiles <= limits.blockEntities(), "Too many block entities");
                Vec p = Vec.compound(tile);
                int index = index(p, sx, sy, sz);
                require(tiles.put(index, tile) == null, "Duplicate block entity position");
            }
            boolean ignoreEntities = request.flag("IgnoreEntities", false) || placement.flag("IgnoreEntities", false);
            List<Nbt> entities = ignoreEntities ? List.of() : source.listOrEmpty("Entities");
            for (Nbt entity : entities) {
                totalEntities += countEntities(entity, 0);
                require(totalEntities <= 4096, "Too many entities");
            }
            List<Tick> ticks = new ArrayList<>();
            if (!ignoreBlocks) {
                readTicks(source, false, sx, sy, sz, ticks);
                readTicks(source, true, sx, sy, sz, ticks);
            }
            totalTicks += ticks.size();
            require(totalTicks <= 65536, "Too many scheduled ticks");
            Region region = new Region(e.getKey(), anchor, offset, mainMirror, mainRotation, subMirror, subRotation,
                sx, sy, sz, (int) volume, List.copyOf(palette), packed, Map.copyOf(tiles), entities, List.copyOf(ticks));
            for (Vec corner : region.corners()) checkCoordinate(corner);
            regions.add(region);
        }
        require(!regions.isEmpty(), "No enabled regions");
        return new PastePlan(List.copyOf(regions), replace, request.flag("ChangedBlocksOnly", false), layers, total, ignoreBlocks);
    }

    private static int countEntities(Nbt entity, int depth) {
        require(depth <= 16, "Passenger nesting limit exceeded");
        int count = 1;
        for (Nbt passenger : entity.listOrEmpty("Passengers")) count += countEntities(passenger, depth + 1);
        return count;
    }

    private static void readTicks(Nbt source, boolean fluid, int sx, int sy, int sz, List<Tick> result) {
        for (Nbt tag : source.listOrEmpty(fluid ? "PendingFluidTicks" : "PendingBlockTicks")) {
            Vec pos = Vec.compound(tag);
            // Litematica can capture pending ticks on the inclusive outer edge of its scan box.
            if (pos.x() < 0 || pos.x() >= sx || pos.y() < 0 || pos.y() >= sy || pos.z() < 0 || pos.z() >= sz) continue;
            require(result.size() < 65536, "Too many scheduled ticks");
            String type = tag.get(fluid ? "Fluid" : "Block").text();
            int priority = tag.number("Priority", 0);
            require(priority >= -3 && priority <= 3, "Invalid tick priority");
            Nbt order = tag.entries().get("SubTick");
            require(order == null || order.type() >= 1 && order.type() <= 4, "Invalid tick order");
            result.add(new Tick(index(pos, sx, sy, sz), type, fluid,
                Math.max(0, tag.get("Time").number()), priority, order == null ? 0 : ((Number) order.value()).longValue()));
        }
    }

    public record Tick(int index, String type, boolean fluid, int delay, int priority, long order) {}

    private static int dimension(int n, int max) {
        long length = Math.abs((long) n); require(length > 0 && length <= max, "Invalid region dimensions"); return (int) length;
    }
    private static int transform(Nbt n, String key, int max) {
        int value = n.number(key, 0); require(value >= 0 && value <= max, "Invalid " + key); return value;
    }
    private static void checkCoordinate(Vec p) {
        require(Math.abs((long) p.x()) < 30000000 && Math.abs((long) p.z()) < 30000000
            && Math.abs((long) p.y()) < 30000000, "Coordinate outside supported range");
    }
    public static int index(Vec p, int sx, int sy, int sz) {
        require(p.x() >= 0 && p.x() < sx && p.y() >= 0 && p.y() < sy && p.z() >= 0 && p.z() < sz,
            "Block entity outside region");
        return (p.y() * sz + p.z()) * sx + p.x();
    }
    private static String blockString(Nbt state) {
        String name = state.get("Name").text();
        require(name.matches("[a-z0-9_.-]+:[a-z0-9_/.-]+"), "Invalid block identifier");
        StringJoiner props = new StringJoiner(",");
        for (var e : state.compoundOrEmpty("Properties").entries().entrySet()) {
            String value = e.getValue().text();
            require(e.getKey().matches("[a-z0-9_]+") && value.matches("[a-z0-9_-]+"), "Invalid block property");
            props.add(e.getKey() + "=" + value);
        }
        return name + (props.length() == 0 ? "" : "[" + props + "]");
    }

    public record Region(String name, Vec anchor, Vec offset, int mainMirror, int mainRotation,
                         int subMirror, int subRotation, int sx, int sy, int sz, int volume,
                         List<String> palette, PackedPalette packed, Map<Integer, Nbt> tiles,
                         List<Nbt> entities, List<Tick> ticks) {
        public Vec position(int index) {
            int x = index % sx, z = (index / sx) % sz, y = index / (sx * sz);
            return position(new Vec(x, y, z));
        }
        public Vec position(Vec p) {
            return p.add(offset).transform(mainMirror, mainRotation).transform(subMirror, subRotation).add(anchor);
        }
        public List<Vec> corners() {
            List<Vec> result = new ArrayList<>(8);
            for (int x : new int[]{0, sx - 1}) for (int y : new int[]{0, sy - 1}) for (int z : new int[]{0, sz - 1})
                result.add(position(new Vec(x, y, z)));
            return result;
        }
        public int blockMirrorSub() {
            return subMirror != 0 && (mainRotation == 1 || mainRotation == 3) ? 3 - subMirror : subMirror;
        }
        public int combinedRotation() { return (mainRotation + subRotation) & 3; }
    }

    public record LayerFilter(String axis, int min, int max) {
        public static LayerFilter all() { return new LayerFilter("y", Integer.MIN_VALUE, Integer.MAX_VALUE); }
        public static LayerFilter decode(Nbt n) {
            String axis = n.text("axis", "y").toLowerCase(Locale.ROOT);
            require(Set.of("x", "y", "z").contains(axis), "Invalid layer axis");
            String mode = n.text("mode", "all").toLowerCase(Locale.ROOT);
            int min = Integer.MIN_VALUE, max = Integer.MAX_VALUE;
            switch (mode) {
                case "all" -> { }
                case "single_layer" -> min = max = n.get("layer_single").number();
                case "all_above" -> min = n.get("layer_above").number();
                case "all_below" -> max = n.get("layer_below").number();
                case "layer_range" -> { min = n.get("layer_range_min").number(); max = n.get("layer_range_max").number(); }
                default -> throw new IllegalArgumentException("Unknown layer mode");
            }
            require(min <= max, "Invalid layer range"); return new LayerFilter(axis, min, max);
        }
        public boolean includes(Vec p) {
            int n = switch (axis) { case "x" -> p.x(); case "z" -> p.z(); default -> p.y(); };
            return n >= min && n <= max;
        }
    }
}
