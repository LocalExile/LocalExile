// Usage: analyzeHeadless ... -readOnly -postScript NightSplit.java <decompdir> <addr> [instructions per piece=3000]
// A function too big for the decompiler in any pass, however long it runs: it needs more memory than the machine has. This
// decompiles it in pieces and writes them to its one file. For that, in a transaction that is rolled back, it replaces the
// function with one per switch case, and more where a piece would pass the limit. The decompiler stops at another function's
// entry, so each piece prints only its own blocks, as a function with no parameters that "calls" the pieces it jumps to. Whatever
// no piece printed becomes a new piece, a piece that fails is cut after its first instruction and tried again, and what is still
// in no piece goes in the file as disassembly, so every instruction of the function is in the file. Pieces do not share variables: what one leaves in a register, the next reads as in_RAX. Run DecompAll with "indexonly"
// afterwards to refresh index.tsv.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.SourceType;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

public class NightSplit extends GhidraScript {
    private static final int TIMEOUT = 120;

    private final Listing listing() { return currentProgram.getListing(); }

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        File dir = new File(a[0]);
        Function whole = getFunctionAt(toAddr(a[1]));
        int limit = a.length > 2 ? Integer.parseInt(a[2]) : 3000;
        Address entry = whole.getEntryPoint();
        String name = whole.getName();
        AddressSet body = new AddressSet(whole.getBody());
        long total = 0;
        TreeSet<Address> entries = new TreeSet<>();
        entries.add(entry);
        for (Instruction i : listing().getInstructions(body, true)) {
            total++;
            if (i.getFlowType().isJump() && i.getFlowType().isComputed())
                for (Address t : i.getFlows())
                    if (body.contains(t)) entries.add(t);
        }
        partition(body, entries, limit);
        println(name + ": " + total + " instructions, " + entries.size() + " pieces");

        TreeMap<Address, String> pieces = new TreeMap<>();
        List<String> failed = new ArrayList<>();
        AddressSet printed = new AddressSet();
        int tx = currentProgram.startTransaction("split");
        try {
            currentProgram.getFunctionManager().removeFunction(entry);
            TreeSet<Address> todo = new TreeSet<>(entries);
            while (!todo.isEmpty()) {
                monitor.checkCancelled();
                for (Address e : todo) create(e);
                List<Address> broken = decompile(todo, pieces, failed, printed);
                todo = new TreeSet<>();
                for (Address e : broken) {
                    Instruction i = listing().getInstructionAt(e);
                    List<Address> next = new ArrayList<>();
                    if (i.getFallThrough() != null) next.add(i.getFallThrough());
                    if (!i.getFlowType().isCall()) next.addAll(Arrays.asList(i.getFlows()));
                    boolean cut = false;
                    for (Address t : next)
                        if (body.contains(t) && entries.add(t)) {
                            todo.add(t);
                            cut = true;
                        }
                    if (cut) todo.add(e);
                    else failed.add(e.toString());
                }
                AddressSet missing = body.subtract(printed);
                for (Instruction i : listing().getInstructions(missing, true)) {
                    if (!entries.contains(i.getAddress()) && !printed.contains(i.getAddress())) {
                        todo.add(i.getAddress());
                        entries.add(i.getAddress());
                        break;
                    }
                }
            }
        } finally {
            currentProgram.endTransaction(tx, false);
        }

        long lost = 0;
        StringBuilder asm = new StringBuilder();
        for (Instruction i : listing().getInstructions(body.subtract(printed), true)) {
            lost++;
            asm.append("// ").append(i.getAddress()).append("  ").append(i).append('\n');
        }
        StringBuilder sb = new StringBuilder("// ===== " + name + " @ " + entry + " =====\n");
        sb.append("// reduced quality: split, the full decompile failed. " + total + " instructions in " + pieces.size()
            + " pieces (scripts/re/ghidra/NightSplit.java), " + lost + " instructions in no piece, " + failed.size() + " pieces failed\n");
        sb.append(asm);
        for (String f : failed) sb.append("// failed piece: ").append(f).append('\n');
        for (Map.Entry<Address, String> p : pieces.entrySet())
            sb.append("\n// ----- piece at ").append(p.getKey()).append(" -----\n").append(p.getValue());
        String ads = entry.toString();
        File out = new File(new File(dir, ads.substring(2, 5)), "FUN_" + ads + ".c");
        Files.write(out.toPath(), sb.toString().getBytes(StandardCharsets.UTF_8));
        println(name + ": " + pieces.size() + " pieces written, " + lost + " instructions in no piece, " + failed.size() + " failed");
        try (PrintWriter log = new PrintWriter(new FileWriter(new File(dir, "retry.tsv"), true))) {
            log.println("0x" + ads + "\t" + body.getNumAddresses() + "\tsplit into " + pieces.size() + " pieces, " + lost + " instructions in no piece, " + failed.size() + " failed");
        }
    }

    /** Adds entries until following flow from each one passes at most limit instructions before it meets another. */
    private void partition(AddressSet body, TreeSet<Address> entries, int limit) {
        Set<Address> seen = new HashSet<>();
        Deque<Address> work = new ArrayDeque<>(entries);
        while (true) {
            while (!work.isEmpty()) {
                Address e = work.poll();
                Deque<Address> flow = new ArrayDeque<>();
                flow.add(e);
                int count = 0;
                while (!flow.isEmpty()) {
                    Address at = flow.poll();
                    if (!seen.add(at)) continue;
                    Instruction i = listing().getInstructionAt(at);
                    if (i == null) continue;
                    count++;
                    List<Address> next = new ArrayList<>();
                    if (i.getFallThrough() != null) next.add(i.getFallThrough());
                    if (!i.getFlowType().isCall()) next.addAll(Arrays.asList(i.getFlows()));
                    for (Address t : next) {
                        if (!body.contains(t) || entries.contains(t) || seen.contains(t)) continue;
                        if (count >= limit) {
                            entries.add(t);
                            work.add(t);
                        } else {
                            flow.addLast(t);
                        }
                    }
                }
            }
            Address rest = null;
            for (Instruction i : listing().getInstructions(body, true)) {
                if (!seen.contains(i.getAddress())) {
                    rest = i.getAddress();
                    break;
                }
            }
            if (rest == null) return;
            entries.add(rest);
            work.add(rest);
        }
    }

    private void create(Address e) throws Exception {
        if (currentProgram.getFunctionManager().getFunctionAt(e) != null) return;
        Instruction i = listing().getInstructionAt(e);
        currentProgram.getFunctionManager().createFunction(null, e, new AddressSet(e, i.getMaxAddress()), SourceType.DEFAULT);
    }

    private DecompInterface open() {
        DecompInterface di = new DecompInterface();
        DecompileOptions o = new DecompileOptions();
        o.grabFromProgram(currentProgram);
        o.setMaxPayloadMBytes(1024);
        o.setMaxInstructions(1000000);
        di.setOptions(o);
        di.toggleCCode(true);
        di.toggleSyntaxTree(true);
        di.openProgram(currentProgram);
        return di;
    }

    /** Returns the pieces that failed. */
    private List<Address> decompile(Set<Address> todo, Map<Address, String> pieces, List<String> failed, AddressSet printed) throws Exception {
        List<Address> broken = new ArrayList<>();
        DecompInterface di = open();
        try {
            int n = 0;
            for (Address e : todo) {
                monitor.checkCancelled();
                DecompileResults r = di.decompileFunction(getFunctionAt(e), TIMEOUT, monitor);
                if (r != null && r.decompileCompleted() && r.getDecompiledFunction() != null) {
                    pieces.put(e, r.getDecompiledFunction().getC());
                    for (PcodeBlockBasic b : r.getHighFunction().getBasicBlocks()) printed.add(b.getStart(), b.getStop());
                } else {
                    broken.add(e);
                    di.dispose();
                    di = open();
                }
                if (++n % 100 == 0) println(n + " of " + todo.size() + " pieces");
            }
        } finally {
            di.dispose();
        }
        return broken;
    }
}
