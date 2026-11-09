package dev.lsp.nbt;

import java.io.*;
import java.util.*;
import java.util.zip.GZIPInputStream;

/** Independent, bounded reader for the standard binary NBT format used on the wire. */
public record Nbt(int type, Object value) {
    public static Nbt compound(Map<String, Nbt> values) { return new Nbt(10, Map.copyOf(values)); }
    public static Nbt string(String value) { return new Nbt(8, value); }
    public static Nbt integer(int value) { return new Nbt(3, value); }
    public static Nbt byteTag(int value) { return new Nbt(1, (byte) value); }
    public static Nbt empty() { return compound(Map.of()); }

    @SuppressWarnings("unchecked")
    public Map<String, Nbt> entries() {
        require(type == 10, "Expected compound");
        return (Map<String, Nbt>) value;
    }
    @SuppressWarnings("unchecked")
    public List<Nbt> list() {
        require(type == 9, "Expected list");
        return (List<Nbt>) value;
    }
    public Nbt get(String key) {
        Nbt tag = entries().get(key);
        require(tag != null, "Missing tag: " + key);
        return tag;
    }
    public Nbt compoundOrEmpty(String key) { return entries().getOrDefault(key, empty()); }
    public List<Nbt> listOrEmpty(String key) {
        Nbt tag = entries().get(key);
        return tag == null ? List.of() : tag.list();
    }
    public String text() { require(type == 8, "Expected string"); return (String) value; }
    public String text(String key, String fallback) {
        Nbt tag = entries().get(key); return tag == null ? fallback : tag.text();
    }
    public int number() {
        require(type >= 1 && type <= 4, "Expected integral number");
        long n = ((Number) value).longValue();
        require(n >= Integer.MIN_VALUE && n <= Integer.MAX_VALUE, "Integer overflow");
        return (int) n;
    }
    public int number(String key, int fallback) {
        Nbt tag = entries().get(key); return tag == null ? fallback : tag.number();
    }
    public boolean flag(String key, boolean fallback) { return number(key, fallback ? 1 : 0) != 0; }
    public int[] ints() { require(type == 11, "Expected int array"); return (int[]) value; }
    public long[] longs() { require(type == 12, "Expected long array"); return (long[]) value; }
    public Nbt with(String key, Nbt tag) {
        Map<String, Nbt> copy = new LinkedHashMap<>(entries()); copy.put(key, tag); return compound(copy);
    }

    public static Nbt readNetwork(DataInputStream in, int bytes, int nodes) throws IOException {
        return readRoot(in, false, bytes, nodes);
    }
    public static Nbt readServux(byte[] packet, int maxBytes, int maxNodes) throws IOException {
        try (DataInputStream framed = new DataInputStream(new ByteArrayInputStream(packet))) {
            int length = framed.readInt();
            require(length > 0 && length == framed.available(), "Invalid Servux frame length");
            InputStream raw = new ByteArrayInputStream(packet, 4, length);
            boolean gzip = length >= 2 && (packet[4] & 255) == 31 && (packet[5] & 255) == 139;
            try (InputStream decoded = gzip ? new GZIPInputStream(raw) : raw;
                 DataInputStream in = new DataInputStream(new LimitedInput(decoded, maxBytes))) {
                Nbt root = readRoot(in, true, maxBytes, maxNodes);
                require(in.read() == -1, "Trailing NBT bytes");
                return root;
            }
        }
    }
    private static Nbt readRoot(DataInputStream in, boolean named, int bytes, int nodes) throws IOException {
        require(in.readUnsignedByte() == 10, "Root must be a compound");
        Budget budget = new Budget(bytes, nodes);
        if (named) readString(in, budget);
        return readPayload(in, 10, 0, budget);
    }
    private static Nbt readPayload(DataInputStream in, int type, int depth, Budget b) throws IOException {
        require(depth <= 64, "NBT nesting limit exceeded");
        b.node();
        return switch (type) {
            case 1 -> { b.use(1); yield byteTag(in.readByte()); }
            case 2 -> { b.use(2); yield new Nbt(2, in.readShort()); }
            case 3 -> { b.use(4); yield integer(in.readInt()); }
            case 4 -> { b.use(8); yield new Nbt(4, in.readLong()); }
            case 5 -> { b.use(4); float n = in.readFloat(); require(Float.isFinite(n), "Non-finite NBT number"); yield new Nbt(5, n); }
            case 6 -> { b.use(8); double n = in.readDouble(); require(Double.isFinite(n), "Non-finite NBT number"); yield new Nbt(6, n); }
            case 7 -> { int n = length(in, b, 1); byte[] a = new byte[n]; in.readFully(a); yield new Nbt(7, a); }
            case 8 -> string(readString(in, b));
            case 9 -> {
                b.use(5);
                int child = in.readUnsignedByte(), n = in.readInt();
                require(n >= 0 && n <= b.nodes && child >= 0 && child <= 12 && (child != 0 || n == 0), "Invalid list");
                List<Nbt> values = new ArrayList<>(Math.min(n, 1024));
                for (int i = 0; i < n; i++) values.add(readPayload(in, child, depth + 1, b));
                yield new Nbt(9, List.copyOf(values));
            }
            case 10 -> {
                Map<String, Nbt> values = new LinkedHashMap<>();
                for (int child; (child = in.readUnsignedByte()) != 0;) {
                    b.use(1);
                    String key = readString(in, b);
                    require(!values.containsKey(key), "Duplicate NBT key");
                    values.put(key, readPayload(in, child, depth + 1, b));
                }
                b.use(1); yield compound(values);
            }
            case 11 -> { int n = length(in, b, 4); int[] a = new int[n]; for (int i = 0; i < n; i++) a[i] = in.readInt(); yield new Nbt(11, a); }
            case 12 -> { int n = length(in, b, 8); long[] a = new long[n]; for (int i = 0; i < n; i++) a[i] = in.readLong(); yield new Nbt(12, a); }
            default -> throw new IllegalArgumentException("Unsupported NBT type " + type);
        };
    }
    private static int length(DataInputStream in, Budget b, int width) throws IOException {
        int n = in.readInt(); require(n >= 0, "Negative array length");
        b.use(4L + (long) n * width); return n;
    }
    private static String readString(DataInputStream in, Budget b) throws IOException {
        int length = in.readUnsignedShort(); b.use(2L + length);
        byte[] encoded = new byte[length + 2];
        encoded[0] = (byte) (length >>> 8); encoded[1] = (byte) length;
        in.readFully(encoded, 2, length);
        return new DataInputStream(new ByteArrayInputStream(encoded)).readUTF();
    }

    public byte[] networkBytes() throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(bytes)) { out.writeByte(type); writePayload(out); }
        return bytes.toByteArray();
    }
    private void writePayload(DataOutputStream out) throws IOException {
        switch (type) {
            case 1 -> out.writeByte(((Number) value).byteValue());
            case 2 -> out.writeShort(((Number) value).shortValue());
            case 3 -> out.writeInt(number());
            case 4 -> out.writeLong(((Number) value).longValue());
            case 5 -> out.writeFloat(((Number) value).floatValue());
            case 6 -> out.writeDouble(((Number) value).doubleValue());
            case 7 -> { byte[] a = (byte[]) value; out.writeInt(a.length); out.write(a); }
            case 8 -> out.writeUTF(text());
            case 9 -> { List<Nbt> a = list(); out.writeByte(a.isEmpty() ? 0 : a.getFirst().type); out.writeInt(a.size()); for (Nbt t : a) t.writePayload(out); }
            case 10 -> { for (var e : entries().entrySet()) { out.writeByte(e.getValue().type); out.writeUTF(e.getKey()); e.getValue().writePayload(out); } out.writeByte(0); }
            case 11 -> { int[] a = ints(); out.writeInt(a.length); for (int n : a) out.writeInt(n); }
            case 12 -> { long[] a = longs(); out.writeInt(a.length); for (long n : a) out.writeLong(n); }
            default -> throw new IOException("Unknown NBT type");
        }
    }

    /** Type-preserving SNBT for Paper's verified CraftNBTTagConfigSerializer bridge. */
    public String snbt() {
        StringBuilder out = new StringBuilder(); appendSnbt(out); return out.toString();
    }
    private void appendSnbt(StringBuilder out) {
        switch (type) {
            case 1 -> out.append(value).append('b');
            case 2 -> out.append(value).append('s');
            case 3 -> out.append(value);
            case 4 -> out.append(value).append('L');
            case 5 -> out.append(value).append('f');
            case 6 -> out.append(value).append('d');
            case 8 -> quote(out, text());
            case 10 -> {
                out.append('{'); boolean first = true;
                for (var e : entries().entrySet()) {
                    if (!first) out.append(','); first = false;
                    quote(out, e.getKey()); out.append(':'); e.getValue().appendSnbt(out);
                }
                out.append('}');
            }
            case 9 -> { out.append('['); boolean first = true; for (Nbt t : list()) { if (!first) out.append(','); first = false; t.appendSnbt(out); } out.append(']'); }
            case 7 -> { out.append("[B;"); byte[] a = (byte[]) value; for (int i = 0; i < a.length; i++) { if (i > 0) out.append(','); out.append(a[i]).append('b'); } out.append(']'); }
            case 11 -> { out.append("[I;"); int[] a = ints(); for (int i = 0; i < a.length; i++) { if (i > 0) out.append(','); out.append(a[i]); } out.append(']'); }
            case 12 -> { out.append("[L;"); long[] a = longs(); for (int i = 0; i < a.length; i++) { if (i > 0) out.append(','); out.append(a[i]).append('L'); } out.append(']'); }
            default -> throw new IllegalArgumentException("Unknown NBT type");
        }
    }
    private static void quote(StringBuilder out, String text) {
        out.append('"');
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '"', '\\' -> out.append('\\').append(c);
                case '\n' -> out.append("\\n");
                case '\r' -> out.append("\\r");
                case '\t' -> out.append("\\t");
                default -> { if (c < 32) out.append(String.format("\\u%04x", (int) c)); else out.append(c); }
            }
        }
        out.append('"');
    }
    public static void require(boolean condition, String message) {
        if (!condition) throw new IllegalArgumentException(message);
    }
    private static final class Budget {
        long bytes; int nodes;
        Budget(long bytes, int nodes) { this.bytes = bytes; this.nodes = nodes; }
        void use(long n) { require(n >= 0 && n <= bytes, "NBT size limit exceeded"); bytes -= n; }
        void node() {
            require(--nodes >= 0, "NBT node limit exceeded");
            if ((nodes & 1023) == 0) require(!Thread.currentThread().isInterrupted(), "NBT decoding cancelled");
            use(16);
        }
    }
    private static final class LimitedInput extends FilterInputStream {
        long left;
        LimitedInput(InputStream in, long limit) { super(in); left = limit; }
        @Override public int read() throws IOException {
            int n = in.read(); if (n >= 0 && --left < 0) throw new IOException("Decompression limit exceeded"); return n;
        }
        @Override public int read(byte[] b, int off, int len) throws IOException {
            int n = in.read(b, off, (int) Math.min(len, Math.max(1, left + 1)));
            if (n > 0 && (left -= n) < 0) throw new IOException("Decompression limit exceeded"); return n;
        }
    }
}
