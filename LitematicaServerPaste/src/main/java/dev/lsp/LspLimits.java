package dev.lsp;

import org.bukkit.configuration.file.FileConfiguration;

public record LspLimits(int compressed, int decompressed, int nodes, int blocks,
                        int blockEntities, int palette, int chunks, int sessions,
                        long transferTimeout, long jobTimeout, int operations, long tickNanos,
                        boolean suppressEvents) {
    public static LspLimits read(FileConfiguration c) {
        return new LspLimits(limit(c, "max-compressed-bytes", 16 << 20, 1024, 16 << 20),
            limit(c, "max-decompressed-bytes", 64 << 20, 1024, 128 << 20),
            limit(c, "max-nbt-nodes", 1000000, 100, 2000000),
            limit(c, "max-blocks", 1000000, 1, 4000000),
            limit(c, "max-block-entities", 16384, 1, 32768),
            limit(c, "max-palette-entries", 16384, 1, 65536),
            limit(c, "max-chunks", 256, 1, 1024),
            limit(c, "max-sessions", 2, 1, 4),
            limit(c, "transfer-timeout-seconds", 30, 5, 120) * 1000000000L,
            limit(c, "job-timeout-seconds", 300, 10, 1800) * 1000000000L,
            limit(c, "operations-per-tick", 4096, 1, 65536),
            limit(c, "milliseconds-per-tick", 4, 1, 20) * 1000000L,
            c.getBoolean("suppress-events", true));
    }

    private static int limit(FileConfiguration c, String key, int fallback, int min, int max) {
        return Math.clamp(c.getInt(key, fallback), min, max);
    }
}
