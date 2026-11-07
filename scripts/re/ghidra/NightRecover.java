// Usage: analyzeHeadless ... -postScript NightRecover.java <outdir>        (writes to the program; use the work copy)
// Two passes before a full decompile, for any x64 PE:
// 1. Creates a function at every .pdata entry no function covers: mostly EH funclets (catch and unwind blocks) and cold
//    chunks that auto-analysis does not reach. Without this their code is in no decompiled file.
// 2. Names the methods of RTTI classes: a default-named function in the vftable of exactly one class becomes
//    Class::vf<slot>, so calls read as methods in every decompile. Functions shared by classes keep their name.
// Writes <outdir>/recover.log and, after saving, <outdir>/recover.done.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class NightRecover extends GhidraScript {
    private PrintWriter log;

    @Override
    public void run() throws Exception {
        File out = new File(getScriptArgs()[0]);
        out.mkdirs();
        log = new PrintWriter(new FileWriter(new File(out, "recover.log"), true), true);
        recoverPdata();
        checkpoint();
        nameMethods();
        checkpoint();
        new FileWriter(new File(out, "recover.done")).close();
        say("done");
    }

    private void checkpoint() throws Exception {
        if (!currentProgram.canSave()) return; // a program -import just made: headless saves it after the scripts
        end(true);
        currentProgram.save("NightRecover", monitor);
        start();
    }

    private void recoverPdata() throws Exception {
        MemoryBlock pd = currentProgram.getMemory().getBlock(".pdata");
        if (pd == null) { say("no .pdata block"); return; }
        Memory mem = currentProgram.getMemory();
        FunctionManager fm = currentProgram.getFunctionManager();
        Address base = currentProgram.getImageBase();
        int entries = 0, covered = 0, created = 0, chained = 0, failed = 0;
        Address p = pd.getStart();
        for (long i = 0; i + 12 <= pd.getSize(); i += 12, p = p.add(12)) {
            monitor.checkCancelled();
            int begin = mem.getInt(p), unwind = mem.getInt(p.add(8));
            if (begin == 0) continue;
            entries++;
            Address a = base.add(begin & 0xffffffffL);
            if (fm.getFunctionContaining(a) != null) { covered++; continue; }
            if ((mem.getByte(base.add(unwind & 0xffffffffL)) >> 3 & 4) != 0) chained++;
            if (getInstructionAt(a) == null) disassemble(a);
            Function f = null;
            try { f = createFunction(a, null); } catch (Exception e) { say("create " + a + ": " + e); }
            if (f != null) created++;
            else { failed++; say("no function at " + a); }
            if (created % 5000 == 4999) { say("created " + created); checkpoint(); }
        }
        say("pdata: " + entries + " entries, " + covered + " covered, " + created + " functions created ("
            + chained + " chained chunks), " + failed + " failed");
    }

    private void nameMethods() throws Exception {
        SymbolTable st = currentProgram.getSymbolTable();
        Map<Function, Set<Namespace>> owners = new HashMap<>();
        Map<Function, Integer> slots = new HashMap<>();
        int tables = 0;
        for (Symbol s : st.getAllSymbols(false)) {
            if (!s.getName().startsWith("vftable")) continue;
            tables++;
            Namespace ns = s.getParentNamespace();
            Address at = s.getAddress();
            for (int slot = 0; ; slot++, at = at.add(8)) {
                if (slot > 0 && currentProgram.getReferenceManager().hasReferencesTo(at)) break;
                Function f = pointedFunction(at);
                if (f == null) break;
                owners.computeIfAbsent(f, k -> new HashSet<>()).add(ns);
                slots.putIfAbsent(f, slot);
            }
        }
        int named = 0, shared = 0;
        for (Map.Entry<Function, Set<Namespace>> e : owners.entrySet()) {
            monitor.checkCancelled();
            Function f = e.getKey();
            if (e.getValue().size() > 1) { shared++; continue; }
            if (f.getSymbol().getSource() != SourceType.DEFAULT || f.isThunk()) continue;
            String name = "vf" + slots.get(f);
            try {
                f.setParentNamespace(e.getValue().iterator().next());
                try { f.setName(name, SourceType.ANALYSIS); }
                catch (ghidra.util.exception.DuplicateNameException d) { f.setName(name + "_" + f.getEntryPoint(), SourceType.ANALYSIS); }
                named++;
            } catch (Exception x) { say("name " + f.getEntryPoint() + ": " + x); }
        }
        say("rtti: " + tables + " vftables, " + owners.size() + " methods, " + named + " named, " + shared + " shared by classes");
    }

    private Function pointedFunction(Address at) {
        try {
            long v = currentProgram.getMemory().getLong(at);
            Address t = at.getNewAddress(v);
            MemoryBlock b = currentProgram.getMemory().getBlock(t);
            return b != null && b.isExecute() ? currentProgram.getFunctionManager().getFunctionAt(t) : null;
        } catch (Exception e) { return null; }
    }

    private void say(String m) { log.println(m); println(m); }
}
