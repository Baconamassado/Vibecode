package dev.lsp.nbt;

import java.io.*;
import java.util.*;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class NbtTest {
    @Test void preservesInventoryTypesAndUnicode() throws Exception {
        Nbt item = Nbt.empty().with("Slot", Nbt.byteTag(17)).with("id", Nbt.string("minecraft:diamond"))
            .with("count", Nbt.integer(64)).with("components", Nbt.empty().with("minecraft:custom_name", Nbt.string("Baú \"A\" \\ itens\n中文")));
        Nbt original = Nbt.empty().with("Items", new Nbt(9, List.of(item)))
            .with("long", new Nbt(4, Long.MIN_VALUE)).with("ints", new Nbt(11, new int[]{-1, 0, 1}));
        Nbt decoded = Nbt.readNetwork(input(original.networkBytes()), 65536, 100);
        Nbt decodedItem = decoded.get("Items").list().getFirst();
        assertEquals(1, decodedItem.get("Slot").type());
        assertEquals(17, decodedItem.get("Slot").number());
        assertEquals(64, decodedItem.get("count").number());
        assertEquals(item.get("components").get("minecraft:custom_name").text(),
            decodedItem.get("components").get("minecraft:custom_name").text());
        assertEquals(Long.MIN_VALUE, decoded.get("long").value());
        assertArrayEquals(new int[]{-1, 0, 1}, decoded.get("ints").ints());
    }
    @Test void rejectsDuplicateKeysBeforeOverwriting() throws Exception {
        ByteArrayOutputStream raw = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(raw)) {
            out.writeByte(10);
            for (int n : new int[]{1, 2}) { out.writeByte(3); out.writeUTF("x"); out.writeInt(n); }
            out.writeByte(0);
        }
        assertThrows(IllegalArgumentException.class, () -> Nbt.readNetwork(input(raw.toByteArray()), 4096, 10));
    }
    @Test void rejectsLargeArraysBeforeAllocating() throws Exception {
        ByteArrayOutputStream raw = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(raw)) {
            out.writeByte(10); out.writeByte(12); out.writeUTF("a"); out.writeInt(Integer.MAX_VALUE);
        }
        assertThrows(IllegalArgumentException.class, () -> Nbt.readNetwork(input(raw.toByteArray()), 4096, 10));
    }
    @Test void rejectsComplexityAndNodeExhaustion() throws Exception {
        byte[] nested = new byte[3 * 66 + 1];
        nested[0] = 10;
        for (int i = 0; i < 65; i++) nested[1 + i * 3] = 10; // unnamed child compounds
        assertThrows(IllegalArgumentException.class, () -> Nbt.readNetwork(input(nested), 65536, 1000));
        byte[] normal = Nbt.empty().with("x", Nbt.integer(1)).networkBytes();
        assertThrows(IllegalArgumentException.class, () -> Nbt.readNetwork(input(normal), 4096, 1));
    }
    @Test void snbtPreservesStringEscapesAndNumericSuffixes() {
        assertEquals("\"a\\\"\\\\\\n\"", Nbt.string("a\"\\\n").snbt());
        assertEquals("17b", Nbt.byteTag(17).snbt());
        assertEquals("-9223372036854775808L", new Nbt(4, Long.MIN_VALUE).snbt());
        assertEquals("[B;-1b,0b,1b]", new Nbt(7, new byte[]{-1, 0, 1}).snbt());
    }
    private static DataInputStream input(byte[] bytes) { return new DataInputStream(new ByteArrayInputStream(bytes)); }
}
