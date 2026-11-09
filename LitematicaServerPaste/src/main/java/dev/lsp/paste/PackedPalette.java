package dev.lsp.paste;

import dev.lsp.nbt.Nbt;

/** Litematica packs continuously across long boundaries (unlike padded chunk palettes). */
public final class PackedPalette {
    private final long[] words;
    private final int bits, paletteSize, size;
    private final long mask;
    public PackedPalette(long[] words, int paletteSize, int size) {
        Nbt.require(paletteSize > 0 && size > 0, "Empty block palette");
        this.bits = Math.max(2, 32 - Integer.numberOfLeadingZeros(paletteSize - 1));
        this.mask = (1L << bits) - 1;
        this.words = words; this.paletteSize = paletteSize; this.size = size;
        long expected = ((long) size * bits + 63) / 64;
        Nbt.require(words.length == expected, "BlockStates length does not match region volume");
    }
    public int get(int index) {
        Nbt.require(index >= 0 && index < size, "Block index outside region");
        long bit = (long) index * bits;
        int word = (int) (bit >>> 6), shift = (int) (bit & 63);
        long n = words[word] >>> shift;
        if (shift + bits > 64) n |= words[word + 1] << (64 - shift);
        int id = (int) (n & mask);
        Nbt.require(id < paletteSize, "Palette index outside palette");
        return id;
    }
}
