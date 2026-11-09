package dev.lsp.paste;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class PackedPaletteTest {
    @Test void readsEntriesStraddlingLongBoundaries() {
        // With 3 bits/entry, index 21 starts at bit 63 and continues in the next word.
        long[] words = {1L << 63, 3L};
        PackedPalette palette = new PackedPalette(words, 8, 22);
        assertEquals(7, palette.get(21));
        assertEquals(0, palette.get(20));
    }
    @Test void enforcesMinimumOfTwoBitsEvenForSingleState() {
        PackedPalette palette = new PackedPalette(new long[]{0}, 1, 32);
        assertEquals(0, palette.get(31));
        assertThrows(IllegalArgumentException.class, () -> new PackedPalette(new long[0], 1, 1));
    }
    @Test void rejectsBadPaletteIdAndMalformedStorage() {
        PackedPalette palette = new PackedPalette(new long[]{3}, 3, 1);
        assertThrows(IllegalArgumentException.class, () -> palette.get(0));
        assertThrows(IllegalArgumentException.class, () -> new PackedPalette(new long[]{0, 0}, 1, 1));
    }
    @Test void appliesMainThenSubregionTransformInOrder() {
        Vec p = new Vec(2, 3, 5);
        assertEquals(new Vec(5, 3, 2), p.transform(1, 1));
        assertEquals(new Vec(-5, 3, 2), p.transform(1, 1).transform(2, 0));
        assertEquals(p, p.transform(0, 1).transform(0, 3));
        assertThrows(ArithmeticException.class, () -> new Vec(Integer.MIN_VALUE, 0, 0).transform(2, 0));
    }
}
