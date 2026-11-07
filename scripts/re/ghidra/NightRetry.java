// Usage: analyzeHeadless ... -readOnly -postScript NightRetry.java <decompdir> [timeoutSeconds=900]
// Decompiles again, one at a time with a long timeout and larger limits, every function DecompAll left as "decompile
// failed" in <decompdir>. Each one walks down a ladder until something prints: the full decompile; the full decompile
// with the function's stored prototype and variables dropped (needs -readOnly: the edit must not reach the project);
// then the decompiler's earlier passes, which print no C, so the result is their p-code. A success replaces the file,
// and anything below the full decompile says so on its second line. <decompdir>/retry.tsv records every result. Run
// DecompAll with "indexonly" afterwards to refresh index.tsv.
import com.sun.management.OperatingSystemMXBean;
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.data.DataType;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.Namespace;
import ghidra.program.model.symbol.SourceType;
import ghidra.util.task.TaskMonitorAdapter;
import java.io.*;
import java.lang.management.ManagementFactory;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

public class NightRetry extends GhidraScript {
    private static final String FAILED = "// decompile failed: ";
    private static final long MIN_FREE_COMMIT_BYTES = 4L << 30;

    private String error;
    private boolean timedOut;

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        File dir = new File(a[0]);
        int timeout = a.length > 1 ? Integer.parseInt(a[1]) : 900;
        List<String[]> todo = new ArrayList<>();
        try (BufferedReader r = Files.newBufferedReader(new File(dir, "index.tsv").toPath(), StandardCharsets.UTF_8)) {
            for (String l; (l = r.readLine()) != null; ) {
                String[] p = l.split("\t", -1);
                if (p.length >= 5 && p[4].equals("failed")) todo.add(p);
            }
        }
        int full = 0, reduced = 0, failed = 0;
        try (PrintWriter log = new PrintWriter(new FileWriter(new File(dir, "retry.tsv"), true))) {
            for (String[] p : todo) {
                monitor.checkCancelled();
                Function f = getFunctionAt(toAddr(p[0]));
                if (f == null) continue;
                String level = ladder(f, dir, timeout);
                if (level == null) failed++;
                else if (level.equals("full")) full++;
                else reduced++;
                String shown = level == null ? "failed: " + error : level;
                log.println(p[0] + "\t" + p[1] + "\t" + shown);
                log.flush();
                println("retry " + p[0] + " (" + p[1] + " bytes): " + shown);
            }
        }
        println("retry finished: " + full + " full, " + reduced + " reduced, " + failed + " still failed");
        new FileWriter(new File(dir, "retry.done")).close();
    }

    /** Returns the level that printed, or null with the last error in {@link #error}. */
    private String ladder(Function f, File dir, int timeout) throws Exception {
        if (attempt(f, dir, "decompile", timeout, "full")) return "full";
        boolean slow = timedOut;
        String first = error;
        // NightSwitches' override kills the decompiler process in a few functions, though its table is right.
        if (!slow && clearOverride(f) && attempt(f, dir, "decompile", timeout, "no switch override")) return "no switch override";
        clearPrototype(f);
        // Dropping the prototype fixes a bad one, not a function that is simply too much work.
        if (!slow && attempt(f, dir, "decompile", timeout, "no stored prototype")) return "no stored prototype";
        for (String style : new String[] { "normalize", "register", "firstpass" })
            if (attempt(f, dir, style, timeout, style + " pass")) return style + " pass";
        error = first + " / last: " + error;
        recordFailure(f, dir);
        return null;
    }

    private boolean attempt(Function f, File dir, String style, int timeout, String level) throws Exception {
        DecompInterface di = new DecompInterface();
        try {
            DecompileOptions o = new DecompileOptions();
            o.grabFromProgram(currentProgram);
            o.setMaxPayloadMBytes(1024);
            o.setMaxInstructions(1000000);
            di.setOptions(o);
            boolean c = style.equals("decompile");
            di.toggleCCode(c);
            di.toggleSyntaxTree(!c);
            di.setSimplificationStyle(style);
            di.openProgram(currentProgram);
            println("trying " + f.getEntryPoint() + " at " + level);
            TaskMonitorAdapter stop = new TaskMonitorAdapter(true);
            Thread guard = memoryGuard(stop);
            DecompileResults res = di.decompileFunction(f, timeout, stop);
            guard.interrupt();
            timedOut = res != null && res.isTimedOut();
            boolean good = res != null && res.decompileCompleted() && (c ? res.getDecompiledFunction() != null : res.getHighFunction() != null);
            if (good) {
                String note = level.equals("full") ? "" : "// reduced quality: " + level + ", the full decompile failed\n";
                write(f, dir, note + (c ? res.getDecompiledFunction().getC() : pcode(res.getHighFunction())));
                return true;
            }
            error = stop.isCancelled() ? "stopped, low memory" : res == null ? "null result" : res.getErrorMessage().trim().replace('\n', ' ');
            if (timedOut) error = "timeout " + timeout + " s";
            println(f.getEntryPoint() + " at " + level + ": " + error);
            return false;
        } finally {
            di.dispose();
        }
    }

    private static String pcode(HighFunction h) {
        StringBuilder sb = new StringBuilder();
        for (PcodeBlockBasic b : h.getBasicBlocks()) {
            sb.append("// block ").append(b.getStart()).append(" - ").append(b.getStop()).append('\n');
            for (Iterator<PcodeOp> ops = b.getIterator(); ops.hasNext(); ) {
                PcodeOp op = ops.next();
                sb.append(op.getSeqnum().getTarget()).append("  ").append(op).append('\n');
            }
        }
        return sb.toString();
    }

    /** The biggest functions need more than the free RAM, so they may spill into the page file, but not fill it. */
    private Thread memoryGuard(TaskMonitorAdapter stop) {
        OperatingSystemMXBean os = (OperatingSystemMXBean) ManagementFactory.getOperatingSystemMXBean();
        Thread t = new Thread(() -> {
            try {
                while (true) {
                    Thread.sleep(5000);
                    if (os.getFreeSwapSpaceSize() < MIN_FREE_COMMIT_BYTES) { stop.cancel(); return; }
                }
            } catch (InterruptedException e) { }
        });
        t.setDaemon(true);
        t.start();
        return t;
    }

    private boolean clearOverride(Function f) throws Exception {
        Namespace overrides = currentProgram.getSymbolTable().getNamespace("override", f);
        if (overrides == null) return false;
        int tx = currentProgram.startTransaction("retry without switch override");
        try {
            overrides.getSymbol().delete();
        } finally {
            currentProgram.endTransaction(tx, true);
        }
        return true;
    }

    private void clearPrototype(Function f) throws Exception {
        int tx = currentProgram.startTransaction("retry without prototype");
        try {
            for (Variable v : f.getLocalVariables()) f.removeVariable(v);
            f.setCustomVariableStorage(false);
            f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS, true, SourceType.DEFAULT);
            f.setReturnType(DataType.DEFAULT, SourceType.DEFAULT);
            f.setSignatureSource(SourceType.DEFAULT);
        } finally {
            currentProgram.endTransaction(tx, true);
        }
    }

    private void recordFailure(Function f, File dir) throws IOException {
        write(f, dir, FAILED + error + "\n");
    }

    private void write(Function f, File dir, String body) throws IOException {
        String ads = f.getEntryPoint().toString();
        File out = new File(new File(dir, ads.length() >= 5 ? ads.substring(2, 5) : "low"), "FUN_" + ads + ".c");
        Files.write(out.toPath(), ("// ===== " + f.getName() + " @ " + ads + " =====\n" + body).getBytes(StandardCharsets.UTF_8));
    }
}
