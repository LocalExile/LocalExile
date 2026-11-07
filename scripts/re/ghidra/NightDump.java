// Usage: analyzeHeadless ... -readOnly -postScript NightDump.java <outdir> [limit=0]
// Everything about a program that is not decompiled C, as text next to the decompile. One step per file, written to
// .tmp and renamed; a file that exists is skipped, so a crashed run resumes. limit > 0 stops each table after that many
// functions (for tests). <outdir>/dump.done appears when all steps ran; a failed step is in dump.log and leaves no file.
//   segments.tsv      name start end size perms
//   functions.tsv     addr size name namespace thunk_of callers callees signature
//   calls.tsv         caller_fn callsite target target_name (calls and tail jumps out of the function)
//   datarefs.tsv      from_fn from to type (every other reference to memory)
//   strings.tsv       addr type value
//   symbols.tsv       addr type source namespace name (everything not default-named)
//   imports.tsv       library name address, exports.tsv  addr name
//   vtables.tsv       class vtable slot function name (RTTI vftables)
//   vtables-unnamed.tsv  vtable slot function name (pointer runs in .rdata no RTTI names)
//   types.h           every data type in the program
//   disasm/<hex digits 3-5>.asm  every instruction, by function
//   coverage.tsv + undecompiled.tsv  executable bytes outside any function, padding left out
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.data.DataTypeWriter;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

public class NightDump extends GhidraScript {
    private interface Step { void write(PrintWriter w) throws Exception; }

    private File out;
    private int limit;
    private PrintWriter log;
    private boolean allOk = true;

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        out = new File(a[0]);
        limit = a.length > 1 ? Integer.parseInt(a[1]) : 0;
        out.mkdirs();
        log = new PrintWriter(new FileWriter(new File(out, "dump.log"), true), true);
        step("segments.tsv", this::segments);
        step("functions.tsv", this::functions);
        step("calls.tsv", w -> refs(w, true));
        step("datarefs.tsv", w -> refs(w, false));
        step("strings.tsv", this::strings);
        step("symbols.tsv", this::symbols);
        step("imports.tsv", this::imports);
        step("exports.tsv", this::exports);
        step("vtables.tsv", this::vtables);
        step("vtables-unnamed.tsv", this::unnamedVtables);
        step("types.h", w -> new DataTypeWriter(currentProgram.getDataTypeManager(), w).write(currentProgram.getDataTypeManager(), monitor));
        step("undecompiled.tsv", this::coverage);
        disasm();
        if (allOk) new FileWriter(new File(out, "dump.done")).close();
        say("finished, all ok: " + allOk);
    }

    private void step(String name, Step s) {
        File f = new File(out, name), tmp = new File(out, name + ".tmp");
        if (f.exists()) { say(name + ": exists"); return; }
        long t0 = System.currentTimeMillis();
        try (PrintWriter w = new PrintWriter(new BufferedWriter(new OutputStreamWriter(new FileOutputStream(tmp), StandardCharsets.UTF_8), 1 << 20))) {
            s.write(w);
        } catch (Throwable e) {
            allOk = false;
            tmp.delete();
            say(name + ": FAILED " + e);
            return;
        }
        try { Files.move(tmp.toPath(), f.toPath(), StandardCopyOption.REPLACE_EXISTING); }
        catch (IOException e) { allOk = false; say(name + ": " + e); return; }
        say(name + ": " + f.length() + " bytes, " + (System.currentTimeMillis() - t0) / 1000 + " s");
    }

    private void segments(PrintWriter w) {
        w.println("name\tstart\tend\tsize\tperms");
        for (MemoryBlock b : currentProgram.getMemory().getBlocks())
            w.println(b.getName() + "\t" + b.getStart() + "\t" + b.getEnd() + "\t" + b.getSize() + "\t"
                + (b.isRead() ? "r" : "-") + (b.isWrite() ? "w" : "-") + (b.isExecute() ? "x" : "-"));
    }

    private List<Function> all() {
        List<Function> l = new ArrayList<>();
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            if (f.isExternal()) continue;
            l.add(f);
            if (limit > 0 && l.size() >= limit) break;
        }
        return l;
    }

    private void functions(PrintWriter w) throws Exception {
        w.println("addr\tsize\tname\tnamespace\tthunk_of\tcallers\tcallees\tsignature");
        for (Function f : all()) {
            monitor.checkCancelled();
            Function t = f.isThunk() ? f.getThunkedFunction(true) : null;
            w.println(f.getEntryPoint() + "\t" + f.getBody().getNumAddresses() + "\t" + clean(f.getName()) + "\t"
                + clean(f.getParentNamespace().getName(true)) + "\t" + (t == null ? "" : clean(t.getName(true))) + "\t"
                + f.getCallingFunctions(monitor).size() + "\t" + f.getCalledFunctions(monitor).size() + "\t"
                + clean(f.getPrototypeString(true, false)));
        }
    }

    private void refs(PrintWriter w, boolean calls) throws Exception {
        FunctionManager fm = currentProgram.getFunctionManager();
        Listing listing = currentProgram.getListing();
        w.println(calls ? "caller_fn\tcallsite\ttarget\ttarget_name" : "from_fn\tfrom\tto\ttype");
        for (Function f : all()) {
            monitor.checkCancelled();
            for (Instruction ins : listing.getInstructions(f.getBody(), true))
                for (Reference r : ins.getReferencesFrom()) {
                    Address to = r.getToAddress();
                    if (r.isStackReference() || !(to.isMemoryAddress() || to.isExternalAddress())) continue;
                    RefType t = r.getReferenceType();
                    boolean leaves = t.isCall() || (t.isFlow() && !f.getBody().contains(to));
                    if (t.isFlow() && !leaves) continue;
                    if (leaves != calls) continue;
                    if (calls) {
                        Function c = fm.getFunctionAt(to);
                        w.println(f.getEntryPoint() + "\t" + ins.getAddress() + "\t" + to + "\t" + (c == null ? "" : clean(c.getName(true))));
                    } else w.println(f.getEntryPoint() + "\t" + ins.getAddress() + "\t" + to + "\t" + t.getName());
                }
        }
    }

    private void strings(PrintWriter w) throws Exception {
        w.println("addr\ttype\tvalue");
        for (Data d : currentProgram.getListing().getDefinedData(true)) {
            monitor.checkCancelled();
            if (!d.hasStringValue() || d.getValue() == null) continue;
            w.println(d.getAddress() + "\t" + d.getDataType().getName() + "\t" + clean(d.getValue().toString()));
        }
    }

    private void symbols(PrintWriter w) throws Exception {
        w.println("addr\ttype\tsource\tnamespace\tname");
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            monitor.checkCancelled();
            if (s.getSource() == SourceType.DEFAULT) continue;
            w.println(s.getAddress() + "\t" + s.getSymbolType() + "\t" + s.getSource() + "\t"
                + clean(s.getParentNamespace().getName(true)) + "\t" + clean(s.getName()));
        }
    }

    private void imports(PrintWriter w) {
        w.println("library\tname\taddress");
        for (Symbol s : currentProgram.getSymbolTable().getExternalSymbols())
            w.println(clean(s.getParentNamespace().getName(true)) + "\t" + clean(s.getName()) + "\t" + s.getAddress());
    }

    private void exports(PrintWriter w) {
        w.println("addr\tname");
        SymbolTable st = currentProgram.getSymbolTable();
        for (Address a : st.getExternalEntryPointIterator()) {
            Symbol s = st.getPrimarySymbol(a);
            w.println(a + "\t" + (s == null ? "" : clean(s.getName(true))));
        }
    }

    private Function pointed(Address at) {
        try {
            Address t = at.getNewAddress(currentProgram.getMemory().getLong(at));
            MemoryBlock b = currentProgram.getMemory().getBlock(t);
            return b != null && b.isExecute() ? currentProgram.getFunctionManager().getFunctionAt(t) : null;
        } catch (Exception e) { return null; }
    }

    private void vtables(PrintWriter w) throws Exception {
        w.println("class\tvtable\tslot\tfunction\tname");
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            monitor.checkCancelled();
            if (!s.getName().startsWith("vftable")) continue;
            Address at = s.getAddress();
            for (int slot = 0; ; slot++, at = at.add(8)) {
                if (slot > 0 && rm.hasReferencesTo(at)) break;
                Function f = pointed(at);
                if (f == null) break;
                w.println(clean(s.getParentNamespace().getName(true)) + "\t" + s.getAddress() + "\t" + slot + "\t" + f.getEntryPoint() + "\t" + clean(f.getName(true)));
            }
        }
    }

    private void unnamedVtables(PrintWriter w) throws Exception {
        w.println("vtable\tslot\tfunction\tname");
        MemoryBlock b = currentProgram.getMemory().getBlock(".rdata");
        if (b == null) return;
        ReferenceManager rm = currentProgram.getReferenceManager();
        SymbolTable st = currentProgram.getSymbolTable();
        Address end = b.getEnd().subtract(8);
        Address at = b.getStart();
        while (at.compareTo(end) < 0) {
            monitor.checkCancelled();
            Symbol s = st.getPrimarySymbol(at);
            boolean named = s != null && s.getName().startsWith("vftable");
            if (named || !rm.hasReferencesTo(at) || pointed(at) == null) { at = at.add(8); continue; }
            List<Function> run = new ArrayList<>();
            Address p = at;
            while (p.compareTo(end) < 0 && (run.isEmpty() || !rm.hasReferencesTo(p))) {
                Function f = pointed(p);
                if (f == null) break;
                run.add(f);
                p = p.add(8);
            }
            if (run.size() >= 3)
                for (int i = 0; i < run.size(); i++)
                    w.println(at + "\t" + i + "\t" + run.get(i).getEntryPoint() + "\t" + clean(run.get(i).getName(true)));
            at = p.equals(at) ? at.add(8) : p;
        }
    }

    private void coverage(PrintWriter w) throws Exception {
        w.println("start\tend\tsize\tkind");
        Memory mem = currentProgram.getMemory();
        AddressSet funcs = new AddressSet();
        for (Function f : currentProgram.getFunctionManager().getFunctions(true))
            if (!f.isExternal()) funcs.add(f.getBody());
        long exec = 0, in = 0, padding = 0, code = 0, data = 0, defined = 0;
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isExecute() || !b.isInitialized()) continue;
            AddressSet blk = new AddressSet(b.getStart(), b.getEnd());
            exec += b.getSize();
            in += blk.intersect(funcs).getNumAddresses();
            // Jump tables NightSwitches marked inside the code are data, not unknown bytes.
            AddressSet rest = blk.subtract(funcs), marked = new AddressSet();
            for (Data d : currentProgram.getListing().getDefinedData(rest, true)) marked.add(d.getMinAddress(), d.getMaxAddress());
            defined += marked.getNumAddresses();
            for (AddressRange r : rest.subtract(marked)) {
                monitor.checkCancelled();
                byte[] bytes = new byte[(int) r.getLength()];
                mem.getBytes(r.getMinAddress(), bytes);
                boolean pad = true;
                for (byte x : bytes) if (x != (byte) 0xcc && x != 0) { pad = false; break; }
                if (pad) { padding += r.getLength(); continue; }
                boolean ins = currentProgram.getListing().getInstructions(new AddressSet(r), true).hasNext();
                if (ins) code += r.getLength(); else data += r.getLength();
                w.println(r.getMinAddress() + "\t" + r.getMaxAddress() + "\t" + r.getLength() + "\t" + (ins ? "instructions" : "undefined"));
            }
        }
        try (PrintWriter c = new PrintWriter(new File(out, "coverage.tsv"), StandardCharsets.UTF_8)) {
            c.println("executable_bytes\tin_functions\tpadding\toutside_with_instructions\toutside_undefined	defined_data");
            c.println(exec + "\t" + in + "\t" + padding + "\t" + code + "\t" + data + "	" + defined);
        }
    }

    private void disasm() {
        File dir = new File(out, "disasm"), mark = new File(dir, "complete");
        if (mark.exists()) { say("disasm: exists"); return; }
        dir.mkdirs();
        Listing listing = currentProgram.getListing();
        String open = null;
        PrintWriter w = null;
        try {
            for (Function f : all()) {
                monitor.checkCancelled();
                String ad = f.getEntryPoint().toString();
                String bucket = ad.length() >= 5 ? ad.substring(2, 5) : "low";
                if (!bucket.equals(open)) {
                    if (w != null) w.close();
                    w = new PrintWriter(new BufferedWriter(new OutputStreamWriter(new FileOutputStream(new File(dir, bucket + ".asm")), StandardCharsets.UTF_8), 1 << 20));
                    open = bucket;
                }
                w.println("; ===== " + clean(f.getName(true)) + " @ " + ad + " =====");
                for (Instruction ins : listing.getInstructions(f.getBody(), true))
                    w.println(ins.getAddress() + "  " + ins);
            }
            if (w != null) w.close();
            new FileWriter(mark).close();
            say("disasm: done");
        } catch (Throwable e) {
            if (w != null) w.close();
            allOk = false;
            say("disasm: FAILED " + e);
        }
    }

    private static String clean(String s) {
        return s.replace("\\", "\\\\").replace("\t", "\\t").replace("\r", "\\r").replace("\n", "\\n");
    }

    private void say(String m) { log.println(m); println(m); }
}
