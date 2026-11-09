package dev.lsp.paste;

import java.util.*;
import org.bukkit.*;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntitySnapshot;
import dev.lsp.nbt.Nbt;

record PasteEntity(EntitySnapshot snapshot, Location location, List<PasteEntity> passengers) {
    static Location destination(Nbt tag, PastePlan.Region region, World world) {
        List<Nbt> p = tag.get("Pos").list();
        Nbt.require(p.size() == 3, "Invalid entity position");
        double[] result = transform(number(p.get(0)) - .5, number(p.get(2)) - .5, region);
        return new Location(world, result[0] + .5 + region.anchor().x(), number(p.get(1)) + region.anchor().y(),
            result[1] + .5 + region.anchor().z());
    }

    static PasteEntity prepare(Nbt source, Location position, PastePlan.Region region) {
        Map<String, Nbt> fields = new HashMap<>(source.entries());
        fields.remove("UUID");
        fields.remove("UUIDMost");
        fields.remove("UUIDLeast");
        fields.remove("Passengers");
        Location target = position.clone();
        List<Nbt> rotation = source.listOrEmpty("Rotation");
        if (!rotation.isEmpty()) {
            Nbt.require(rotation.size() == 2, "Invalid entity rotation");
            double yaw = Math.toRadians(number(rotation.get(0)));
            double[] direction = transform(-Math.sin(yaw), Math.cos(yaw), region);
            target.setYaw((float) Math.toDegrees(Math.atan2(-direction[0], direction[1])));
            target.setPitch((float) number(rotation.get(1)));
        }
        fields.put("Pos", new Nbt(9, List.of(new Nbt(6, target.getX()), new Nbt(6, target.getY()), new Nbt(6, target.getZ()))));
        fields.put("Rotation", new Nbt(9, List.of(new Nbt(5, target.getYaw()), new Nbt(5, target.getPitch()))));
        String id = source.get("id").text();
        if (Set.of("minecraft:item_frame", "minecraft:glow_item_frame", "minecraft:painting", "minecraft:leash_knot").contains(id)) {
            int x = target.getBlockX(), y = target.getBlockY(), z = target.getBlockZ();
            fields.put("block_pos", new Nbt(11, new int[]{x, y, z}));
            fields.put("TileX", Nbt.integer(x));
            fields.put("TileY", Nbt.integer(y));
            fields.put("TileZ", Nbt.integer(z));
            for (String field : List.of("facing", "Facing")) {
                Nbt facing = fields.get(field);
                if (facing == null) continue;
                List<String> directions = List.of("down", "up", "north", "south", "west", "east");
                int index = facing.type() == 8 ? directions.indexOf(facing.text()) : facing.number();
                if (index < 2 || index > 5) continue;
                double[] d = transform(index == 4 ? -1 : index == 5 ? 1 : 0, index == 2 ? -1 : index == 3 ? 1 : 0, region);
                int transformed = d[0] < 0 ? 4 : d[0] > 0 ? 5 : d[1] < 0 ? 2 : 3;
                fields.put(field, facing.type() == 8 ? Nbt.string(directions.get(transformed)) : Nbt.byteTag(transformed));
            }
        }
        EntitySnapshot snapshot = Bukkit.getServer().getEntityFactory().createEntitySnapshot(Nbt.compound(fields).snbt());
        List<PasteEntity> passengers = source.listOrEmpty("Passengers").stream().map(p -> prepare(p, position, region)).toList();
        return new PasteEntity(snapshot, target, passengers);
    }

    void spawn() {
        List<Entity> spawned = new ArrayList<>();
        try {
            spawn(spawned);
        } catch (RuntimeException failure) {
            spawned.reversed().forEach(Entity::remove);
            throw failure;
        }
    }

    private Entity spawn(List<Entity> spawned) {
        Entity entity = snapshot.createEntity(location);
        spawned.add(entity);
        Nbt.require(entity.isValid(), "Entity spawn was rejected");
        for (PasteEntity passenger : passengers) {
            Nbt.require(entity.addPassenger(passenger.spawn(spawned)), "Passenger attachment was rejected");
        }
        return entity;
    }

    private static double number(Nbt n) {
        Nbt.require(n.value() instanceof Number, "Expected entity coordinate or angle");
        double value = ((Number) n.value()).doubleValue();
        Nbt.require(Double.isFinite(value) && Math.abs(value) <= 30_000_000, "Invalid entity coordinate or angle");
        return value;
    }

    private static double[] transform(double x, double z, PastePlan.Region region) {
        double[] main = transform(x, z, region.mainMirror(), region.mainRotation());
        return transform(main[0], main[1], region.subMirror(), region.subRotation());
    }

    private static double[] transform(double x, double z, int mirror, int rotation) {
        if (mirror == 1) z = -z;
        if (mirror == 2) x = -x;
        return switch (rotation) {
            case 1 -> new double[]{-z, x};
            case 2 -> new double[]{-x, -z};
            case 3 -> new double[]{z, -x};
            default -> new double[]{x, z};
        };
    }
}
