// Usage: -postScript ReadSchema.java <out.json> <components.json> [factory:0x1421b5c40+0x1421c6150+0x14219c350+0x1428f74d0] [only:0xfunc+...]
// (no "=" or ",": cmd.exe splits arguments there)
//
// Read-sequence summaries for
//   - every component deserializer in components.json (vtable slot 17 = +0x88, and slot 20 when slot 17 forwards), and
//   - every message class the S->C factories create (switch case -> vtable; slot 1 = write, slot 2 = read).
// Callee parameters are committed first (decompile + commitParamsToDatabase, bottom-up, in memory only: the project is
// opened -readOnly), so calls that pass the reader in a register the callee's stored prototype did not list (the
// ObjectMagicProperties base part, 0x141f92000 in message reads) show their arguments.
//
// A summary keeps the decompiled C lines that read, and the if/loop/switch lines around them. Reads are found in the
// p-code of the decompiled function:
//   - inline blob reads: STORE reader+0x10 = old + N (the bounds-checked memcpy pattern of {data, size, pos});
//   - known primitives (PRIMS below) called with the reader;
//   - calls that pass the reader on (summarized as their own function, "sub");
//   - indirect calls that pass the reader ("vcall [vt+0x..]").
// "if" blocks whose condition loads reader+8 (the size) are bounds checks and are flattened. "min" is the byte count of
// the reads outside any condition or loop (callees included). Output: JSON; scripts/re/schema_md.py makes the docs.
import ghidra.app.decompiler.*;
import ghidra.app.decompiler.component.DecompilerUtils;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.pcode.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.regex.*;

public class ReadSchema extends GhidraScript {
    // address -> {name, min bytes or -1, kind}; kind r = reader primitive, s = stream read (dst, n), w = stream write
    static final Object[][] PRIMS = {
        { 0x141e61010L, "cint", 1, "r" }, { 0x14089cad0L, "cint", 1, "r" }, { 0x141fa3d60L, "cint", 1, "r" },
        { 0x140e0cbe0L, "wstr", -1, "r" }, { 0x1408402a0L, "str", -1, "r" },
        { 0x141fa4610L, "blob16", 2, "r" }, { 0x141fa44f0L, "blob16.w", 2, "w" },
        { 0x140dfb630L, "raw", 0, "s" }, { 0x140dfb760L, "raw.w", 0, "sw" },
        { 0x141fa3c50L, "cint.w", 1, "w" },
        { 0x140e28cf0L, "raw", 0, "b" },      // blob reader memcpy (reader, dst, n)
    };
    static final long[] IGNORE = { 0x140e28a30L, 0x142cdd4c0L }; // out-of-range exception builder, throw
    static final String[] ARG_REGS = { "RCX", "RDX", "R8", "R9" };

    DecompInterface di;
    Memory mem;
    Map<Long, Object[]> prims = new HashMap<>();
    Map<String, Summary> memo = new LinkedHashMap<>();
    Set<String> inProgress = new HashSet<>();
    Set<Long> only = null;
    boolean withC = false; // "withc": also write each summarized function's C (debugging)

    static class Ev {
        String desc;
        int min;        // -1 = variable/unknown
        Function callee;
        int calleeArg = -1;
    }

    static class Summary {
        String key, func, name;
        int readerArg, min;
        boolean unknownMin, reads, failed, returnsReader;
        List<String> lines = new ArrayList<>();
        String c;
        Set<String> subs = new LinkedHashSet<>();
    }

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        mem = currentProgram.getMemory();
        for (Object[] p : PRIMS) prims.put((Long) p[0], p);
        List<Long> factories = new ArrayList<>(List.of(0x1421b5c40L, 0x1421c6150L, 0x14219c350L, 0x1428f74d0L));
        for (int i = 2; i < a.length; i++) {
            if (a[i].startsWith("factory:")) {
                factories.clear();
                for (String s : a[i].substring(8).split("[+]")) factories.add(Long.decode(s));
            }
            if (a[i].equals("withc")) withC = true;
            if (a[i].startsWith("only:")) {
                only = new HashSet<>();
                for (String s : a[i].substring(5).split("[+]")) only.add(Long.decode(s));
            }
        }
        di = new DecompInterface();
        DecompileOptions opt = new DecompileOptions();
        di.setOptions(opt);
        di.toggleCCode(true);
        di.toggleSyntaxTree(true);
        di.setSimplificationStyle("decompile");
        di.openProgram(currentProgram);

        // Roots: deserializers.
        List<String[]> compRoots = new ArrayList<>(); // func, label
        String json = Files.readString(Path.of(a[1]));
        Pattern reg = Pattern.compile("\\{\"name\":\"([^\"]*)\",\"typeId\":(-?\\d+).*");
        // Only each factory's own vtable and its subclasses ("primary"); member objects' vtables are not components.
        Pattern vt = Pattern.compile("\\{\"vt\":\"(0x[0-9a-f]+)\",\"len\":\\d+,\"primary\":true[^{}]*?\"slot17\":\"(0x[0-9a-f]+)\",\"slot17Kind\":\"(\\w+)\"(?:,\"slot20\":\"(0x[0-9a-f]+)\")?");
        for (String line : json.split("\r?\n")) {
            Matcher m = reg.matcher(line);
            if (!m.matches()) continue;
            String name = m.group(1);
            if (Integer.parseInt(m.group(2)) < 0 && !name.startsWith("Client")) continue;
            Matcher v = vt.matcher(line);
            while (v.find()) {
                String kind = v.group(3);
                if (kind.equals("code")) compRoots.add(new String[] { v.group(2), name + " " + v.group(1) + " slot17" });
                if (kind.equals("fwd20") && v.group(4) != null)
                    compRoots.add(new String[] { v.group(4), name + " " + v.group(1) + " slot20 (via slot17 " + v.group(2) + ")" });
            }
        }
        // Roots: messages.
        List<String[]> msgs = new ArrayList<>(); // factory, id, vtable, write, read, via
        for (long f : factories) msgs.addAll(factoryCases(getFunctionAt(toAddr(f))));

        // Commit parameters bottom-up over the call closure of every root.
        LinkedHashSet<Function> roots = new LinkedHashSet<>();
        for (String[] r : compRoots) addFn(roots, r[0]);
        for (String[] m : msgs) { addFn(roots, m[3]); addFn(roots, m[4]); }
        if (only != null) roots.removeIf(f -> !only.contains(f.getEntryPoint().getOffset()));
        List<Function> order = new ArrayList<>();
        Set<Function> seen = new HashSet<>();
        for (Function f : roots) postOrder(f, 0, seen, order);
        println("committing parameters of " + order.size() + " functions");
        int done = 0;
        for (Function f : order) {
            if (monitor.isCancelled()) break;
            if (f.getSignatureSource() != SourceType.DEFAULT && f.getSignatureSource() != SourceType.ANALYSIS) continue;
            DecompileResults r = di.decompileFunction(f, 60, monitor);
            if (r.decompileCompleted() && r.getHighFunction() != null) {
                try {
                    HighFunctionDBUtil.commitParamsToDatabase(r.getHighFunction(), true,
                        HighFunctionDBUtil.ReturnCommitOption.COMMIT, SourceType.ANALYSIS);
                } catch (Exception e) { /* keep the old prototype */ }
            }
            if (++done % 500 == 0) println("  " + done);
        }
        di.flushCache();

        // Summaries.
        StringBuilder comps = new StringBuilder(), ms = new StringBuilder();
        for (String[] r : compRoots) {
            Function f = getFunctionAt(toAddr(r[0]));
            if (f == null || (only != null && !only.contains(f.getEntryPoint().getOffset()))) continue;
            Summary s = summarize(f, 1);
            comps.append(comps.length() == 0 ? "" : ",\n").append("{\"label\":").append(js(r[1]))
                 .append(",\"func\":").append(js(r[0])).append(",\"summary\":").append(js(s.key)).append("}");
        }
        for (String[] m : msgs) {
            String w = "null", rd = "null";
            Function wf = m[3] == null ? null : getFunctionAt(toAddr(m[3])), rf = m[4] == null ? null : getFunctionAt(toAddr(m[4]));
            if (only != null) {
                if (wf != null && !only.contains(wf.getEntryPoint().getOffset())) wf = null;
                if (rf != null && !only.contains(rf.getEntryPoint().getOffset())) rf = null;
                if (wf == null && rf == null) continue;
            }
            if (wf != null) w = js(summarize(wf, 1).key);
            if (rf != null) rd = js(summarize(rf, 1).key);
            ms.append(ms.length() == 0 ? "" : ",\n").append("{\"factory\":").append(js(m[0])).append(",\"id\":").append(js(m[1]))
              .append(",\"vtable\":").append(js(m[2])).append(",\"write\":").append(js(m[3])).append(",\"read\":").append(js(m[4]))
              .append(",\"via\":").append(js(m[5])).append(",\"writeSummary\":").append(w).append(",\"readSummary\":").append(rd).append("}");
        }
        PrintWriter out = new PrintWriter(new OutputStreamWriter(new FileOutputStream(a[0]), "UTF-8"));
        out.println("{\"generator\":\"scripts/re/ghidra/ReadSchema.java\",");
        out.println("\"components\":[\n" + comps + "],");
        out.println("\"messages\":[\n" + ms + "],");
        out.println("\"summaries\":{");
        boolean first = true;
        for (Summary s : memo.values()) {
            out.print((first ? "" : ",\n") + js(s.key) + ":{\"func\":" + js(s.func) + ",\"name\":" + js(s.name) + ",\"readerArg\":" + s.readerArg
                + ",\"min\":" + s.min + ",\"returnsReader\":" + s.returnsReader + ",\"minIsLowerBound\":" + s.unknownMin + ",\"reads\":" + s.reads + ",\"failed\":" + s.failed
                + ",\"subs\":" + jsList(s.subs) + ",\"lines\":" + jsList(s.lines) + (s.c == null ? "" : ",\"c\":" + js(s.c)) + "}");
            first = false;
        }
        out.println("\n}}");
        out.close();
        println("components " + compRoots.size() + ", messages " + msgs.size() + ", summaries " + memo.size());
    }

    void addFn(Set<Function> s, String a) {
        if (a == null) return;
        Function f = getFunctionAt(toAddr(a));
        if (f != null) s.add(f);
    }

    void postOrder(Function f, int depth, Set<Function> seen, List<Function> order) {
        if (f == null || f.isThunk() || !seen.add(f) || prims.containsKey(f.getEntryPoint().getOffset())) return;
        if (depth < 4 && f.getBody().getNumAddresses() < 12000)
            for (Function c : f.getCalledFunctions(monitor)) postOrder(c, depth + 1, seen, order);
        order.add(f);
    }

    // ---- factory switch -> message classes ----

    List<String[]> factoryCases(Function f) throws Exception {
        List<String[]> out = new ArrayList<>();
        DecompileResults r = di.decompileFunction(f, 120, monitor);
        HighFunction h = r.getHighFunction();
        if (h == null) { println("factory decompile failed " + f); return out; }
        Map<Address, List<Integer>> byCase = new TreeMap<>();
        for (JumpTable jt : h.getJumpTables()) {
            Address[] cases = jt.getCases();
            Integer[] labels = jt.getLabelValues();
            for (int i = 0; i < cases.length && i < labels.length; i++)
                byCase.computeIfAbsent(cases[i], k -> new ArrayList<>()).add(labels[i]);
        }
        // The switch index is (id - base); the decompiler's labels are the case values of the C switch (the ids).
        for (var e : byCase.entrySet()) {
            String[] cls = caseClass(e.getKey());
            for (int id : e.getValue()) {
                if (cls[0] == null) { out.add(new String[] { hex(f.getEntryPoint()), "0x" + Integer.toHexString(id), null, null, null, cls[1] }); continue; }
                Address v = toAddr(cls[0]);
                String w = hex(mem.getLong(v.add(8))), rd = hex(mem.getLong(v.add(16)));
                out.add(new String[] { hex(f.getEntryPoint()), "0x" + Integer.toHexString(id), cls[0], w, rd, cls[1] });
            }
        }
        return out;
    }

    /** Walk a case body (fallthrough, stop at a return): the last vtable it stores at offset 0 of a register, else the
     *  same in the first non-allocator function it calls (the constructor). An unconditional jump before any vtable
     *  store is followed inside the factory: cases that share a constructor tail set their id and jump into it
     *  (login 0x24/0x25/0x26/0x28 jump into 0x29's). */
    String[] caseClass(Address start) throws Exception {
        Instruction i = getInstructionAt(start);
        Function owner = getFunctionContaining(start);
        Set<Address> seen = new HashSet<>();
        List<Function> calls = new ArrayList<>();
        String vt = null;
        for (int k = 0; k < 120 && i != null && seen.add(i.getAddress()); k++) {
            String m = i.getMnemonicString();
            String v = zeroStoreVt(i);
            if (v != null) vt = v;
            if (m.equals("CALL")) {
                Function c = callee(i);
                if (c != null) calls.add(c);
            }
            if (m.equals("RET")) break;
            if (m.equals("JMP")) {
                Address t = i.getFlows().length == 1 ? i.getFlows()[0] : null;
                if (vt != null || t == null || owner == null || !owner.getBody().contains(t)) break;
                i = getInstructionAt(t);
                continue;
            }
            i = i.getNext();
        }
        if (vt != null) return new String[] { vt, "case" };
        for (Function c : calls) {
            if (c.getEntryPoint().getOffset() == 0x142c835e0L) continue; // operator new
            String v = lastZeroStoreVt(c, 0);
            if (v != null) return new String[] { v, "ctor " + hex(c.getEntryPoint()) };
        }
        return new String[] { null, "no vtable (calls " + calls + ")" };
    }

    Map<String, String> leaRegs = new HashMap<>();

    String zeroStoreVt(Instruction i) {
        String m = i.getMnemonicString();
        if (m.equals("LEA") && i.getRegister(0) != null) {
            Address t = dataRef(i);
            if (t != null && isVtable(t)) leaRegs.put(i.getRegister(0).getName(), hex(t));
            else leaRegs.remove(i.getRegister(0).getName());
            return null;
        }
        if (m.equals("MOV") && i.getNumOperands() == 2) {
            Object[] dst = i.getOpObjects(0);
            if (dst.length == 1 && dst[0] instanceof Register && i.toString().matches("MOV qword ptr \\[\\w+\\],\\w+")) {
                Object[] src = i.getOpObjects(1);
                if (src.length == 1 && src[0] instanceof Register) return leaRegs.get(((Register) src[0]).getName());
            }
            Address t = dataRef(i);
            if (t != null && isVtable(t) && i.toString().matches("MOV qword ptr \\[\\w+\\],.*")) return hex(t);
        }
        return null;
    }

    String lastZeroStoreVt(Function f, int depth) {
        String vt = null;
        leaRegs.clear();
        List<Function> calls = new ArrayList<>();
        for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true)) {
            String v = zeroStoreVt(i);
            if (v != null) vt = v;
            if (i.getMnemonicString().equals("CALL")) { Function c = callee(i); if (c != null) calls.add(c); }
        }
        if (vt == null && depth < 2)
            for (Function c : calls) if ((vt = lastZeroStoreVt(c, depth + 1)) != null) break;
        return vt;
    }

    Function callee(Instruction i) {
        for (Reference r : i.getReferencesFrom())
            if (r.getReferenceType().isCall()) {
                Function c = getFunctionAt(r.getToAddress());
                if (c != null && c.isThunk()) c = c.getThunkedFunction(true);
                return c;
            }
        return null;
    }

    Address dataRef(Instruction i) {
        for (int op = 0; op < i.getNumOperands(); op++)
            for (Reference r : i.getOperandReferences(op)) if (r.getReferenceType().isData()) return r.getToAddress();
        return null;
    }

    boolean isVtable(Address a) {
        try {
            var b = mem.getBlock(a);
            if (b == null || b.isExecute() || a.getOffset() % 8 != 0) return false;
            for (int k = 0; k < 3; k++) {
                var c = mem.getBlock(toAddr(mem.getLong(a.add(8L * k))));
                if (c == null || !c.isExecute()) return false;
            }
            return true;
        } catch (Exception e) { return false; }
    }

    // ---- summaries ----

    Summary summarize(Function f, int readerArg) {
        String key = hex(f.getEntryPoint()) + (readerArg == 1 ? "" : "@" + readerArg);
        Summary s = memo.get(key);
        if (s != null) return s;
        s = new Summary();
        s.key = key; s.func = hex(f.getEntryPoint()); s.name = f.getName(); s.readerArg = readerArg;
        memo.put(key, s);
        inProgress.add(key);
        try { build(f, readerArg, s); }
        catch (Exception e) { s.failed = true; s.lines.add("// summary failed: " + e); }
        inProgress.remove(key);
        return s;
    }

    void build(Function f, int readerArg, Summary s) throws Exception {
        DecompileResults r = di.decompileFunction(f, 90, monitor);
        HighFunction h = r.getHighFunction();
        if (!r.decompileCompleted() || h == null) { s.failed = true; s.lines.add("// decompile failed: " + r.getErrorMessage()); return; }
        if (withC) s.c = r.getDecompiledFunction().getC();
        Set<Varnode> reader = new HashSet<>(), self = new HashSet<>();
        seed(h, readerArg, reader);
        seed(h, 0, self);
        // Helpers that return their reader (chained calls) hand it back: grow the reader set until it is stable.
        Map<PcodeOp, Ev> evs;
        int before, iter = 0;
        do { before = reader.size(); evs = events(h, reader, self); } while (reader.size() > before && ++iter < 6);
        Iterator<PcodeOpAST> ri = h.getPcodeOps();
        while (ri.hasNext()) {
            PcodeOpAST op = ri.next();
            if (op.getOpcode() == PcodeOp.RETURN && op.getNumInputs() > 1 && reader.contains(op.getInput(1))) s.returnsReader = true;
        }
        for (Ev e : evs.values())
            if (e.callee == null || inProgress.contains(hex(e.callee.getEntryPoint()) + (e.calleeArg == 1 ? "" : "@" + e.calleeArg))
                || summarize(e.callee, e.calleeArg).reads) s.reads = true;
        // Lines -> tree.
        List<ClangLine> lines = DecompilerUtils.toLines(r.getCCodeMarkup());
        Node root = null;
        Deque<Node> st = new ArrayDeque<>();
        StringBuilder tb = new StringBuilder();
        List<Ev> le = new ArrayList<>();
        PcodeOp cbranch = null;
        for (ClangLine ln : lines) {
            if (tb.length() > 0) tb.append(' ');
            for (ClangToken t : ln.getAllTokens()) {
                tb.append(t.getText());
                PcodeOp op = t.getPcodeOp();
                if (op != null) {
                    Ev e = evs.get(op);
                    if (e != null && !le.contains(e)) le.add(e);
                    if (op.getOpcode() == PcodeOp.CBRANCH) cbranch = op;
                }
            }
            String t = tb.toString().trim().replaceAll("[ 	]+", " ");
            // A statement the decompiler wrapped continues on the next line.
            if (!t.isEmpty() && !t.startsWith("//") && (!t.matches(".*[;{}:]$|.*[*]/$") || depthOf(t) > 0)) continue;
            List<Ev> lineEvs = le;
            PcodeOp lineCb = cbranch;
            tb.setLength(0);
            le = new ArrayList<>();
            cbranch = null;
            if (lineEvs.isEmpty() && t.isEmpty()) continue;
            if (root == null) {
                if (t.endsWith("{")) { root = new Node("", false); st.push(root); }
                continue;
            }
            if (st.isEmpty()) break;
            if (t.startsWith("}")) {
                Node closed = st.pop();
                String rest = t.substring(1).trim();
                if (rest.startsWith("while")) closed.header = closed.header + " ... " + rest.replaceAll(";$", "");
                if (st.isEmpty()) break;
                if (rest.endsWith("{")) {
                    Node n = new Node(rest.substring(0, rest.length() - 1).trim(), false);
                    st.peek().kids.add(n);
                    st.push(n);
                }
                continue;
            }
            if (t.endsWith("{")) {
                // Only an if can be a bounds check (a loop header can carry the CBRANCH of a check inside it).
                Node n = new Node(t.substring(0, t.length() - 1).trim(),
                    t.startsWith("if ") && lineCb != null && isGuard(lineCb, reader));
                st.peek().kids.add(n);
                st.push(n);
                if (!lineEvs.isEmpty()) n.headerEvs = lineEvs;
                continue;
            }
            if (!lineEvs.isEmpty()) { st.peek().kids.add(new Leaf(t, lineEvs, true)); continue; }
            if (t.matches("(goto|break|continue|return)\\b.*|LAB_\\w+:|case .*:|default:|if \\(.*\\) (goto|break|return)\\b.*"))
            {
                Leaf l = new Leaf(t, lineEvs, false);
                l.guard = lineCb != null && isGuard(lineCb, reader);
                st.peek().kids.add(l);
            }
        }
        if (root == null) { s.failed = true; return; }
        gotoSkips(root);
        if (withC) { StringBuilder tb2 = new StringBuilder(); dumpTree(root, 0, tb2); s.c = s.c + "\n// tree\n" + tb2; }
        int[] min = { 0 };
        boolean[] unk = { false };
        render(root, 0, true, s, min, unk);
        s.min = min[0];
        s.unknownMin = unk[0];
        for (Ev e : evs.values()) if (e.callee != null) s.subs.add(hex(e.callee.getEntryPoint()) + (e.calleeArg == 1 ? "" : "@" + e.calleeArg));
    }

    static class Node {
        String header; boolean guard; List<Object> kids = new ArrayList<>(); List<Ev> headerEvs;
        Node(String h, boolean g) { header = h; guard = g; }
    }

    void dumpTree(Object o, int d, StringBuilder b) {
        if (o instanceof Leaf) { Leaf l = (Leaf) o; b.append(ind(d)).append(l.strong ? "S " : "w ").append(l.guard ? "G " : "").append(l.text).append("\n"); return; }
        Node n = (Node) o;
        b.append(ind(d)).append("[").append(n.guard ? "G " : "").append(n.headerEvs != null ? "E " : "").append(n.header).append("]\n");
        for (Object k : n.kids) dumpTree(k, d + 1, b);
    }

    static class Leaf {
        String text; List<Ev> evs; boolean strong, guard;
        Leaf(String t, List<Ev> e, boolean s) { text = t; evs = e; strong = s; }
    }

    static final Pattern IF_GOTO = Pattern.compile("if \\((.*)\\) goto (LAB_\\w+);");

    static String negate(String c) {
        if (c.endsWith(" == 0") && depthOf(c.substring(0, c.length() - 5)) == 0) return c.substring(0, c.length() - 5) + " != 0";
        if (c.endsWith(" != 0") && depthOf(c.substring(0, c.length() - 5)) == 0) return c.substring(0, c.length() - 5) + " == 0";
        return "!(" + c + ")";
    }

    /** `if (c) goto L; ... L:` in one block skips the statements between: make them a block "if (!(c))". */
    void gotoSkips(Node n) {
        for (int i = 0; i < n.kids.size(); i++) {
            Object k = n.kids.get(i);
            if (!(k instanceof Leaf) || ((Leaf) k).strong) continue;
            Matcher m = IF_GOTO.matcher(((Leaf) k).text);
            if (!m.matches()) continue;
            int j = i + 1;
            while (j < n.kids.size() && !(n.kids.get(j) instanceof Leaf && ((Leaf) n.kids.get(j)).text.equals(m.group(2) + ":"))) j++;
            if (j >= n.kids.size()) continue;
            Node w = new Node("if (" + negate(m.group(1)) + ")", ((Leaf) k).guard);
            w.kids.addAll(n.kids.subList(i + 1, j));
            n.kids.subList(i, j).clear();
            n.kids.add(i, w);
        }
        for (Object k : n.kids) if (k instanceof Node) gotoSkips((Node) k);
    }

    boolean hasStrong(Object o) {
        if (o instanceof Leaf) {
            Leaf l = (Leaf) o;
            if (!l.strong) return false;
            for (Ev e : l.evs) { // a call that passes the reader to a function that reads nothing is not a read
                if (e.callee == null) return true;
                String key = hex(e.callee.getEntryPoint()) + (e.calleeArg == 1 ? "" : "@" + e.calleeArg);
                if (inProgress.contains(key) || summarize(e.callee, e.calleeArg).reads) return true;
            }
            return false;
        }
        Node n = (Node) o;
        if (n.headerEvs != null) return true;
        for (Object k : n.kids) if (hasStrong(k)) return true;
        return false;
    }

    void render(Node n, int depth, boolean uncond, Summary s, int[] min, boolean[] unk) {
        Object prev = null;
        for (Object k : n.kids) {
            Object before = prev;
            prev = k;
            if (k instanceof Node && ((Node) k).header.startsWith("else") && before instanceof Node) {
                Node b = (Node) before;
                if (b.guard) { render((Node) k, depth, uncond, s, min, unk); continue; } // else of a bounds check
                if (!hasStrong(b) && hasStrong(k)) s.lines.add(ind(depth) + clip(b.header) + " { }");
            }
            if (!hasStrong(k)) {
                if (k instanceof Leaf && depth > 0) s.lines.add(ind(depth) + "// " + ((Leaf) k).text);
                continue;
            }
            if (k instanceof Leaf) {
                Leaf l = (Leaf) k;
                s.lines.add(ind(depth) + descs(l.evs, uncond, min, unk) + "    // " + clip(l.text));
            } else {
                Node c = (Node) k;
                if (c.guard) { render(c, depth, uncond, s, min, unk); continue; }
                String hdr = c.header;
                if (c.headerEvs != null) hdr = descs(c.headerEvs, uncond, min, unk) + " in: " + hdr;
                s.lines.add(ind(depth) + clip(hdr) + " {");
                render(c, depth + 1, false, s, min, unk);
                s.lines.add(ind(depth) + "}");
            }
        }
    }

    String descs(List<Ev> l, boolean uncond, int[] min, boolean[] unk) {
        StringBuilder b = new StringBuilder();
        for (Ev e : l) {
            b.append(b.length() == 0 ? "" : "; ");
            String d = e.desc;
            int m = e.min;
            if (e.callee != null) {
                String key = hex(e.callee.getEntryPoint()) + (e.calleeArg == 1 ? "" : "@" + e.calleeArg);
                if (inProgress.contains(key)) { m = -1; d += " (recursive)"; }
                else {
                    Summary cs = summarize(e.callee, e.calleeArg);
                    m = cs.min;
                    if (cs.unknownMin) unk[0] |= uncond;
                    if (!cs.reads) d += " (no reads)";
                    else d += " [min " + cs.min + (cs.unknownMin ? "+" : "") + "]";
                }
            }
            b.append(d);
            if (uncond) { if (m < 0) unk[0] = true; else min[0] += m; }
        }
        return b.toString();
    }

    static int depthOf(String t) {
        int d = 0;
        for (char c : t.toCharArray()) { if (c == '(') d++; else if (c == ')') d--; }
        return d;
    }

    static String ind(int d) { return "  ".repeat(d); }
    static String clip(String t) { return t.length() > 140 ? t.substring(0, 137) + "..." : t; }

    // ---- p-code events ----

    void seed(HighFunction h, int idx, Set<Varnode> set) {
        LocalSymbolMap lsm = h.getLocalSymbolMap();
        if (idx < lsm.getNumParams()) {
            HighSymbol p = lsm.getParamSymbol(idx);
            if (p != null && p.getHighVariable() != null) Collections.addAll(set, p.getHighVariable().getInstances());
        }
        if (set.isEmpty() && idx < ARG_REGS.length) {
            Register reg = currentProgram.getRegister(ARG_REGS[idx]);
            Iterator<PcodeOpAST> it = h.getPcodeOps();
            while (it.hasNext()) for (Varnode v : it.next().getInputs())
                if (v.isInput() && v.isRegister() && v.getAddress().equals(reg.getAddress())) set.add(v);
        }
        propagate(set, new ArrayList<>(set));
    }

    /** Add the copies, casts and phi nodes of `from` to the reader set. */
    void propagate(Set<Varnode> set, Collection<Varnode> from) {
        // Propagate through copies, casts and phi nodes.
        Deque<Varnode> w = new ArrayDeque<>(from);
        while (!w.isEmpty()) {
            Varnode v = w.pop();
            Iterator<PcodeOp> d = v.getDescendants();
            while (d.hasNext()) {
                PcodeOp op = d.next();
                int oc = op.getOpcode();
                if ((oc == PcodeOp.COPY || oc == PcodeOp.CAST || oc == PcodeOp.MULTIEQUAL || oc == PcodeOp.INDIRECT)
                    && op.getOutput() != null && set.add(op.getOutput())) w.push(op.getOutput());
            }
        }
    }

    /** (base varnode in set, constant offset) of a pointer, or null. */
    Long offsetFrom(Varnode p, Set<Varnode> base, int depth) {
        if (p == null || depth > 6) return null;
        if (base.contains(p)) return 0L;
        PcodeOp d = p.getDef();
        if (d == null) return null;
        switch (d.getOpcode()) {
            case PcodeOp.COPY: case PcodeOp.CAST: return offsetFrom(d.getInput(0), base, depth + 1);
            case PcodeOp.PTRSUB: case PcodeOp.INT_ADD: {
                if (!d.getInput(1).isConstant()) return null;
                Long o = offsetFrom(d.getInput(0), base, depth + 1);
                return o == null ? null : o + d.getInput(1).getOffset();
            }
            case PcodeOp.PTRADD: {
                if (!d.getInput(1).isConstant() || !d.getInput(2).isConstant()) return null;
                Long o = offsetFrom(d.getInput(0), base, depth + 1);
                return o == null ? null : o + d.getInput(1).getOffset() * d.getInput(2).getOffset();
            }
        }
        return null;
    }

    boolean dependsOnLoad(Varnode v, Set<Varnode> base, long off, int depth) {
        if (v == null || depth > 5) return false;
        PcodeOp d = v.getDef();
        if (d == null) return false;
        if (d.getOpcode() == PcodeOp.LOAD) {
            Long o = offsetFrom(d.getInput(1), base, 0);
            if (o != null && o == off) return true;
        }
        for (Varnode i : d.getInputs()) if (dependsOnLoad(i, base, off, depth + 1)) return true;
        return false;
    }

    boolean isGuard(PcodeOp cb, Set<Varnode> reader) {
        return dependsOnLoad(cb.getInput(1), reader, 8, 0);
    }

    /** pos = base + c: the compiler often stores pos+4 and later pos+8 from the same base, so a read's size is c minus
     *  the next lower constant stored from that base. */
    static Varnode strip(Varnode v) {
        PcodeOp d = v.getDef();
        while (d != null && (d.getOpcode() == PcodeOp.COPY || d.getOpcode() == PcodeOp.CAST)) { v = d.getInput(0); d = v.getDef(); }
        return v;
    }

    Map<PcodeOp, Ev> events(HighFunction h, Set<Varnode> reader, Set<Varnode> self) {
        Map<PcodeOp, Ev> m = new HashMap<>();
        Map<Varnode, TreeSet<Long>> stored = new HashMap<>();
        Iterator<PcodeOpAST> it0 = h.getPcodeOps();
        while (it0.hasNext()) {
            PcodeOpAST op = it0.next();
            if (op.getOpcode() != PcodeOp.STORE) continue;
            Long o = offsetFrom(op.getInput(1), reader, 0);
            if (o == null || o != 0x10) continue;
            PcodeOp d = strip(op.getInput(2)).getDef();
            if (d != null && (d.getOpcode() == PcodeOp.INT_ADD || d.getOpcode() == PcodeOp.PTRADD) && d.getInput(1).isConstant())
                stored.computeIfAbsent(strip(d.getInput(0)), k -> new TreeSet<>()).add(d.getInput(1).getOffset());
        }
        Iterator<PcodeOpAST> it = h.getPcodeOps();
        while (it.hasNext()) {
            PcodeOpAST op = it.next();
            int oc = op.getOpcode();
            if (oc == PcodeOp.STORE) {
                Long o = offsetFrom(op.getInput(1), reader, 0);
                if (o == null || o != 0x10) continue;
                Varnode val = op.getInput(2);
                PcodeOp d = val.getDef();
                while (d != null && (d.getOpcode() == PcodeOp.COPY || d.getOpcode() == PcodeOp.CAST)) d = d.getInput(0).getDef();
                Ev e = new Ev();
                if (d != null && (d.getOpcode() == PcodeOp.INT_ADD || d.getOpcode() == PcodeOp.PTRADD) && d.getInput(1).isConstant()) {
                    long c = d.getInput(1).getOffset();
                    Long lower = stored.getOrDefault(strip(d.getInput(0)), new TreeSet<>()).lower(c);
                    int n = (int) (lower == null ? c : c - lower);
                    e.min = n;
                    e.desc = typeName(n) + dest(h, strip(d.getInput(0)), lower == null ? 0 : lower, n, self);
                } else if (d != null && d.getOpcode() == PcodeOp.INT_ADD) {
                    e.min = -1;
                    e.desc = "bytes[var]";
                } else continue; // pos = an earlier position (branch merge, rewind): no new bytes
                m.put(op, e);
            } else if (oc == PcodeOp.CALL || oc == PcodeOp.CALLIND) {
                int arg = -1;
                for (int i = 1; i < op.getNumInputs(); i++) if (reader.contains(op.getInput(i))) { arg = i - 1; break; }
                if (arg < 0) continue;
                Ev e = new Ev();
                if (oc == PcodeOp.CALLIND) {
                    e.min = -1;
                    e.desc = "vcall " + slotOf(op.getInput(0)) + "(reader arg " + arg + ")";
                    m.put(op, e);
                    continue;
                }
                Address t = op.getInput(0).getAddress();
                Function c = getFunctionAt(t);
                if (c != null && c.isThunk()) { c = c.getThunkedFunction(true); t = c.getEntryPoint(); }
                long ta = t.getOffset();
                boolean ign = false;
                for (long x : IGNORE) ign |= x == ta;
                if (ign) continue;
                Object[] p = prims.get(ta);
                if (p != null) {
                    String k = (String) p[3];
                    e.min = (Integer) p[2];
                    e.desc = (String) p[1];
                    if ((k.startsWith("s") || k.equals("b")) && op.getNumInputs() > 3) {
                        Varnode n = op.getInput(3);
                        if (n.isConstant()) { e.min = (int) n.getOffset(); e.desc += n.getOffset(); }
                        else { e.min = -1; e.desc += "[var]"; }
                        Long o = offsetFrom(op.getInput(2), self, 0);
                        if (o != null) e.desc += " " + (k.equals("sw") ? "← " : "→ ") + "+0x" + Long.toHexString(o);
                    } else if (k.equals("r") && op.getNumInputs() > arg + 2) {
                        Long o = offsetFrom(op.getInput(arg + 2), self, 0);
                        if (o != null) e.desc += " → +0x" + Long.toHexString(o);
                    }
                    if (k.equals("b") && op.getOutput() != null && op.getOutput().getSize() == 8 && reader.add(op.getOutput()))
                        propagate(reader, List.of(op.getOutput()));
                    if (k.equals("r") && op.getOutput() != null) {
                        String dd = sinkOf(op.getOutput(), self);
                        if (dd != null) e.desc += dd;
                    }
                } else {
                    e.callee = c;
                    e.calleeArg = arg;
                    if (c != null && op.getOutput() != null && !reader.contains(op.getOutput())
                        && !inProgress.contains(hex(c.getEntryPoint()) + (arg == 1 ? "" : "@" + arg))
                        && summarize(c, arg).returnsReader) {
                        reader.add(op.getOutput());
                        propagate(reader, List.of(op.getOutput()));
                    }
                    e.min = -1;
                    e.desc = "sub " + hex(t) + (arg == 1 ? "" : "(reader arg " + arg + ")");
                    if (c == null) { e.callee = null; e.desc += " (no function)"; }
                }
                m.put(op, e);
            }
        }
        return m;
    }

    String slotOf(Varnode target) {
        PcodeOp d = target.getDef();
        while (d != null && (d.getOpcode() == PcodeOp.COPY || d.getOpcode() == PcodeOp.CAST)) d = d.getInput(0).getDef();
        if (d != null && d.getOpcode() == PcodeOp.LOAD) {
            PcodeOp a = d.getInput(1).getDef();
            while (a != null && (a.getOpcode() == PcodeOp.COPY || a.getOpcode() == PcodeOp.CAST)) a = a.getInput(0).getDef();
            if (a == null) return "[vt+0]";
            if ((a.getOpcode() == PcodeOp.INT_ADD || a.getOpcode() == PcodeOp.PTRSUB) && a.getInput(1).isConstant())
                return "[vt+0x" + Long.toHexString(a.getInput(1).getOffset()) + "]";
            if (a.getOpcode() == PcodeOp.PTRADD && a.getInput(1).isConstant() && a.getInput(2).isConstant())
                return "[vt+0x" + Long.toHexString(a.getInput(1).getOffset() * a.getInput(2).getOffset()) + "]";
        }
        return "[?]";
    }

    static String typeName(int n) {
        switch (n) { case 1: return "u8"; case 2: return "u16"; case 4: return "u32"; case 8: return "u64"; default: return "bytes[" + n + "]"; }
    }

    /** Where an inline read of n bytes at base+start goes: "→ +0xoff" (a field of this) or " (flags)". */
    String dest(HighFunction h, Varnode base, long start, int n, Set<Varnode> self) {
        Iterator<PcodeOpAST> it = h.getPcodeOps();
        while (it.hasNext()) {
            PcodeOpAST op = it.next();
            if (op.getOpcode() == PcodeOp.LOAD && op.getOutput() != null && op.getOutput().getSize() == n) {
                Long k = posOffset(op.getInput(1), base, 0);
                if (k == null || k != start) continue;
                String s = sinkOf(op.getOutput(), self);
                return s == null ? "" : s;
            }
            if (op.getOpcode() == PcodeOp.CALL && op.getNumInputs() >= 4) { // memcpy(dst, data + pos, n)
                Function c = getFunctionAt(op.getInput(0).getAddress());
                if (c == null || !c.getName().toLowerCase().matches(".*mem(cpy|move).*")) continue;
                Long k = posOffset(op.getInput(2), base, 0);
                if (k == null || k != start) continue;
                Long o = offsetFrom(op.getInput(1), self, 0);
                return o == null ? "" : " → +0x" + Long.toHexString(o);
            }
        }
        return "";
    }

    /** k such that v = (data pointer) + base + k, or null. */
    Long posOffset(Varnode v, Varnode base, int depth) {
        if (v == null || depth > 6) return null;
        if (v.equals(base)) return 0L;
        PcodeOp d = v.getDef();
        if (d == null) return null;
        int oc = d.getOpcode();
        if (oc == PcodeOp.COPY || oc == PcodeOp.CAST || oc == PcodeOp.INT_ZEXT) return posOffset(d.getInput(0), base, depth + 1);
        if (oc == PcodeOp.INT_ADD || oc == PcodeOp.PTRSUB || oc == PcodeOp.PTRADD) {
            Varnode a = d.getInput(0), b = d.getInput(1);
            long scale = oc == PcodeOp.PTRADD && d.getInput(2).isConstant() ? d.getInput(2).getOffset() : 1;
            if (b.isConstant()) { Long k = posOffset(a, base, depth + 1); return k == null ? null : k + b.getOffset() * scale; }
            if (scale != 1) return null;
            Long k = posOffset(a, base, depth + 1);
            return k != null ? k : posOffset(b, base, depth + 1);
        }
        return null;
    }

    boolean usesVarnode(Varnode v, Set<Varnode> s, int depth) {
        if (v == null || depth > 4) return false;
        if (s.contains(v)) return true;
        PcodeOp d = v.getDef();
        if (d == null) return false;
        for (Varnode i : d.getInputs()) if (usesVarnode(i, s, depth + 1)) return true;
        return false;
    }

    String sinkOf(Varnode v, Set<Varnode> self) {
        Deque<Varnode> w = new ArrayDeque<>();
        w.push(v);
        Set<Varnode> seen = new HashSet<>();
        boolean flags = false;
        for (int steps = 0; !w.isEmpty() && steps < 24; steps++) {
            Varnode x = w.pop();
            if (!seen.add(x)) continue;
            Iterator<PcodeOp> d = x.getDescendants();
            while (d.hasNext()) {
                PcodeOp op = d.next();
                int oc = op.getOpcode();
                if (oc == PcodeOp.STORE && op.getInput(2) == x) {
                    Long o = offsetFrom(op.getInput(1), self, 0);
                    if (o != null) return " → +0x" + Long.toHexString(o);
                } else if (oc == PcodeOp.INT_AND || oc == PcodeOp.INT_RIGHT || oc == PcodeOp.INT_SRIGHT) flags = true;
                else if (oc == PcodeOp.COPY || oc == PcodeOp.CAST || oc == PcodeOp.INT_ZEXT || oc == PcodeOp.INT_SEXT
                    || oc == PcodeOp.MULTIEQUAL || oc == PcodeOp.INDIRECT || oc == PcodeOp.PIECE || oc == PcodeOp.SUBPIECE) {
                    if (op.getOutput() != null) w.push(op.getOutput());
                }
            }
        }
        return flags ? " (flags)" : null;
    }

    // ---- json ----

    String hex(Address a) { return "0x" + Long.toHexString(a.getOffset()); }
    String hex(long v) { return "0x" + Long.toHexString(v); }

    static String jsList(Collection<String> l) {
        StringBuilder b = new StringBuilder("[");
        boolean f = true;
        for (String s : l) { b.append(f ? "" : ",").append(js(s)); f = false; }
        return b.append("]").toString();
    }

    static String js(String s) {
        if (s == null) return "null";
        StringBuilder b = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            if (c == '"' || c == '\\') b.append('\\').append(c);
            else if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
            else b.append(c);
        }
        return b.append('"').toString();
    }
}
