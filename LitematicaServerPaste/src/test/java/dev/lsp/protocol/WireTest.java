package dev.lsp.protocol;

import dev.lsp.nbt.Nbt;
import java.io.*;
import java.util.zip.GZIPOutputStream;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class WireTest {
    @Test void legacyMetadataUsesVersionStringButRepliesWithIntegerProtocol() throws Exception {
        // Synthetic fixture encoded from the 0.27.9 wire contract, without using Nbt's writer.
        ByteArrayOutputStream raw = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(raw)) {
            out.writeByte(2); out.writeByte(10);
            out.writeByte(8); out.writeUTF("version"); out.writeUTF("litematica-fabric-26.1.2-0.27.9"); out.writeByte(0);
        }
        DataInputStream in = input(raw.toByteArray());
        assertEquals(2, Wire.varInt(in));
        assertEquals(1, Wire.protocol(Nbt.readNetwork(in, 4096, 20)));
        DataInputStream response = input(Wire.metadata(1));
        assertEquals(1, Wire.varInt(response));
        Nbt metadata = Nbt.readNetwork(response, 4096, 20);
        assertEquals(3, metadata.get("version").type());
        assertEquals(1, metadata.get("version").number());
        assertEquals(0, response.available());
    }
    @Test void legacyPasteHasSignedTransactionBeforeUnnamedNetworkNbt() throws Exception {
        // -1 transaction + TAG_Compound + TAG_String Task=LitematicaPaste + TAG_End.
        ByteArrayOutputStream raw = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(raw)) {
            out.write(new byte[]{-1, -1, -1, -1, 15, 10, 8});
            out.writeUTF("Task"); out.writeUTF("LitematicaPaste"); out.writeByte(0);
        }
        assertEquals("LitematicaPaste", Wire.paste(raw.toByteArray(), 1, 4096, 20).get("Task").text());
    }
    @Test void modernPasteHasBigEndianLengthAndGzipNamedRoot() throws Exception {
        ByteArrayOutputStream compressed = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(new GZIPOutputStream(compressed))) {
            out.writeByte(10); out.writeUTF("");
            out.writeByte(8); out.writeUTF("Task"); out.writeUTF("LitematicaPaste"); out.writeByte(0);
        }
        ByteArrayOutputStream frame = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(frame)) {
            out.writeInt(compressed.size()); out.write(compressed.toByteArray());
        }
        assertEquals("LitematicaPaste", Wire.paste(frame.toByteArray(), 2, 4096, 20).get("Task").text());
        assertThrows(IllegalArgumentException.class, () -> Wire.paste(frame.toByteArray(), 2, 20, 20));
    }
    @Test void varIntRejectsOverflowAndTruncation() {
        assertThrows(IllegalArgumentException.class, () -> Wire.varInt(input(new byte[]{-1, -1, -1, -1, 16})));
        assertThrows(EOFException.class, () -> Wire.varInt(input(new byte[]{-128})));
    }
    @Test void streamRejectsOverflowAndEmptyFragments() throws Exception {
        Wire.Transfer transfer = new Wire.Transfer(8, 16, 0);
        assertFalse(transfer.append(input(new byte[]{1, 2, 3}), 2));
        assertTrue(transfer.expired(8, 5));
        assertThrows(IllegalArgumentException.class, () -> transfer.append(input(new byte[6]), 8));
        assertThrows(IllegalArgumentException.class, () -> transfer.append(input(new byte[0]), 8));
        assertTrue(transfer.append(input(new byte[]{4, 5, 6, 7, 8}), 9));
        assertArrayEquals(new byte[]{1, 2, 3, 4, 5, 6, 7, 8}, transfer.bytes());
    }
    @Test void rejectsUnverifiedProtocolAndLegacyClient() {
        assertThrows(IllegalArgumentException.class, () -> Wire.protocol(Nbt.empty().with("version", Nbt.integer(3))));
        assertThrows(IllegalArgumentException.class, () -> Wire.protocol(Nbt.empty().with("version", Nbt.string("unknown"))));
    }
    private static DataInputStream input(byte[] bytes) { return new DataInputStream(new ByteArrayInputStream(bytes)); }
}
