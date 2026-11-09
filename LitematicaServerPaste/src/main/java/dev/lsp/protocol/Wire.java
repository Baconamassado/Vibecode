package dev.lsp.protocol;

import java.io.*;
import dev.lsp.nbt.Nbt;

public final class Wire {
    private Wire() {}
    public static int varInt(DataInputStream in) throws IOException {
        int value = 0;
        for (int i = 0; i < 5; i++) {
            int part = in.readUnsignedByte();
            Nbt.require(i != 4 || (part & 0xf0) == 0, "VarInt overflow");
            value |= (part & 127) << (7 * i);
            if ((part & 128) == 0) return value;
        }
        throw new IOException("Invalid VarInt");
    }
    public static void varInt(DataOutputStream out, int value) throws IOException {
        do {
            int next = value & 127; value >>>= 7;
            out.writeByte(value == 0 ? next : next | 128);
        } while (value != 0);
    }
    public static byte[] metadata(int protocol) throws IOException {
        // The client checks this prefix before enabling its existing Servux paste path.
        // The suffix identifies LSP; this is protocol compatibility, not the Servux mod.
        Nbt metadata = Nbt.empty().with("version", Nbt.integer(protocol))
            .with("name", Nbt.string("Litematica Server Paste"))
            .with("id", Nbt.string("servux:litematics"))
            .with("servux", Nbt.string("servux-fabric-26.1.2-lsp-0.1.0"));
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(bytes)) {
            varInt(out, 1); out.write(metadata.networkBytes());
        }
        return bytes.toByteArray();
    }

    public static int protocol(Nbt metadata) {
        Nbt version = metadata.get("version");
        if (version.type() == 8) {
            String client = version.text();
            Nbt.require(client.contains("26.1.2") && client.contains("0.27.9"),
                "Legacy client not verified; expected Minecraft 26.1.2 / Litematica 0.27.9");
            return 1;
        }
        int protocol = version.number();
        Nbt.require(protocol == 1 || protocol == 2, "Unsupported Servux protocol");
        return protocol;
    }

    public static Nbt paste(byte[] bytes, int protocol, int maxBytes, int nodes) throws IOException {
        if (protocol == 2) return Nbt.readServux(bytes, maxBytes, nodes);
        Nbt.require(protocol == 1, "Unsupported Servux protocol");
        try (DataInputStream in = new DataInputStream(new ByteArrayInputStream(bytes))) {
            varInt(in); // Legacy transaction id (-1 is allowed).
            Nbt request = Nbt.readNetwork(in, maxBytes, nodes);
            Nbt.require(in.available() == 0, "Trailing legacy paste bytes");
            return request;
        }
    }

    /** One ordered PacketSplitter stream per player; only its first fragment has a length. */
    public static final class Transfer {
        private final byte[] buffer;
        private int offset;
        private long lastActivity;
        public Transfer(int length, int limit, long now) {
            Nbt.require(length > 4 && length <= limit, "Transfer exceeds compressed size limit");
            buffer = new byte[length]; lastActivity = now;
        }
        public boolean append(DataInputStream in, long now) throws IOException {
            int count = in.available();
            Nbt.require(count > 0 && count <= buffer.length - offset, "Invalid transfer fragment");
            in.readFully(buffer, offset, count); offset += count; lastActivity = now;
            return offset == buffer.length;
        }
        public byte[] bytes() {
            Nbt.require(offset == buffer.length, "Incomplete transfer"); return buffer;
        }
        public boolean expired(long now, long timeout) { return now - lastActivity > timeout; }
        public int received() { return offset; }
        public int total() { return buffer.length; }
    }
}
