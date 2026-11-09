package dev.lsp.paper;

import java.lang.reflect.*;
import org.bukkit.block.BlockState;
import dev.lsp.nbt.Nbt;

/** The only dependency on CraftBukkit internals. Signatures checked against Paper 26.1.2 source. */
public final class PaperNbtBridge {
    private final Class<?> tileState;
    private final Method deserialize, loadData;

    public PaperNbtBridge() throws ReflectiveOperationException {
        tileState = Class.forName("org.bukkit.craftbukkit.block.CraftBlockEntityState");
        Class<?> compound = Class.forName("net.minecraft.nbt.CompoundTag");
        Class<?> serializer = Class.forName("org.bukkit.craftbukkit.util.CraftNBTTagConfigSerializer");
        deserialize = serializer.getMethod("deserialize", Object.class);
        loadData = tileState.getMethod("loadData", compound);
        // Fail on startup, rather than accepting a paste with an unusable bridge.
        deserialize.invoke(null, "{}");
    }

    public Object prepare(Nbt nbt) throws ReflectiveOperationException {
        return deserialize.invoke(null, nbt.snbt());
    }
    public void load(BlockState state, Object nbt) throws ReflectiveOperationException {
        Nbt.require(tileState.isInstance(state), "NBT targets a block without block entity");
        loadData.invoke(state, nbt);
    }
    public BlockState prepare(BlockState template, Nbt tag) throws ReflectiveOperationException {
        Object data = prepare(tag);
        load(template, data);
        return template;
    }
}
