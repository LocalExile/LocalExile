// Usage: -postScript SendSites.java <out.json> [send:0x14216e940/RDX+0x141d625f0/RDX]
// Every call of a message send function, resolved to the (vtable, id) pairs of the message it sends. The message
// argument is a stack object: its vtable is the qword at [slot], its u16 id the word at [slot+8]. From the call the
// control-flow graph (with Ghidra's resolved jump tables) is walked backwards path by path, so ids set in different
// switch cases or branches before one shared send are all found, each with the vtable on its own path. A message built
// by a constructor (a call with RCX = &slot) takes its vtable and id from the constructor's stores to [this] and
// [this+8], or from the id argument it is passed. Anything else is reported as unresolved, never guessed.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.block.*;
import ghidra.program.model.lang.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class SendSites extends GhidraScript {
    static final long UNKNOWN = Long.MIN_VALUE;
    BasicBlockModel bbm;
    Set<Register> volatileRegs = new HashSet<>();

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        bbm = new BasicBlockModel(currentProgram);
        for (String r : new String[] { "RAX", "RCX", "RDX", "R8", "R9", "R10", "R11" }) volatileRegs.add(reg(r));
        LinkedHashMap<Long, String> sends = new LinkedHashMap<>();
        sends.put(0x14216e940L, "RDX");
        for (int i = 1; i < a.length; i++) {
            if (!a[i].startsWith("send:")) continue;
            sends.clear();
            for (String s : a[i].substring(5).split("[+]")) {
                String[] p = s.split("/");
                sends.put(Long.decode(p[0]), p[1]);
            }
        }
        StringBuilder out = new StringBuilder("{\"generator\":\"scripts/re/ghidra/SendSites.java\",\n\"sites\":[\n");
        boolean first = true;
        int nSites = 0, nOk = 0;
        for (Map.Entry<Long, String> e : sends.entrySet()) {
            Register arg = reg(e.getValue());
            for (Reference r : getReferencesTo(toAddr(e.getKey()))) {
                if (!r.getReferenceType().isCall()) continue;
                Instruction call = getInstructionAt(r.getFromAddress());
                if (call == null) continue;
                nSites++;
                Function f = getFunctionContaining(call.getAddress());
                List<String> notes = new ArrayList<>();
                Set<String> pairs = new TreeSet<>();
                Long slot = stackSlot(call, arg);
                if (slot == null) notes.add("message is not a stack object");
                else walk(call, slot, pairs, notes);
                boolean ok = notes.isEmpty() && !pairs.isEmpty() && pairs.stream().noneMatch(p -> p.contains("null"));
                if (ok) nOk++;
                out.append(first ? "" : ",\n").append("{\"send\":\"0x").append(Long.toHexString(e.getKey()))
                    .append("\",\"site\":\"0x").append(call.getAddress()).append("\",\"func\":")
                    .append(f == null ? "null" : "\"0x" + f.getEntryPoint() + "\"")
                    .append(",\"slot\":").append(slot == null ? "null" : slot.toString())
                    .append(",\"pairs\":[").append(String.join(",", pairs)).append("],\"notes\":[");
                for (int k = 0; k < notes.size(); k++) out.append(k == 0 ? "" : ",").append(q(notes.get(k)));
                out.append("]}");
                first = false;
            }
        }
        out.append("\n]}\n");
        try (Writer w = new OutputStreamWriter(new FileOutputStream(a[0]), "UTF-8")) { w.write(out.toString()); }
        println("SCRIPT SendSites: " + nSites + " sites, " + nOk + " fully resolved -> " + a[0]);
    }

    Register reg(String n) { return currentProgram.getLanguage().getRegister(n); }
    static String q(String s) { return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""; }
    static String hex(long v) { return v == UNKNOWN ? "null" : "\"0x" + Long.toHexString(v) + "\""; }
    static long mask(long v) { return v == UNKNOWN ? v : v & 0xffff; }

    /** The stack offset an argument register points to at a call: LEA reg,[stack] before it (through register moves). */
    Long stackSlot(Instruction at, Register r) {
        Instruction i = at.getPrevious();
        for (int k = 0; k < 40 && i != null; k++, i = i.getPrevious()) {
            if (i.getMnemonicString().equals("CALL")) return null;
            if (!writes(i, r)) continue;
            String m = i.getMnemonicString();
            if (m.equals("LEA")) return stackRef(i);
            if (m.equals("MOV") && i.getOpObjects(1).length == 1 && i.getOpObjects(1)[0] instanceof Register) {
                r = ((Register) i.getOpObjects(1)[0]).getBaseRegister();
                continue;
            }
            return null;
        }
        return null;
    }

    /** The stack offset an instruction references (Ghidra attaches it to operand 0 even for LEA's source). */
    Long stackRef(Instruction i) {
        for (Reference r : i.getReferencesFrom())
            if (r instanceof StackReference) return (long) ((StackReference) r).getStackOffset();
        return null;
    }

    /** The stack offset an instruction writes to, or null. */
    Long stackWrite(Instruction i) {
        for (Reference r : i.getReferencesFrom())
            if (r instanceof StackReference && r.getReferenceType().isWrite()) return (long) ((StackReference) r).getStackOffset();
        return null;
    }

    boolean writes(Instruction i, Register r) {
        Register b = r.getBaseRegister();
        for (Object o : i.getResultObjects())
            if (o instanceof Register && ((Register) o).getBaseRegister().equals(b)) return true;
        return false;
    }

    int storeSize(Instruction i) {
        String s = i.getDefaultOperandRepresentation(0);
        if (s.startsWith("byte ptr")) return 1;
        if (s.startsWith("word ptr")) return 2;
        if (s.startsWith("dword ptr")) return 4;
        if (s.startsWith("qword ptr")) return 8;
        if (s.startsWith("xmmword ptr")) return 16;
        if (s.startsWith("ymmword ptr")) return 32;
        return 0;
    }

    Address callee(Instruction call) {
        for (Reference ref : call.getReferencesFrom()) if (ref.getReferenceType().isCall()) return ref.getToAddress();
        return null;
    }

    /** One backward path state: the vtable and id found so far (later stores win, so the first one met counts).
     *  A field is null until found, a Long once known, or the Register it was stored from while that register's
     *  definition is still being looked for on the same path (so values merged at a join stay paired by path). */
    record State(Address block, Object vt, Object id) {}

    /** The last instruction of each predecessor block of the block that starts at i (no call edges). */
    List<Instruction> preds(Instruction i) throws Exception {
        List<Instruction> out = new ArrayList<>();
        CodeBlock b = bbm.getFirstCodeBlockContaining(i.getAddress(), monitor);
        Function f = getFunctionContaining(i.getAddress());
        if (b == null || f == null || f.getEntryPoint().equals(b.getMinAddress())) return out;
        CodeBlockReferenceIterator it = b.getSources(monitor);
        while (it.hasNext()) {
            CodeBlockReference cr = it.next();
            if (cr.getFlowType().isCall()) continue;
            CodeBlock sb = cr.getSourceBlock();
            Instruction last = sb == null ? null : getInstructionContaining(sb.getMaxAddress());
            if (last != null) out.add(last);
        }
        return out;
    }

    /** Where to continue backwards from just before i: the previous instruction in its block, or the predecessors. */
    List<Instruction> before(Instruction i) throws Exception {
        CodeBlock b = bbm.getFirstCodeBlockContaining(i.getAddress(), monitor);
        if (b != null && !b.getMinAddress().equals(i.getAddress()) && i.getPrevious() != null) return List.of(i.getPrevious());
        return preds(i);
    }

    static String pair(Object vt, Object id) {
        long v = vt instanceof Long ? (Long) vt : UNKNOWN, d = id instanceof Long ? mask((Long) id) : UNKNOWN;
        return "{\"vtable\":" + hex(v) + ",\"id\":" + hex(d) + "}";
    }

    /** A value stored from operand op: the immediate, or the source register (resolved later on the same path). */
    Object source(Instruction i, int op) {
        Object[] o = i.getOpObjects(op);
        if (o.length == 1 && o[0] instanceof Scalar) return ((Scalar) o[0]).getValue();
        if (o.length == 1 && o[0] instanceof Register) return ((Register) o[0]).getBaseRegister();
        return UNKNOWN;
    }

    /** A pending register field going back over instruction i: unchanged, or what i set it to. */
    Object through(Instruction i, Object f) {
        if (!(f instanceof Register r)) return f;
        String m = i.getMnemonicString();
        if (m.equals("CALL")) return volatileRegs.contains(r) ? (Object) UNKNOWN : f;
        if (!writes(i, r)) return f;
        Object[] s = i.getNumOperands() > 1 ? i.getOpObjects(1) : new Object[0];
        if (m.equals("MOV") && s.length == 1 && s[0] instanceof Scalar) return ((Scalar) s[0]).getValue();
        if ((m.equals("MOV") || m.equals("MOVZX")) && s.length == 1 && s[0] instanceof Register) return ((Register) s[0]).getBaseRegister();
        if (m.equals("XOR") && i.getOpObjects(0).length == 1 && s.length == 1 && s[0].equals(i.getOpObjects(0)[0])) return 0L;
        if (m.equals("LEA"))
            for (Reference ref : i.getOperandReferences(1)) if (ref.isMemoryReference()) return ref.getToAddress().getOffset();
        return UNKNOWN;
    }

    void walk(Instruction call, long slot, Set<String> pairs, List<String> notes) throws Exception {
        Deque<Object[]> work = new ArrayDeque<>();
        Set<State> seen = new HashSet<>();
        for (Instruction p : before(call)) work.push(new Object[] { p, null, null });
        if (work.isEmpty()) notes.add("no path into the call");
        int steps = 0;
        while (!work.isEmpty()) {
            if (++steps > 50000) { notes.add("path limit"); return; }
            Object[] w = work.pop();
            Instruction i = (Instruction) w[0];
            Object vt = w[1], id = w[2];
            CodeBlock b = bbm.getFirstCodeBlockContaining(i.getAddress(), monitor);
            if (b == null) { pairs.add(pair(vt, id)); notes.add("no block at " + i.getAddress()); continue; }
            if (!seen.add(new State(b.getMinAddress(), vt, id))) continue;
            boolean done = false;
            while (true) {
                vt = through(i, vt);
                id = through(i, id);
                String m = i.getMnemonicString();
                Long so = stackWrite(i);
                int sz = so == null ? 0 : storeSize(i);
                if (so != null && sz > 0) {
                    long lo = so, hi = so + sz;
                    if (vt == null && lo <= slot && hi > slot) {
                        if (lo == slot && sz == 8 && m.equals("MOV")) vt = source(i, 1);
                        else { vt = UNKNOWN; notes.add("vtable slot written by " + m + " at " + i.getAddress()); }
                    }
                    if (id == null && lo <= slot + 8 && hi > slot + 8) {
                        if (lo == slot + 8 && sz >= 2 && m.equals("MOV")) id = source(i, 1);
                        else { id = UNKNOWN; notes.add("id slot written by " + m + " at " + i.getAddress()); }
                    }
                } else if (m.equals("CALL") && (vt == null || id == null)) {
                    Long rc = stackSlot(i, reg("RCX"));
                    if (rc != null && rc == slot) {
                        // a constructor, or a call that only fills fields: take what it stores, keep looking for the rest
                        long[] c = ctor(i);
                        if (vt == null && c[0] != UNKNOWN) vt = c[0];
                        if (id == null && c[1] != UNKNOWN) id = c[1];
                    }
                }
                if (vt instanceof Long && id instanceof Long) { pairs.add(pair(vt, id)); done = true; break; }
                if (i.getAddress().equals(b.getMinAddress()) || i.getPrevious() == null) break;
                i = i.getPrevious();
            }
            if (done) continue;
            List<Instruction> ps = preds(getInstructionAt(b.getMinAddress()));
            if (ps.isEmpty()) { pairs.add(pair(vt, id)); notes.add("reached the function entry"); }
            for (Instruction p : ps) work.push(new Object[] { p, vt, id });
        }
    }

    /** Values a source operand can hold: an immediate, or a register's values found backwards (several at merges). */
    Set<Long> values(Instruction i, int op) throws Exception {
        Object[] o = i.getOpObjects(op);
        Set<Long> out = new TreeSet<>();
        if (o.length == 1 && o[0] instanceof Scalar) { out.add(((Scalar) o[0]).getValue()); return out; }
        if (o.length == 1 && o[0] instanceof Register) { regBefore(i, (Register) o[0], out, new HashSet<>()); return out; }
        out.add(UNKNOWN);
        return out;
    }

    /** The values register r can hold just before instruction i. */
    void regBefore(Instruction i, Register r, Set<Long> out, Set<Address> seen) throws Exception {
        List<Instruction> bs = before(i);
        if (bs.isEmpty()) out.add(UNKNOWN);
        for (Instruction p : bs) regAt(p, r, out, seen);
    }

    /** The values register r holds just after instruction i (walking back from i, inclusive). */
    void regAt(Instruction i, Register r, Set<Long> out, Set<Address> seen) throws Exception {
        if (seen.size() > 400) { out.add(UNKNOWN); return; }
        if (!seen.add(i.getAddress())) return;
        if (i.getMnemonicString().equals("CALL") && volatileRegs.contains(r.getBaseRegister())) { out.add(UNKNOWN); return; }
        if (writes(i, r)) {
            String m = i.getMnemonicString();
            Object[] s = i.getNumOperands() > 1 ? i.getOpObjects(1) : new Object[0];
            if (m.equals("MOV") && s.length == 1 && s[0] instanceof Scalar) { out.add(((Scalar) s[0]).getValue()); return; }
            if ((m.equals("MOV") || m.equals("MOVZX")) && s.length == 1 && s[0] instanceof Register) {
                regBefore(i, (Register) s[0], out, seen);
                return;
            }
            if (m.equals("XOR") && i.getOpObjects(0).length == 1 && s.length == 1 && s[0].equals(i.getOpObjects(0)[0])) { out.add(0L); return; }
            if (m.equals("LEA"))
                for (Reference ref : i.getOperandReferences(1))
                    if (ref.isMemoryReference()) { out.add(ref.getToAddress().getOffset()); return; }
            out.add(UNKNOWN);
            return;
        }
        regBefore(i, r, out, seen);
    }

    /** A constructor called with RCX = the message: the last vtable it stores at [this] and the id at [this+8]
     *  (an immediate, or its id argument resolved at the call). Follows calls into base constructors. */
    long[] ctor(Instruction call) throws Exception {
        long[] r = { UNKNOWN, UNKNOWN };
        Address t = callee(call);
        if (t != null) ctorScan(t, call, r, 0);
        return r;
    }

    void ctorScan(Address entry, Instruction callerCall, long[] r, int depth) throws Exception {
        Set<Register> self = new HashSet<>(List.of(reg("RCX")));
        Instruction i = getInstructionAt(entry);
        for (int k = 0; k < 80 && i != null; k++, i = i.getNext()) {
            String m = i.getMnemonicString();
            if (m.equals("RET") || m.equals("JMP")) break;
            Object[] d = i.getOpObjects(0), s = i.getNumOperands() > 1 ? i.getOpObjects(1) : new Object[0];
            if (m.equals("MOV") && s.length == 1 && s[0] instanceof Register && self.contains(((Register) s[0]).getBaseRegister())
                && d.length == 1 && d[0] instanceof Register) {
                self.add(((Register) d[0]).getBaseRegister());
                continue;
            }
            if (m.equals("CALL")) {
                Address t = callee(i);
                if (t != null && depth < 2 && (r[0] == UNKNOWN || r[1] == UNKNOWN)) {
                    long[] sub = { UNKNOWN, UNKNOWN };
                    ctorScan(t, callerCall, sub, depth + 1);
                    if (sub[0] != UNKNOWN) r[0] = sub[0];
                    if (sub[1] != UNKNOWN) r[1] = sub[1];
                }
                self.retainAll(Set.of(reg("RBX"), reg("RSI"), reg("RDI"), reg("RBP"), reg("R12"), reg("R13"), reg("R14"), reg("R15")));
                continue;
            }
            for (Object o : i.getResultObjects())
                if (o instanceof Register) self.remove(((Register) o).getBaseRegister());
            if (!m.equals("MOV") || !i.getDefaultOperandRepresentation(0).contains("[")) continue;
            Register base = null;
            long disp = 0;
            for (Object o : d) {
                if (o instanceof Register) base = ((Register) o).getBaseRegister();
                if (o instanceof Scalar) disp = ((Scalar) o).getSignedValue();
            }
            if (base == null || !self.contains(base)) continue;
            if (disp == 0 && storeSize(i) == 8 && s.length == 1 && s[0] instanceof Register) {
                Set<Long> vs = new TreeSet<>();
                regBefore(i, (Register) s[0], vs, new HashSet<>());
                if (vs.size() == 1 && !vs.contains(UNKNOWN)) r[0] = vs.iterator().next();
            } else if (disp == 8 && storeSize(i) >= 2) {
                if (s.length == 1 && s[0] instanceof Scalar) r[1] = ((Scalar) s[0]).getValue() & 0xffff;
                else if (s.length == 1 && s[0] instanceof Register && depth == 0) {
                    Register sr = ((Register) s[0]).getBaseRegister();
                    if (sr.equals(reg("RDX")) || sr.equals(reg("R8")) || sr.equals(reg("R9"))) {
                        Set<Long> vs = new TreeSet<>();
                        regBefore(callerCall, sr, vs, new HashSet<>());
                        if (vs.size() == 1 && !vs.contains(UNKNOWN)) r[1] = vs.iterator().next() & 0xffff;
                    }
                }
            }
        }
    }
}
