package dev.lsp;

import java.io.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.logging.Level;
import org.bukkit.*;
import org.bukkit.command.*;
import org.bukkit.entity.Player;
import org.bukkit.event.*;
import org.bukkit.event.player.*;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.plugin.messaging.PluginMessageListener;
import dev.lsp.nbt.Nbt;
import dev.lsp.paper.PaperNbtBridge;
import dev.lsp.paste.*;
import dev.lsp.protocol.Wire;

public final class LspPlugin extends JavaPlugin implements PluginMessageListener, Listener {
    public static final String CHANNEL = "servux:litematics";
    private final Map<UUID, Integer> protocols = new HashMap<>();
    private final Map<UUID, Session> sessions = new LinkedHashMap<>();
    private final Map<UUID, Long> lastNotice = new HashMap<>();
    private final Queue<Runnable> completions = new ConcurrentLinkedQueue<>();
    private ExecutorService decoder;
    private PaperNbtBridge bridge;
    private LspLimits limits;
    private int dataVersion;
    private long turns;

    private static final class Session {
        final UUID owner, world;
        final int protocol;
        final long started = System.nanoTime();
        final List<PasteJob.ChunkKey> tickets = new ArrayList<>();
        Wire.Transfer transfer;
        Future<?> decoding;
        PasteJob job;
        Session(Player p, int protocol) { owner = p.getUniqueId(); world = p.getWorld().getUID(); this.protocol = protocol; }
    }

    @Override public void onEnable() {
        saveDefaultConfig(); limits = LspLimits.read(getConfig());
        if (!Bukkit.getMinecraftVersion().equals("26.1.2")) {
            getLogger().severe("LSP 0.1 requer Paper para Minecraft 26.1.2.");
            getServer().getPluginManager().disablePlugin(this); return;
        }
        try {
            bridge = new PaperNbtBridge();
            dataVersion = Bukkit.getUnsafe().getDataVersion();
        } catch (ReflectiveOperationException | LinkageError error) {
            getLogger().severe("Ponte NBT incompativel com este build do Paper: " + error.getClass().getSimpleName());
            getServer().getPluginManager().disablePlugin(this); return;
        }
        decoder = new ThreadPoolExecutor(1, 1, 0, TimeUnit.SECONDS,
            new ArrayBlockingQueue<>(limits.sessions()), task -> {
                Thread thread = new Thread(task, "LSP-NBT-Decoder"); thread.setDaemon(true); return thread;
            }, new ThreadPoolExecutor.AbortPolicy());
        getServer().getMessenger().registerOutgoingPluginChannel(this, CHANNEL);
        getServer().getMessenger().registerIncomingPluginChannel(this, CHANNEL, this);
        getServer().getPluginManager().registerEvents(this, this);
        getServer().getPluginManager().registerEvents(new PasteEvents(this), this);
        getServer().getScheduler().runTaskTimer(this, this::tick, 1, 1);
        getLogger().info("LSP ativo: Paper 26.1.2, Servux wire 1 (Litematica 0.27.9) e wire 2.");
    }
    @Override public void onDisable() {
        getServer().getScheduler().cancelTasks(this);
        for (Session s : List.copyOf(sessions.values())) release(s);
        protocols.clear(); lastNotice.clear();
        if (decoder != null) decoder.shutdownNow();
        completions.clear();
        getServer().getMessenger().unregisterIncomingPluginChannel(this);
        getServer().getMessenger().unregisterOutgoingPluginChannel(this);
    }

    public boolean authorized(Player p) {
        return p.isOnline() && p.hasPermission("lsp.paste") && p.getGameMode() == GameMode.CREATIVE;
    }
    public boolean protectedLocation(Location location) {
        return limits != null && limits.suppressEvents() && sessions.values().stream()
            .anyMatch(s -> s.job != null && s.job.contains(location));
    }

    @Override public void onPluginMessageReceived(String channel, Player player, byte[] message) {
        if (!CHANNEL.equals(channel) || !isEnabled()) return;
        if (!authorized(player)) { notice(player, "Requer modo criativo e permissao lsp.paste."); return; }
        try (DataInputStream in = new DataInputStream(new ByteArrayInputStream(message))) {
            Nbt.require(message.length <= 32768, "Plugin packet too large");
            int type = Wire.varInt(in);
            switch (type) {
                case 2 -> handshake(player, in);
                case 13 -> fragment(player, in);
                case 8 -> {
                    // v2 unregisters entity-data sync when disabled; do not leave a paste alive.
                    cancel(player.getUniqueId(), "Sincronizacao do cliente desativada.");
                    protocols.remove(player.getUniqueId());
                }
                case 3, 4, 7 -> { /* NBT overlays/schematic saving are outside this plugin's paste service. */ }
                case 14, 17 -> notice(player, "Fill/Delete e tarefas remotas nao sao implementados pelo LSP. Use /lsp cancel para cancelar.");
                default -> notice(player, "Tipo de pacote nao suportado pelo LSP.");
            }
        } catch (IOException | RuntimeException error) {
            Session s = sessions.get(player.getUniqueId());
            if (s != null && s.job == null) release(s);
            report(player, error, "recebimento");
        }
    }

    private void handshake(Player player, DataInputStream in) throws IOException {
        Nbt.require(in.available() <= 8192, "Metadata too large");
        Nbt metadata = Nbt.readNetwork(in, 16384, 128);
        Nbt.require(in.available() == 0, "Trailing metadata bytes");
        // 0.27.9 sends a mod-version STRING rather than a protocol integer.
        int protocol = Wire.protocol(metadata);
        Session active = sessions.get(player.getUniqueId());
        Nbt.require(active == null || active.protocol == protocol, "Cannot change protocol during paste");
        protocols.put(player.getUniqueId(), protocol);
        player.sendPluginMessage(this, CHANNEL, Wire.metadata(protocol));
    }

    private void fragment(Player player, DataInputStream in) throws IOException {
        UUID id = player.getUniqueId();
        Integer protocol = protocols.get(id);
        Nbt.require(protocol != null, "Enable entityDataSync and pasteUsingServux before pasting");
        Session s = sessions.get(id);
        if (s == null) {
            Nbt.require(sessions.size() < limits.sessions(), "Server paste session limit reached");
            int total = Wire.varInt(in);
            s = new Session(player, protocol);
            s.transfer = new Wire.Transfer(total, limits.compressed(), System.nanoTime());
            sessions.put(id, s);
        }
        Nbt.require(s.transfer != null, "A paste is already being processed; wait or use /lsp cancel");
        Nbt.require(s.world.equals(player.getWorld().getUID()), "World changed during transfer");
        if (s.transfer.append(in, System.nanoTime())) {
            byte[] bytes = s.transfer.bytes(); s.transfer = null;
            Session current = s;
            player.sendMessage("[LSP] Construcao recebida. Validando blocos e NBT...");
            s.decoding = decoder.submit(() -> {
                try {
                    Nbt request = Wire.paste(bytes, current.protocol, limits.decompressed(), limits.nodes());
                    PastePlan plan = PastePlan.decode(request, limits, dataVersion);
                    if (!Thread.currentThread().isInterrupted()) completions.add(() -> start(current, plan));
                } catch (IOException | RuntimeException error) {
                    if (!Thread.currentThread().isInterrupted()) completions.add(() -> {
                        if (sessions.get(current.owner) == current) {
                            release(current); report(Bukkit.getPlayer(current.owner), error, "decodificacao");
                        }
                    });
                }
            });
        }
    }

    private void start(Session s, PastePlan plan) {
        if (sessions.get(s.owner) != s) return;
        Player player = Bukkit.getPlayer(s.owner);
        if (player == null || !authorized(player) || !s.world.equals(player.getWorld().getUID())) {
            cancel(s.owner, "Jogador indisponivel, sem permissao ou em outro mundo."); return;
        }
        try {
            PasteJob job = new PasteJob(player, plan, bridge, limits);
            for (Session other : sessions.values()) {
                if (other != s && other.job != null && other.world.equals(s.world)) {
                    Nbt.require(Collections.disjoint(job.chunks(), other.job.chunks()), "Another paste is using these chunks");
                }
            }
            s.job = job;
            for (var key : job.chunks()) {
                Nbt.require(job.world().addPluginChunkTicket(key.x(), key.z(), this), "Cannot reserve destination chunk");
                s.tickets.add(key);
            }
        } catch (RuntimeException error) {
            release(s); report(player, error, "preparacao");
        }
    }

    private void tick() {
        for (int i = 0; i < limits.sessions(); i++) {
            Runnable completion = completions.poll(); if (completion == null) break; completion.run();
        }
        long now = System.nanoTime();
        List<Session> ready = new ArrayList<>();
        for (Session s : List.copyOf(sessions.values())) {
            Player player = Bukkit.getPlayer(s.owner);
            if (player == null || !authorized(player) || !s.world.equals(player.getWorld().getUID())) {
                cancel(s.owner, "Jogador indisponivel, sem permissao ou em outro mundo.");
            } else if ((s.transfer != null && s.transfer.expired(now, limits.transferTimeout())) || now - s.started > limits.jobTimeout()) {
                cancel(s.owner, "Tempo limite da operacao excedido.");
            } else if (s.job != null) ready.add(s);
        }
        if (ready.isEmpty()) return;
        long deadline = System.nanoTime() + limits.tickNanos();
        for (int op = 0; op < limits.operations() && (op == 0 || System.nanoTime() < deadline); op++) {
            Session s = ready.get((int) (turns++ % ready.size()));
            if (sessions.get(s.owner) != s) continue;
            try {
                s.job.step();
                if (s.job.done()) {
                    Player player = Bukkit.getPlayer(s.owner);
                    if (player != null) {
                        player.sendMessage("[LSP] Colagem concluida: " + s.job.placed() + " blocos, " + s.job.restored() + " NBT restaurados.");
                        player.sendMessage("[LSP] Grupos de entidades colados: " + s.job.spawned() + ". Ticks submetidos ao servidor: " + s.job.scheduled() + ".");
                    }
                    release(s);
                }
            } catch (ReflectiveOperationException | RuntimeException error) {
                Player player = Bukkit.getPlayer(s.owner);
                int placed = s.job.placed(); boolean mutated = s.job.mutated(); release(s);
                if (player != null && mutated) player.sendMessage("[LSP] Falha durante a colagem (" + placed + " blocos concluidos). Alteracoes parciais permanecem no mundo.");
                report(player, error, "colagem");
            }
        }
    }

    private void release(Session s) {
        sessions.remove(s.owner, s);
        if (s.decoding != null) s.decoding.cancel(true);
        World world = Bukkit.getWorld(s.world);
        if (world != null) for (var key : s.tickets) world.removePluginChunkTicket(key.x(), key.z(), this);
        s.tickets.clear(); s.transfer = null;
    }
    private void cancel(UUID owner, String reason) {
        Session s = sessions.get(owner);
        if (s == null) return;
        Player player = Bukkit.getPlayer(owner);
        int placed = s.job == null ? 0 : s.job.placed();
        boolean mutated = s.job != null && s.job.mutated(); release(s);
        if (player != null) player.sendMessage("[LSP] " + reason + (mutated ? " Alteracoes ja aplicadas permanecem no mundo (" + placed + " blocos concluidos)." : ""));
    }
    private void report(Player player, Throwable error, String stage) {
        String reason = error instanceof IllegalArgumentException ? error.getMessage() : error.getClass().getSimpleName();
        if (reason == null) reason = "Erro de dados";
        reason = reason.replaceAll("[\\r\\n\\p{Cntrl}]", " ");
        if (reason.length() > 180) reason = reason.substring(0, 180);
        getLogger().log(Level.WARNING, "LSP: falha em " + stage + ": " + reason);
        if (player != null) notice(player, "Falha em " + stage + ": " + reason);
    }
    private void notice(Player player, String text) {
        long now = System.nanoTime();
        long previous = lastNotice.getOrDefault(player.getUniqueId(), Long.MIN_VALUE / 2);
        if (now - previous > 2000000000L) {
            lastNotice.put(player.getUniqueId(), now); player.sendMessage("[LSP] " + text);
        }
    }

    @EventHandler public void onQuit(PlayerQuitEvent e) { cancel(e.getPlayer().getUniqueId(), "Desconectado."); protocols.remove(e.getPlayer().getUniqueId()); lastNotice.remove(e.getPlayer().getUniqueId()); }
    @EventHandler public void onWorld(PlayerChangedWorldEvent e) { cancel(e.getPlayer().getUniqueId(), "Mundo alterado."); }
    @EventHandler public void onMode(PlayerGameModeChangeEvent e) { if (!e.isCancelled() && e.getNewGameMode() != GameMode.CREATIVE) cancel(e.getPlayer().getUniqueId(), "Modo criativo desativado."); }

    @Override public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (!(sender instanceof Player player)) { sender.sendMessage("LSP " + getPluginMeta().getVersion() + ": " + sessions.size() + " sessoes ativas."); return true; }
        if (!player.hasPermission("lsp.paste")) { player.sendMessage("[LSP] Sem permissao lsp.paste."); return true; }
        String sub = args.length == 0 ? "help" : args[0].toLowerCase(Locale.ROOT);
        switch (sub) {
            case "cancel" -> { if (sessions.containsKey(player.getUniqueId())) cancel(player.getUniqueId(), "Operacao cancelada."); else player.sendMessage("[LSP] Nenhuma operacao ativa."); }
            case "status" -> {
                Session s = sessions.get(player.getUniqueId());
                String status = s == null ? "sem colagem ativa" : s.job != null ? s.job.status()
                    : s.transfer != null ? "recebendo " + s.transfer.received() + "/" + s.transfer.total() + " bytes" : "decodificando";
                player.sendMessage("[LSP] Protocolo: " + protocols.getOrDefault(player.getUniqueId(), 0) + "; " + status + ".");
            }
            default -> {
                player.sendMessage("[LSP] Litematica 0.27.9 / MC 26.1.2: entityDataSync=true, pasteUsingServux=true.");
                player.sendMessage("[LSP] Criativo + lsp.paste. Comandos: /lsp status e /lsp cancel.");
                player.sendMessage("[LSP] Cola blocos, entidades e ticks presentes no schematic. Ignore Entities desativa entidades na sub-regiao.");
            }
        }
        return true;
    }
}
