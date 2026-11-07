// Usage: analyzeHeadless ... -postScript NightSwitches.java <outdir>
// Recovers the switches auto-analysis left as "Could not recover jumptable": a JMP through a register after a load from
// a u32 table, either image relative with the table inline after the function (MSVC, often with no bounds check) or
// relative to the table itself (clang). Disassembles the cases, adds them to the function, writes a jump table override
// for the decompiler, marks the table as data. Cases NightFragments had turned into their own functions are removed when
// nothing calls them, and so are its functions made of a table misread as code. Repeats, since new cases hold more
// switches; then does the same for the tables of switches auto-analysis did resolve, which it leaves undefined. Writes
// <outdir>/switches.tsv; functions in it need decompiling again.
import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.JumpTable;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;
import java.util.regex.*;

public class NightSwitches extends GhidraScript {
    static final Pattern WORD = Pattern.compile("dword ptr \\[(\\w+) \\+ (\\w+)\\*0x4(?: \\+ (0x[0-9a-f]+))?\\]");
    static final Pattern BYTE = Pattern.compile("byte ptr \\[(\\w+) \\+ (\\w+)(?: \\+ (0x[0-9a-f]+))?\\]");
    static final int BACK = 48;

    private PrintWriter out;
    private FunctionManager fm;
    private long imageBase;
    private int removed;

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        out = new PrintWriter(new FileWriter(new File(args[0], "switches.tsv"), true), true);
        fm = currentProgram.getFunctionManager();
        imageBase = currentProgram.getImageBase().getOffset();
        int total = 0, unresolved = 0;
        for (int pass = 1; pass <= 12; pass++) {
            List<Address> sites = new ArrayList<>();
            for (Instruction i : currentProgram.getListing().getInstructions(true)) {
                if (i.getMnemonicString().equals("JMP") && i.getRegister(0) != null && !hasComputedFlow(i))
                    sites.add(i.getAddress());
            }
            int fixed = 0, failed = 0;
            for (Address a : sites) {
                monitor.checkCancelled();
                String why;
                try {
                    why = recover(getInstructionAt(a), false);
                } catch (Exception e) {
                    why = e.toString();
                }
                if (why == null) fixed++;
                else {
                    failed++;
                    if (pass == 1) out.println("skip\t" + a + "\t" + why);
                }
            }
            total += fixed;
            unresolved = failed;
            println("pass " + pass + ": " + sites.size() + " sites, " + fixed + " recovered, " + failed + " left");
            end(true);
            currentProgram.save("NightSwitches", monitor);
            start();
            if (fixed == 0) break;
        }
        int marked = 0;
        List<Address> resolved = new ArrayList<>();
        for (Instruction i : currentProgram.getListing().getInstructions(true))
            if (i.getMnemonicString().equals("JMP") && i.getRegister(0) != null && hasComputedFlow(i)) resolved.add(i.getAddress());
        for (Address a : resolved) {
            monitor.checkCancelled();
            try {
                String why = recover(getInstructionAt(a), true);
                if (why == null) marked++;
                else out.println("nomark	" + a + "	" + why);
            } catch (Exception e) {
                out.println("skip	" + a + "	" + e);
            }
        }
        println(marked + " of " + resolved.size() + " resolved tables marked");
        println(total + " switches recovered, " + unresolved + " register jumps left, " + removed + " case functions removed");
        out.close();
    }

    private boolean hasComputedFlow(Instruction i) {
        for (Reference r : i.getReferencesFrom())
            if (r.getReferenceType().isComputed() && r.getReferenceType().isJump()) return true;
        return false;
    }

    private Register base(Register r) {
        return r == null ? null : r.getBaseRegister();
    }

    private String recover(Instruction jump, boolean tableOnly) throws Exception {
        Register target = base(jump.getRegister(0));
        Function fn = fm.getFunctionContaining(jump.getAddress());
        if (fn == null) return "no function";
        if (fn.isThunk()) return "thunk";
        Instruction load = null;
        Matcher word = null;
        Instruction p = jump;
        for (int k = 0; k < 12 && load == null; k++) {
            p = getInstructionBefore(p);
            if (p == null || stops(p)) return "no table load";
            String m = p.getMnemonicString();
            if ((m.equals("MOV") || m.equals("MOVSXD")) && base(p.getRegister(0)) == target) {
                word = WORD.matcher(p.toString());
                if (!word.find()) return "other load: " + p;
                load = p;
            }
        }
        if (load == null) return "no table load";
        Register baseReg = currentProgram.getRegister(word.group(1));
        Register indexReg = base(currentProgram.getRegister(word.group(2)));
        long disp = word.group(3) == null ? 0 : Long.parseLong(word.group(3).substring(2), 16);
        Long baseValue = null, bound = null, byteTable = null;
        for (p = load, baseValue = null; (p = getInstructionBefore(p)) != null && !stops(p) && ((load.getAddress().subtract(p.getAddress())) < 400); ) {
            String m = p.getMnemonicString();
            if (baseValue == null && m.equals("LEA") && base(p.getRegister(0)) == base(baseReg)) {
                Address to = refTo(p);
                if (to == null) return "base is not an address: " + p;
                baseValue = to.getOffset();
            }
            if (bound == null && byteTable == null && m.equals("MOVZX") && base(p.getRegister(0)) == indexReg) {
                Matcher b = BYTE.matcher(p.toString());
                if (b.find() && b.group(3) != null) {
                    byteTable = Long.parseLong(b.group(3).substring(2), 16);
                    indexReg = base(currentProgram.getRegister(b.group(2)));
                }
            }
            if (bound == null && m.equals("CMP") && base(p.getRegister(0)) == indexReg) {
                Object[] o = p.getOpObjects(1);
                if (o.length == 1 && o[0] instanceof Scalar) bound = ((Scalar) o[0]).getUnsignedValue() + 1;
            }
            if (baseValue != null && bound != null) break;
        }
        boolean imageRelative = disp != 0;
        if (baseValue == null) {
            if (!imageRelative) return "no base";
            baseValue = imageBase;
        }
        if (byteTable != null && baseValue != imageBase) return "index bytes with a table relative base";
        Address table = toAddr(baseValue + disp);
        if (byteTable != null && bound == null) return "index bytes with no bound";
        if (bound != null && bound > 4096) return "bound " + bound;

        byte[] index = null;
        long count;
        if (byteTable != null) {
            index = getBytes(toAddr(baseValue + byteTable), bound.intValue());
            int max = 0;
            for (byte b : index) max = Math.max(max, b & 0xff);
            count = max + 1;
        } else {
            count = bound == null ? -1 : bound;
        }

        Address low = fn.getEntryPoint();
        // With no bound the table is the one after the code: its cases come before it, and the first word that isn't one ends it.
        Address high = count < 0 ? table : low.add(0x400000);
        // A resolved switch says what its cases are: this also finds them when its function is only a piece of the real one.
        Set<Address> known = new HashSet<>();
        if (tableOnly) {
            for (Reference r : jump.getReferencesFrom()) if (r.getReferenceType().isComputed()) known.add(r.getToAddress());
            if (byteTable == null) count = -1;  // the references decide where the table ends
        }
        Set<Address> targets = new LinkedHashSet<>();
        List<Address> list = new ArrayList<>();
        int n = 0;
        for (; count < 0 || n < count; n++) {
            Address at = table.add(4L * n);
            if (count < 0 && (n >= 4096 || currentProgram.getListing().getDefinedDataAt(at) != null)) break;
            int v = getInt(at);
            long t = imageRelative ? baseValue + (v & 0xffffffffL) : baseValue + v;
            Address to = toAddr(t);
            if (tableOnly ? !known.contains(to) : to.compareTo(low) < 0 || to.compareTo(high) >= 0
                || !currentProgram.getMemory().getExecuteSet().contains(to)) {
                if (count < 0 || tableOnly) break;
                if (byteTable != null) return "case " + n + " at " + to + " is outside " + low + ".." + high;
                // The bound was another comparison's: start again with none.
                count = -1;
                high = table;
                n = -1;
                targets.clear();
                list.clear();
                continue;
            }
            list.add(to);
            targets.add(to);
        }
        if (n < 2) return "table of " + n;

        StringBuilder gone = new StringBuilder();
        String junk = clearMisread(table, 4L * n, fn, gone);
        if (junk == null && index != null) junk = clearMisread(toAddr(baseValue + byteTable), index.length, fn, gone);
        if (junk != null) return junk;
        if (tableOnly) {
            markTable(table, n, 4);
            if (index != null) markTable(toAddr(baseValue + byteTable), index.length, 1);
            if (gone.length() > 0) out.println("table	" + jump.getAddress() + "	" + fn.getEntryPoint() + "	" + table + "	" + n + "	0	" + gone.toString().trim());
            return null;
        }
        int fresh = 0;
        for (Address t : targets) {
            Function g = fm.getFunctionAt(t);
            if (g != null && !g.equals(fn)) {
                if (g.isThunk()) return "case " + t + " is a thunk";  // what "calls" a case is misread data
                fm.removeFunction(t);
                removed++;
                gone.append(' ').append(t);
            }
            Instruction c = getInstructionContaining(t);
            if (c != null && !c.getAddress().equals(t)) {
                String why = clearMisread(c.getAddress(), c.getLength(), fn, gone);
                if (why != null) return why;
            }
            if (getInstructionAt(t) == null) {
                if (!disassemble(t)) return "case " + t + " does not disassemble";
                fresh++;
            }
        }
        ReferenceManager refs = currentProgram.getReferenceManager();
        for (Address t : targets)
            refs.addMemoryReference(jump.getAddress(), t, RefType.COMPUTED_JUMP, SourceType.ANALYSIS, Reference.MNEMONIC);
        markTable(table, n, 4);
        if (index != null) markTable(toAddr(baseValue + byteTable), index.length, 1);
        CreateFunctionCmd.fixupFunctionBody(currentProgram, fn, monitor);
        ArrayList<Address> dests = new ArrayList<>(list);
        new JumpTable(jump.getAddress(), dests, true, 0).writeOverride(fn);
        out.println("ok\t" + jump.getAddress() + "\t" + fn.getEntryPoint() + "\t" + table + "\t" + n + "\t" + fresh + "\t" + gone.toString().trim());
        return null;
    }

    /** Clears instructions in a range that holds data. Refuses if they belong to the switch's function or a called one. */
    private String clearMisread(Address start, long length, Function fn, StringBuilder gone) throws Exception {
        Address end = start.add(length - 1);
        for (Address a = start; a.compareTo(end) <= 0; a = a.next()) {
            Instruction i = getInstructionContaining(a);
            if (i == null) continue;
            Function g = fm.getFunctionContaining(i.getAddress());
            if (g != null) {
                if (g.equals(fn)) return "table at " + start + " overlaps its function";
                // A function starting inside the table is misread data whatever calls it: other misread data does.
                if (g.getEntryPoint().compareTo(start) >= 0 && g.getEntryPoint().compareTo(end) <= 0 || !isCalled(g.getEntryPoint()) && !g.isThunk()) {
                    gone.append(' ').append(g.getEntryPoint());
                    fm.removeFunction(g.getEntryPoint());
                    removed++;
                }
            }
            clearListing(i.getAddress(), i.getMaxAddress());
        }
        return null;
    }

    private Address refTo(Instruction i) {
        for (Reference r : i.getReferencesFrom())
            if (r.isMemoryReference()) return r.getToAddress();
        Object[] o = i.getOpObjects(1);
        for (Object x : o) if (x instanceof Address) return (Address) x;
        for (Object x : o) if (x instanceof Scalar) return toAddr(((Scalar) x).getUnsignedValue());
        return null;
    }

    private boolean stops(Instruction i) {
        FlowType f = i.getFlowType();
        return f.isTerminal() || (f.isJump() && f.isUnConditional());
    }

    private boolean isCalled(Address a) {
        for (Reference r : getReferencesTo(a))
            if (r.getReferenceType().isCall() || r.getReferenceType().isData()) return true;
        return false;
    }

    private void markTable(Address start, int count, int size) {
        for (int i = 0; i < count; i++) {
            Address a = start.add((long) i * size);
            try {
                if (currentProgram.getListing().getDefinedDataAt(a) == null && getInstructionContaining(a) == null) {
                    if (size == 4) createDWord(a);
                    else createByte(a);
                }
            } catch (Exception e) {
                // a table overlapping defined data keeps what is there
            }
        }
    }
}
