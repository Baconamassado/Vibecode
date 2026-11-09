package dev.lsp.paper;

import java.lang.reflect.*;
import org.bukkit.World;
import dev.lsp.paste.PastePlan.Tick;
import dev.lsp.paste.Vec;

/** Paper 26.1.2 tick scheduler adapter, resolved before a paste changes the world. */
public final class PaperTicks {
    private final Object level, blocks, fluids;
    private final Constructor<?> position, scheduled;
    private final Method blockState, block, fluidState, fluid, key, priority, schedule, blockTicks, fluidTicks;

    public PaperTicks(World world) throws ReflectiveOperationException {
        level = world.getClass().getMethod("getHandle").invoke(world);
        Class<?> pos = Class.forName("net.minecraft.core.BlockPos");
        Class<?> tick = Class.forName("net.minecraft.world.ticks.ScheduledTick");
        Class<?> priorities = Class.forName("net.minecraft.world.ticks.TickPriority");
        Class<?> registries = Class.forName("net.minecraft.core.registries.BuiltInRegistries");
        Class<?> registry = Class.forName("net.minecraft.core.Registry");
        position = pos.getConstructor(int.class, int.class, int.class);
        scheduled = tick.getConstructor(Object.class, pos, long.class, priorities, long.class);
        priority = priorities.getMethod("byValue", int.class);
        blocks = registries.getField("BLOCK").get(null);
        fluids = registries.getField("FLUID").get(null);
        key = registry.getMethod("getKey", Object.class);
        blockState = level.getClass().getMethod("getBlockState", pos);
        block = blockState.getReturnType().getMethod("getBlock");
        fluidState = blockState.getReturnType().getMethod("getFluidState");
        fluid = fluidState.getReturnType().getMethod("getType");
        blockTicks = level.getClass().getMethod("getBlockTicks");
        fluidTicks = level.getClass().getMethod("getFluidTicks");
        schedule = Class.forName("net.minecraft.world.ticks.LevelTicks").getMethod("schedule", tick);
    }

    public boolean schedule(Vec destination, Tick tick, long gameTime) throws ReflectiveOperationException {
        Object pos = position.newInstance(destination.x(), destination.y(), destination.z());
        Object state = blockState.invoke(level, pos);
        Object type = tick.fluid() ? fluid.invoke(fluidState.invoke(state)) : block.invoke(state);
        Object id = key.invoke(tick.fluid() ? fluids : blocks, type);
        if (!tick.type().equals(String.valueOf(id))) return false;
        Object entry = scheduled.newInstance(type, pos, gameTime + tick.delay(),
            priority.invoke(null, tick.priority()), tick.order());
        schedule.invoke((tick.fluid() ? fluidTicks : blockTicks).invoke(level), entry);
        return true;
    }
}
