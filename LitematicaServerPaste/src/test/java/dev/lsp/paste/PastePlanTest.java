package dev.lsp.paste;

import java.util.*;
import dev.lsp.LspLimits;
import dev.lsp.nbt.Nbt;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class PastePlanTest {
    private final LspLimits limits = new LspLimits(16777216, 67108864, 1000000, 10000, 100, 100, 16, 2,
        30000000000L, 300000000000L, 4096, 4000000L, true);

    @Test void decodesNegativeRegionSizeAndInventoryNbt() {
        Nbt tile = Nbt.empty().with("x", Nbt.integer(0)).with("y", Nbt.integer(0)).with("z", Nbt.integer(0))
            .with("Items", new Nbt(9, List.of(Nbt.empty().with("Slot", Nbt.byteTag(3)).with("count", Nbt.integer(42)))));
        Nbt region = region().with("TileEntities", new Nbt(9, List.of(tile)));
        PastePlan plan = PastePlan.decode(request(region), limits, 999);
        var r = plan.regions().getFirst();
        assertEquals(new Vec(8, 64, 10), r.position(0));
        assertEquals(new Vec(10, 64, 10), r.position(2));
        assertEquals(42, r.tiles().get(0).get("Items").list().getFirst().get("count").number());
    }
    @Test void rejectsForeignDataVersionAndInvalidBlockEntityPosition() {
        assertThrows(IllegalArgumentException.class, () -> PastePlan.decode(request(region()), limits, 1000));
        Nbt tile = Nbt.empty().with("x", Nbt.integer(3)).with("y", Nbt.integer(0)).with("z", Nbt.integer(0));
        assertThrows(IllegalArgumentException.class,
            () -> PastePlan.decode(request(region().with("TileEntities", new Nbt(9, List.of(tile)))), limits, 999));
    }
    @Test void preservesEntitiesUnlessTheSubregionIgnoresThem() {
        Nbt r = region().with("Entities", new Nbt(9, List.of(Nbt.empty().with("id", Nbt.string("minecraft:pig")))));
        assertEquals("minecraft:pig", PastePlan.decode(request(r), limits, 999).regions().getFirst().entities().getFirst().get("id").text());
        Nbt sub = Nbt.empty().with("IgnoreEntities", Nbt.byteTag(1));
        Nbt req = request(r).with("SubRegions", Nbt.empty().with("r", sub));
        assertTrue(PastePlan.decode(req, limits, 999).regions().getFirst().entities().isEmpty());
    }
    @Test void retainsFluidTickTimingAndSkipsTicksOutsideTheRegion() {
        Nbt tick = Nbt.empty().with("x", Nbt.integer(1)).with("y", Nbt.integer(0)).with("z", Nbt.integer(0))
            .with("Fluid", Nbt.string("minecraft:water")).with("Time", Nbt.integer(12))
            .with("Priority", Nbt.integer(-2)).with("SubTick", new Nbt(4, 5000000000L));
        Nbt r = region().with("PendingFluidTicks", new Nbt(9, List.of(tick, tick.with("x", Nbt.integer(3)))));
        var ticks = PastePlan.decode(request(r), limits, 999).regions().getFirst().ticks();
        assertEquals(List.of(new PastePlan.Tick(1, "minecraft:water", true, 12, -2, 5000000000L)), ticks);
    }
    @Test void filtersRenderedLayersUsingWorldCoordinates() {
        var filter = PastePlan.LayerFilter.decode(Nbt.empty().with("axis", Nbt.string("x"))
            .with("mode", Nbt.string("layer_range")).with("layer_range_min", Nbt.integer(8)).with("layer_range_max", Nbt.integer(9)));
        assertTrue(filter.includes(new Vec(8, 64, 10)));
        assertFalse(filter.includes(new Vec(10, 64, 10)));
    }
    private Nbt request(Nbt region) {
        Nbt schematic = Nbt.empty().with("Version", Nbt.integer(7)).with("MinecraftDataVersion", Nbt.integer(999))
            .with("Regions", Nbt.empty().with("r", region));
        return Nbt.empty().with("Task", Nbt.string("LitematicaPaste")).with("Origin", new Nbt(11, new int[]{10, 64, 10}))
            .with("Schematics", schematic).with("ReplaceMode", Nbt.string("all"));
    }
    private Nbt region() {
        Nbt size = Nbt.empty().with("x", Nbt.integer(-3)).with("y", Nbt.integer(1)).with("z", Nbt.integer(1));
        Nbt pos = Nbt.empty().with("x", Nbt.integer(0)).with("y", Nbt.integer(0)).with("z", Nbt.integer(0));
        return Nbt.empty().with("Size", size).with("Position", pos)
            .with("BlockStatePalette", new Nbt(9, List.of(Nbt.empty().with("Name", Nbt.string("minecraft:chest")))))
            .with("BlockStates", new Nbt(12, new long[]{0}));
    }
}
