// Maps addresses of one client build to another from a finished Version Tracking session
// (Agents/docs/re/tooling.md section 11).
// Run headless on the session's project with -process <any program> -noanalysis -readOnly:
//   PortMap.java <session path, e.g. /port3293c> <out dir> <addresses.txt>
// Writes to <out dir>:
//   functions.tsv  every accepted function association: old, new, size old, size new, identical (same instruction keys)
//   propagated.tsv functions paired after Version Tracking: old, new, instructions old, new, evidence, aligned share,
//                  taken or refused, round (see propagate())
//   refs.tsv       every reference target mapped through aligned instructions: old, new, votes; conflicts.tsv the rest
//   resolved.tsv   each address of <addresses.txt> (one 0x… per line): old, new or -, how (or why not)
// Instruction keys are mnemonic + operand kinds + register operands, so code that only moved compares equal. Matched
// functions are aligned by those keys (common prefix and suffix, then unique-key anchors, recursively); only aligned
// instructions map. A reference target maps when every aligned instruction pair that refers to it agrees.
//@category LocalExile
import java.io.*;
import java.nio.file.*;
import java.util.*;

import ghidra.app.script.GhidraScript;
import ghidra.feature.vt.api.db.VTSessionDB;
import ghidra.feature.vt.api.main.*;
import ghidra.framework.model.DomainFile;
import ghidra.program.model.address.*;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;

public class PortMap extends GhidraScript {
    Program a, b;
    final TreeMap<Long, Long> insn = new TreeMap<>();               // old instruction start -> new
    final TreeMap<Long, Long> fn = new TreeMap<>();                 // old function entry -> new
    final HashMap<Long, HashMap<Long, Integer>> votes = new HashMap<>();
    final TreeMap<Long, Long> refs = new TreeMap<>();
    final HashSet<Long> fnNew = new HashSet<>();                    // new function entries already paired
    List<Instruction[]> sink;                                        // while set, align() collects its pairs here

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        DomainFile df = state.getProject().getProjectData().getFile(args[0]);
        VTSessionDB session = (VTSessionDB) df.getDomainObject(this, true, true, monitor);
        a = session.getSourceProgram();
        b = session.getDestinationProgram();
        Path out = Paths.get(args[1]);
        Files.createDirectories(out);

        int accepted = 0, identical = 0;
        try (PrintWriter fw = new PrintWriter(Files.newBufferedWriter(out.resolve("functions.tsv")))) {
            for (VTAssociation as : session.getAssociationManager().getAssociations()) {
                if (as.getStatus() != VTAssociationStatus.ACCEPTED || as.getType() != VTAssociationType.FUNCTION) continue;
                Function fa = a.getFunctionManager().getFunctionAt(as.getSourceAddress());
                Function fb = b.getFunctionManager().getFunctionAt(as.getDestinationAddress());
                if (fa == null || fb == null) continue;
                monitor.checkCancelled();
                accepted++;
                fn.put(fa.getEntryPoint().getOffset(), fb.getEntryPoint().getOffset());
                List<Instruction> ia = instructions(a, fa), ib = instructions(b, fb);
                String[] ka = keys(ia), kb = keys(ib);
                boolean same = Arrays.equals(ka, kb);
                if (same) identical++;
                align(ia, ib, ka, kb, 0, ka.length, 0, kb.length);
                fw.printf("0x%x\t0x%x\t%d\t%d\t%s%n", fa.getEntryPoint().getOffset(), fb.getEntryPoint().getOffset(),
                    fa.getBody().getNumAddresses(), fb.getBody().getNumAddresses(), same ? "same" : "changed");
            }
        }
        println("PORTMAP functions accepted " + accepted + ", identical " + identical + ", instructions mapped " + insn.size());
        for (long v : fn.values()) fnNew.add(v);
        unanimous();
        propagate(out);

        int conflicts = 0;
        try (PrintWriter rw = new PrintWriter(Files.newBufferedWriter(out.resolve("refs.tsv")));
             PrintWriter cw = new PrintWriter(Files.newBufferedWriter(out.resolve("conflicts.tsv")))) {
            for (var e : new TreeMap<>(votes).entrySet()) {
                if (e.getValue().size() == 1) {
                    var only = e.getValue().entrySet().iterator().next();
                    rw.printf("0x%x\t0x%x\t%d%n", e.getKey(), only.getKey(), only.getValue());
                }
                else {
                    conflicts++;
                    StringBuilder sb = new StringBuilder();
                    e.getValue().forEach((k, v) -> sb.append(String.format(" 0x%x:%d", k, v)));
                    cw.printf("0x%x\t%s%n", e.getKey(), sb.toString().trim());
                }
            }
        }
        println("PORTMAP reference targets mapped " + refs.size() + ", conflicting " + conflicts);

        if (args.length > 2) resolve(Paths.get(args[2]), out.resolve("resolved.tsv"));
        session.release(this);
    }

    void unanimous() {
        refs.clear();
        for (var e : votes.entrySet())
            if (e.getValue().size() == 1) refs.put(e.getKey(), e.getValue().keySet().iterator().next());
    }

    // Functions Version Tracking left unmatched, paired by evidence from what is already mapped, then checked by
    // alignment. Evidence: a unanimous reference target that is a function entry on both sides (a call or a function
    // pointer), or a slot of two vtables that a unanimous reference pairs (see vtable()). A pair is taken when its old
    // and new function have no other candidate and at least half of the larger function aligns; its references then
    // vote, and the rounds repeat until none is taken.
    void propagate(Path out) throws Exception {
        FunctionManager fa = a.getFunctionManager(), fb = b.getFunctionManager();
        int round = 0, total = 0;
        try (PrintWriter pw = new PrintWriter(Files.newBufferedWriter(out.resolve("propagated.tsv")))) {
            while (true) {
                monitor.checkCancelled();
                HashMap<Long, HashMap<Long, String>> cand = new HashMap<>();
                for (var e : refs.entrySet()) {
                    long x = e.getKey(), y = e.getValue();
                    if (!fn.containsKey(x) && !fnNew.contains(y) && funcAt(a, x) != null && funcAt(b, y) != null)
                        cand.computeIfAbsent(x, k -> new HashMap<>()).putIfAbsent(y, "ref");
                    vtable(x, y, cand);
                }
                // One candidate each way.
                HashMap<Long, Integer> wanted = new HashMap<>();
                for (var m : cand.values()) for (long y : m.keySet()) wanted.merge(y, 1, Integer::sum);
                int taken = 0;
                for (var e : new TreeMap<>(cand).entrySet()) {
                    long x = e.getKey();
                    if (e.getValue().size() != 1) continue;
                    var only = e.getValue().entrySet().iterator().next();
                    long y = only.getKey();
                    if (wanted.get(y) != 1) continue;
                    Function f = funcAt(a, x), g = funcAt(b, y);
                    List<Instruction> ia = instructions(a, f), ib = instructions(b, g);
                    String[] ka = keys(ia), kb = keys(ib);
                    sink = new ArrayList<>();
                    align(ia, ib, ka, kb, 0, ka.length, 0, kb.length);
                    List<Instruction[]> pairs = sink;
                    sink = null;
                    double ratio = pairs.size() / (double) Math.max(1, Math.max(ka.length, kb.length));
                    boolean ok = ratio >= 0.5;
                    pw.printf("0x%x\t0x%x\t%d\t%d\t%s\t%.3f\t%s\t%d%n", x, y, ka.length, kb.length, only.getValue(), ratio,
                        ok ? "taken" : "refused", round);
                    if (!ok) continue;
                    fn.put(x, y);
                    fnNew.add(y);
                    for (Instruction[] p : pairs) pair(p[0], p[1]);
                    taken++;
                }
                total += taken;
                println("PORTMAP propagation round " + round + ": candidates " + cand.size() + ", taken " + taken);
                if (taken == 0) break;
                unanimous();
                round++;
            }
        }
        println("PORTMAP functions propagated " + total + ", instructions mapped " + insn.size());
    }

    // x -> y pairs two vtables (runs of function pointers in data). Slot 0 pairs through the reference itself; a later
    // slot only when an already-paired slot after it agrees and none before that disagrees (a class's vtables can be
    // reordered between builds, so the walk stops at the first disagreement or at a slot that is not a function).
    void vtable(long x, long y, HashMap<Long, HashMap<Long, String>> cand) {
        try {
            Address ax = addr(a, x), by = addr(b, y);
            MemoryBlock ma = a.getMemory().getBlock(ax), mb = b.getMemory().getBlock(by);
            if (ma == null || mb == null || ma.isExecute() || mb.isExecute() || !ma.isInitialized() || !mb.isInitialized()) return;
            List<long[]> open = new ArrayList<>();
            int lastAgree = -1;
            for (int k = 0; k < 4096; k++) {
                long pa = a.getMemory().getLong(ax.add(8L * k)), pb = b.getMemory().getLong(by.add(8L * k));
                if (funcAt(a, pa) == null || funcAt(b, pb) == null) break;
                Long m = fn.get(pa);
                if (m != null) {
                    if (m != pb) break;
                    lastAgree = k;
                }
                else if (!fnNew.contains(pb)) open.add(new long[] { pa, pb, k });
            }
            for (long[] o : open)
                if (o[2] == 0 || o[2] < lastAgree)
                    cand.computeIfAbsent(o[0], k -> new HashMap<>()).putIfAbsent(o[1], "vtable 0x" + Long.toHexString(x) + " slot " + o[2]);
        }
        catch (MemoryAccessException | AddressOutOfBoundsException e) { }
    }

    Function funcAt(Program p, long v) {
        try {
            Address ad = addr(p, v);
            if (!p.getMemory().contains(ad)) return null;
            return p.getFunctionManager().getFunctionAt(ad);
        }
        catch (AddressOutOfBoundsException e) { return null; }
    }

    static Address addr(Program p, long v) { return p.getAddressFactory().getDefaultAddressSpace().getAddress(v); }

    List<Instruction> instructions(Program p, Function f) {
        List<Instruction> list = new ArrayList<>();
        for (Instruction i : p.getListing().getInstructions(f.getBody(), true)) list.add(i);
        return list;
    }

    String[] keys(List<Instruction> list) {
        String[] k = new String[list.size()];
        for (int n = 0; n < k.length; n++) {
            Instruction i = list.get(n);
            StringBuilder sb = new StringBuilder(i.getMnemonicString());
            for (int op = 0; op < i.getNumOperands(); op++) {
                sb.append('|').append(i.getOperandType(op));
                for (Object o : i.getOpObjects(op))
                    if (o instanceof Register r) sb.append(',').append(r.getName());
                    else if (o instanceof Scalar s && i.getOperandReferences(op).length == 0) sb.append(",#").append(s.getValue());
            }
            k[n] = sb.toString();
        }
        return k;
    }

    // Aligns ka[a0,a1) with kb[b0,b1): equal prefix and suffix, then keys unique on both sides as anchors in order.
    void align(List<Instruction> ia, List<Instruction> ib, String[] ka, String[] kb, int a0, int a1, int b0, int b1) {
        while (a0 < a1 && b0 < b1 && ka[a0].equals(kb[b0])) pair(ia.get(a0++), ib.get(b0++));
        while (a0 < a1 && b0 < b1 && ka[a1 - 1].equals(kb[b1 - 1])) pair(ia.get(--a1), ib.get(--b1));
        if (a0 >= a1 || b0 >= b1) return;
        HashMap<String, int[]> ua = new HashMap<>(), ub = new HashMap<>();
        for (int n = a0; n < a1; n++) ua.merge(ka[n], new int[] { n, 1 }, (x, y) -> new int[] { x[0], x[1] + 1 });
        for (int n = b0; n < b1; n++) ub.merge(kb[n], new int[] { n, 1 }, (x, y) -> new int[] { x[0], x[1] + 1 });
        List<int[]> anchors = new ArrayList<>();
        for (int n = a0; n < a1; n++) {
            int[] x = ua.get(ka[n]), y = ub.get(ka[n]);
            if (x[1] == 1 && y != null && y[1] == 1) anchors.add(new int[] { n, y[0] });
        }
        // Longest increasing run of anchor positions in b (patience), so anchors keep their order.
        List<int[]> lis = increasing(anchors);
        if (lis.isEmpty()) return;
        int pa = a0, pb = b0;
        for (int[] an : lis) {
            align(ia, ib, ka, kb, pa, an[0], pb, an[1]);
            pair(ia.get(an[0]), ib.get(an[1]));
            pa = an[0] + 1; pb = an[1] + 1;
        }
        align(ia, ib, ka, kb, pa, a1, pb, b1);
    }

    static List<int[]> increasing(List<int[]> xs) {
        int n = xs.size();
        int[] tail = new int[n], prev = new int[n];
        int len = 0;
        for (int i = 0; i < n; i++) {
            int lo = 0, hi = len;
            while (lo < hi) { int m = (lo + hi) / 2; if (xs.get(tail[m])[1] < xs.get(i)[1]) lo = m + 1; else hi = m; }
            prev[i] = lo > 0 ? tail[lo - 1] : -1;
            tail[lo] = i;
            if (lo == len) len++;
        }
        LinkedList<int[]> out = new LinkedList<>();
        for (int i = len > 0 ? tail[len - 1] : -1; i >= 0; i = prev[i]) out.addFirst(xs.get(i));
        return out;
    }

    void pair(Instruction x, Instruction y) {
        if (sink != null) { sink.add(new Instruction[] { x, y }); return; }
        insn.put(x.getAddress().getOffset(), y.getAddress().getOffset());
        for (int op = 0; op < Math.min(x.getNumOperands(), y.getNumOperands()); op++) {
            Reference[] rx = x.getOperandReferences(op), ry = y.getOperandReferences(op);
            if (rx.length != ry.length) continue;
            for (int n = 0; n < rx.length; n++) {
                if (!rx[n].getToAddress().isMemoryAddress() || !ry[n].getToAddress().isMemoryAddress()) continue;
                votes.computeIfAbsent(rx[n].getToAddress().getOffset(), k -> new HashMap<>())
                    .merge(ry[n].getToAddress().getOffset(), 1, Integer::sum);
            }
        }
        // Flow references (calls and jumps) are on the instruction, not always on an operand.
        Address[] fx = x.getFlows(), fy = y.getFlows();
        if (fx != null && fy != null && fx.length == fy.length)
            for (int n = 0; n < fx.length; n++)
                votes.computeIfAbsent(fx[n].getOffset(), k -> new HashMap<>()).merge(fy[n].getOffset(), 1, Integer::sum);
    }

    // An address of the old build -> the new build, only through a recorded mapping, or by offset from a mapped start
    // when the bytes there prove it (equal bytes, or pointers whose targets map to each other).
    void resolve(Path list, Path out) throws Exception {
        int done = 0, total = 0;
        try (PrintWriter w = new PrintWriter(Files.newBufferedWriter(out))) {
            for (String line : Files.readAllLines(list)) {
                line = line.trim();
                if (!line.startsWith("0x")) continue;
                long x = Long.parseUnsignedLong(line.substring(2), 16);
                total++;
                String[] r = resolveOne(x);
                if (r[0] != null) done++;
                w.printf("0x%x\t%s\t%s%n", x, r[0] == null ? "-" : r[0], r[1]);
            }
        }
        println("PORTMAP addresses resolved " + done + " of " + total);
    }

    String[] resolveOne(long x) {
        Address ax = a.getAddressFactory().getDefaultAddressSpace().getAddress(x);
        if (!a.getMemory().contains(ax)) return new String[] { null, "not in the old image" };
        if (fn.containsKey(x)) return hit(fn.get(x), "function");
        if (insn.containsKey(x)) return hit(insn.get(x), "instruction");
        if (refs.containsKey(x)) return hit(refs.get(x), "reference");
        Instruction i = a.getListing().getInstructionContaining(ax);
        if (i != null) {
            long s = i.getAddress().getOffset();
            if (insn.containsKey(s)) return hit(insn.get(s) + (x - s), "inside instruction 0x" + Long.toHexString(s));
            Function f = a.getFunctionManager().getFunctionContaining(ax);
            return new String[] { null, f == null ? "instruction in no function" : "instruction not aligned in function 0x" + Long.toHexString(f.getEntryPoint().getOffset()) + (fn.containsKey(f.getEntryPoint().getOffset()) ? "" : " (function not matched)") };
        }
        // Data: the nearest mapped start at or below x in the same memory block, within 0x2000 bytes.
        MemoryBlock block = a.getMemory().getBlock(ax);
        Long below = refs.floorKey(x);
        if (below != null && x - below < 0x2000 && block.contains(ax.getNewAddress(below))) {
            long s = below, t = refs.get(below) + (x - s);
            String proof = proven(x, t);
            if (proof != null) return hit(t, "data at 0x" + Long.toHexString(s) + " +0x" + Long.toHexString(x - s) + " (" + proof + ")");
            return new String[] { null, "data near 0x" + Long.toHexString(s) + " but the bytes do not prove the offset" };
        }
        return new String[] { null, "data with no mapped start nearby" };
    }

    String[] hit(long t, String how) { return new String[] { "0x" + Long.toHexString(t), how }; }

    String proven(long x, long t) {
        try {
            Address ax = a.getAddressFactory().getDefaultAddressSpace().getAddress(x);
            Address bt = b.getAddressFactory().getDefaultAddressSpace().getAddress(t);
            byte[] p = new byte[16], q = new byte[16];
            int np = a.getMemory().getBytes(ax, p), nq = b.getMemory().getBytes(bt, q);
            boolean uniform = true;
            for (byte v : p) uniform &= v == p[0];
            if (np == 16 && nq == 16 && Arrays.equals(p, q) && !uniform) return "16 equal bytes";
            long vp = a.getMemory().getLong(ax), vq = b.getMemory().getLong(bt);
            Long m = fn.get(vp);
            if (m == null) m = refs.get(vp);
            if (m == null) m = insn.get(vp);
            if (m != null && m == vq) return "pointer to mapped 0x" + Long.toHexString(vp);
        }
        catch (MemoryAccessException | AddressOutOfBoundsException e) { }
        return null;
    }
}
