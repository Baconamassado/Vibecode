package dev.lsp.paste;

import dev.lsp.nbt.Nbt;

public record Vec(int x, int y, int z) {
    public static Vec array(Nbt tag) {
        int[] a = tag.ints(); Nbt.require(a.length == 3, "Position must have 3 components");
        return new Vec(a[0], a[1], a[2]);
    }
    public static Vec compound(Nbt tag) {
        return new Vec(tag.get("x").number(), tag.get("y").number(), tag.get("z").number());
    }
    public Vec add(Vec other) {
        return new Vec(Math.addExact(x, other.x), Math.addExact(y, other.y), Math.addExact(z, other.z));
    }
    public Vec transform(int mirror, int rotation) {
        Nbt.require(mirror >= 0 && mirror <= 2 && rotation >= 0 && rotation <= 3, "Invalid transform");
        int a = mirror == 2 ? Math.negateExact(x) : x;
        int b = mirror == 1 ? Math.negateExact(z) : z;
        return switch (rotation) {
            case 1 -> new Vec(Math.negateExact(b), y, a);
            case 2 -> new Vec(Math.negateExact(a), y, Math.negateExact(b));
            case 3 -> new Vec(b, y, Math.negateExact(a));
            default -> new Vec(a, y, b);
        };
    }
}
