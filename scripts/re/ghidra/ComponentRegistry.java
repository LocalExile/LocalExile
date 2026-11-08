// Usage: -postScript ComponentRegistry.java <out.json>
// Walks every caller of the pool registration 0x140e35f40 (static initializers in 0x14006xxxx:
// `lea rdx,"Name"; lea rcx,pool; call`) and, for each registration, writes
//   name -> pool -> pool vtable -> pool users (the factory allocates from the pool) -> every vtable each user installs
//   (directly or through callees up to depth 2) -> subclass vtables (installed by functions that also install, or call
//   a function that installs, a found vtable) -> slot 17 (+0x88, the deserializer) and slot 20 (+0xa0).
// The component type id is the index of the name in the client's component-name table 0x142fe6f10 (read by
// 0x14247b130, the id -> name lookup of the "Error: Dependency" message).
// Live objects can use a subclass vtable (the player's Stats is 0x1435e7f88, the factory installs 0x1435ab340), so
// every vtable is recorded; scripts/re/frida/check-components.js and the Frida traces say which one is live.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class ComponentRegistry extends GhidraScript {
    static final long REGISTER = 0x140e35f40L;
    static final long NAME_TABLE = 0x142fe6f10L;
    Memory mem;
    Map<Address, Address> subBase = new HashMap<>();

    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        mem = currentProgram.getMemory();
        Map<String, Integer> typeIds = readNameTable();

        List<String> entries = new ArrayList<>();
        List<Reference> refs = new ArrayList<>();
        for (Reference r : getReferencesTo(toAddr(REGISTER))) if (r.getReferenceType().isCall()) refs.add(r);
        refs.sort(Comparator.comparing(Reference::getFromAddress));
        for (Reference r : refs) {
            Instruction call = getInstructionAt(r.getFromAddress());
            Function init = getFunctionContaining(r.getFromAddress());
            Address name = null, pool = null;
            Instruction p = call.getPrevious();
            for (int k = 0; k < 16 && p != null && (name == null || pool == null); k++, p = p.getPrevious()) {
                if (!p.getMnemonicString().equals("LEA")) continue;
                Register reg = p.getRegister(0);
                Address t = target(p, 1);
                if (reg == null || t == null) continue;
                if (reg.getName().equals("RDX") && name == null) name = t;
                if (reg.getName().equals("RCX") && pool == null) pool = t;
            }
            String nm = name == null ? null : readName(name);
            // Pool vtable: the first vtable stored after the call (`lea rax,vt; mov [pool],rax`).
            Address poolVt = null;
            Instruction n = call.getNext();
            for (int k = 0; k < 12 && n != null && poolVt == null; k++, n = n.getNext()) {
                Address t = target(n, 1);
                if (t != null && isVtable(t)) poolVt = t;
            }
            StringBuilder e = new StringBuilder();
            e.append("{\"name\":").append(js(nm));
            e.append(",\"typeId\":").append(nm != null && typeIds.containsKey(nm) ? typeIds.get(nm) : -1);
            e.append(",\"init\":").append(js(init == null ? null : hex(init.getEntryPoint())));
            e.append(",\"pool\":").append(js(pool == null ? null : hex(pool)));
            e.append(",\"poolVtable\":").append(js(poolVt == null ? null : hex(poolVt)));
            if (poolVt != null) e.append(",\"poolSlots\":").append(slots(poolVt, 12));
            // Pool users other than the initializer: the factory (allocates from the pool) and the atexit destructor.
            e.append(",\"users\":[");
            Set<Address> allVts = new LinkedHashSet<>();
            Set<Address> primary = new HashSet<>(); // each user's first full-size vtable, and their subclasses
            boolean first = true;
            if (pool != null) {
                Set<Function> users = new TreeSet<>(Comparator.comparing(Function::getEntryPoint));
                for (Reference pr : getReferencesTo(pool)) {
                    Function f = getFunctionContaining(pr.getFromAddress());
                    if (f != null && !f.equals(init)) users.add(f);
                }
                for (Function f : users) {
                    List<String[]> vts = new ArrayList<>();
                    collectVtables(f, 0, "", vts, new HashSet<>());
                    vts.removeIf(v -> v[0].equals(hex(toAddr(0))));
                    if (!first) e.append(",");
                    first = false;
                    e.append("{\"func\":").append(js(hex(f.getEntryPoint())));
                    e.append(",\"size\":").append(f.getBody().getNumAddresses());
                    e.append(",\"vtables\":[");
                    boolean f2 = true, gotPrimary = false;
                    for (String[] v : vts) {
                        Address va = toAddr(v[0]);
                        if (poolVt != null && va.equals(poolVt)) continue;
                        if (!gotPrimary && vtLen(va) > 17) { primary.add(va); gotPrimary = true; }
                        if (!f2) e.append(",");
                        f2 = false;
                        e.append("{\"vt\":").append(js(v[0])).append(",\"via\":").append(js(v[1])).append("}");
                        allVts.add(va);
                    }
                    e.append("]");
                    // The object's own vtable: the last one stored at [reg+0] in the factory, else in its first callee
                    // that stores one (the constructor; a base constructor stores first, the derived class last).
                    Address ov = lastObjectVt(f, 0);
                    if (ov != null && (poolVt == null || !ov.equals(poolVt))) {
                        primary.add(ov);
                        e.append(",\"objectVtable\":").append(js(hex(ov)));
                    }
                    e.append("}");
                }
            }
            e.append("]");
            // Subclasses: other vtables installed by functions that install a found vtable or call such a function.
            Map<Address, String> sub = new LinkedHashMap<>();
            for (Address v : new ArrayList<>(allVts)) findSubclasses(v, allVts, sub, poolVt, 0);
            for (boolean grew = true; grew; ) {
                grew = false;
                for (var s : subBase.entrySet())
                    if (primary.contains(s.getValue()) && primary.add(s.getKey())) grew = true;
            }
            e.append(",\"vtables\":[");
            first = true;
            for (Address v : allVts) {
                if (!first) e.append(",");
                first = false;
                e.append(vtJson(v, sub.get(v), primary.contains(v)));
            }
            for (var s : sub.entrySet()) {
                if (allVts.contains(s.getKey())) continue;
                e.append(first ? "" : ",");
                first = false;
                e.append(vtJson(s.getKey(), s.getValue(), primary.contains(s.getKey())));
            }
            e.append("]}");
            entries.add(e.toString());
        }
        PrintWriter out = new PrintWriter(new OutputStreamWriter(new FileOutputStream(a[0]), "UTF-8"));
        out.println("{\"generator\":\"scripts/re/ghidra/ComponentRegistry.java\",\"register\":\"0x140e35f40\",");
        out.println("\"typeTable\":" + tableJson(typeIds) + ",");
        out.println("\"registrations\":[");
        for (int i = 0; i < entries.size(); i++) out.println(entries.get(i) + (i + 1 < entries.size() ? "," : ""));
        out.println("]}");
        out.close();
        println("registrations: " + entries.size());
    }

    String vtJson(Address v, String subOf, boolean primary) throws Exception {
        int len = vtLen(v);
        StringBuilder b = new StringBuilder();
        b.append("{\"vt\":").append(js(hex(v))).append(",\"len\":").append(len).append(",\"primary\":").append(primary);
        if (subOf != null) b.append(",\"subclassVia\":").append(js(subOf));
        if (len > 17) {
            long s17 = mem.getLong(v.add(0x88));
            b.append(",\"slot17\":").append(js(hex(s17))).append(",\"slot17Kind\":").append(js(kind(toAddr(s17))));
        }
        if (len > 20) b.append(",\"slot20\":").append(js(hex(mem.getLong(v.add(0xa0)))));
        b.append(",\"installers\":[");
        boolean first = true;
        for (Function f : installers(v)) {
            b.append(first ? "" : ",").append(js(hex(f.getEntryPoint())));
            first = false;
        }
        b.append("]}");
        return b.toString();
    }

    /** "ret" (returns at once: no data), "fwd20" (calls or jumps through +0xa0, i.e. slot 20) or "code". */
    String kind(Address f) {
        Instruction i = getInstructionAt(f);
        if (i == null) return "noinsn";
        if (i.getMnemonicString().equals("RET")) return "ret";
        for (int k = 0; k < 24 && i != null; k++, i = i.getNext()) {
            String m = i.getMnemonicString();
            if ((m.equals("CALL") || m.equals("JMP")) && i.toString().contains("+ 0xa0]")) return "fwd20";
            if (m.equals("RET")) break;
        }
        return "code";
    }

    void findSubclasses(Address v, Set<Address> known, Map<Address, String> sub, Address poolVt, int depth) throws Exception {
        if (depth > 2) return;
        Set<Function> fs = installers(v);
        Set<Function> cand = new LinkedHashSet<>(fs);
        for (Function f : fs) {
            int n = 0;
            for (Reference r : getReferencesTo(f.getEntryPoint())) {
                if (!r.getReferenceType().isCall() || ++n > 64) continue;
                Function c = getFunctionContaining(r.getFromAddress());
                if (c != null) cand.add(c);
            }
        }
        for (Function g : cand) {
            if (g.getBody().getNumAddresses() > 4000) continue;
            for (Address w : directVtables(g)) {
                if (w.equals(v) || known.contains(w) || sub.containsKey(w) || w.equals(poolVt)) continue;
                if (!sameFamily(v, w)) continue;
                sub.put(w, hex(v) + " by " + hex(g.getEntryPoint()));
                subBase.put(w, v);
                findSubclasses(w, known, sub, poolVt, depth + 1);
            }
        }
    }

    /** Consecutive code pointers from v. Adjacent vtables can run together, so a short class may show a bogus slot 17. */
    int vtLen(Address v) throws Exception {
        int n = 0;
        while (n < 400 && isCode(toAddr(mem.getLong(v.add(8L * n))))) {
            n++;
        }
        return n;
    }

    /** Tiny shared stubs (ret, xor eax,eax; ret, ...) say nothing about a class family. */
    boolean trivial(long f) {
        Function fn = getFunctionAt(toAddr(f));
        int n = 0;
        for (Reference r : getReferencesTo(toAddr(f))) if (++n > 150) return true;
        return false;
    }

    /** A subclass keeps most of the base's slots (destructors, type id, dependencies). Count slots 2..24 shared. */
    boolean sameFamily(Address base, Address w) throws Exception {
        int same = 0, n = 0;
        for (int k = 2; k < 25; k++) {
            long x = mem.getLong(base.add(8L * k)), y = mem.getLong(w.add(8L * k));
            if (!isCode(toAddr(x)) || !isCode(toAddr(y))) break;
            n++;
            if (x == y && !trivial(x)) same++;
        }
        return n >= 18 && same >= 3;
    }

    Set<Function> installers(Address v) {
        Set<Function> fs = new TreeSet<>(Comparator.comparing(Function::getEntryPoint));
        for (Reference r : getReferencesTo(v)) {
            Function f = getFunctionContaining(r.getFromAddress());
            if (f != null) fs.add(f);
        }
        return fs;
    }

    void collectVtables(Function f, int depth, String via, List<String[]> out, Set<Function> seen) throws Exception {
        if (!seen.add(f) || f.getBody().getNumAddresses() > 6000) return;
        for (Address v : directVtables(f)) {
            boolean dup = false;
            for (String[] o : out) if (o[0].equals(hex(v))) dup = true;
            if (!dup) out.add(new String[] { hex(v), via.isEmpty() ? "direct" : via });
        }
        if (depth >= 2) return;
        for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true)) {
            String m = i.getMnemonicString();
            if (!m.equals("CALL") && !m.equals("JMP")) continue;
            for (Reference r : i.getReferencesFrom()) {
                if (!r.getReferenceType().isCall() && !r.getReferenceType().isJump()) continue;
                Function c = getFunctionAt(r.getToAddress());
                if (c == null || c.isThunk()) continue;
                collectVtables(c, depth + 1, (via.isEmpty() ? "" : via + ">") + hex(c.getEntryPoint()), out, seen);
            }
        }
    }

    Address lastObjectVt(Function f, int depth) {
        Map<String, Address> lea = new HashMap<>();
        Address last = null;
        List<Function> calls = new ArrayList<>();
        for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true)) {
            String m = i.getMnemonicString();
            if (m.equals("LEA") && i.getRegister(0) != null) {
                Address t = target(i, 1);
                if (t != null && isVtable(t)) lea.put(i.getRegister(0).getName(), t);
                else lea.remove(i.getRegister(0).getName());
            } else if (m.equals("MOV") && i.toString().matches("MOV qword ptr \\[\\w+\\],\\w+")) {
                Object[] src = i.getOpObjects(1);
                if (src.length == 1 && src[0] instanceof Register && lea.containsKey(((Register) src[0]).getName()))
                    last = lea.get(((Register) src[0]).getName());
            } else if (m.equals("CALL") || m.equals("JMP")) {
                for (Reference r : i.getReferencesFrom()) {
                    if (!r.getReferenceType().isCall() && !r.getReferenceType().isJump()) continue;
                    Function c = getFunctionAt(r.getToAddress());
                    if (c != null && !c.isThunk() && c.getBody().getNumAddresses() > 24) calls.add(c);
                }
            }
        }
        if (last == null && depth < 2)
            for (Function c : calls) if ((last = lastObjectVt(c, depth + 1)) != null) break;
        return last;
    }

    List<Address> directVtables(Function f) throws Exception {
        List<Address> l = new ArrayList<>();
        for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true)) {
            String m = i.getMnemonicString();
            if (!m.equals("LEA") && !m.equals("MOV")) continue;
            for (int op = 0; op < i.getNumOperands(); op++) {
                Address t = target(i, op);
                if (t != null && isVtable(t) && !l.contains(t)) l.add(t);
            }
        }
        return l;
    }

    Address target(Instruction i, int op) {
        if (i == null || op >= i.getNumOperands()) return null;
        for (Reference r : i.getOperandReferences(op))
            if (r.getReferenceType().isData() || r.getReferenceType() == RefType.DATA) return r.getToAddress();
        return null;
    }

    boolean isCode(Address a) {
        MemoryBlock b = mem.getBlock(a);
        return b != null && b.isExecute();
    }

    boolean isVtable(Address a) {
        try {
            MemoryBlock b = mem.getBlock(a);
            if (b == null || b.isExecute() || !b.isInitialized()) return false;
            if (a.getOffset() % 8 != 0) return false;
            for (int k = 0; k < 3; k++) if (!isCode(toAddr(mem.getLong(a.add(8L * k))))) return false;
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    String slots(Address v, int n) throws Exception {
        StringBuilder b = new StringBuilder("[");
        for (int k = 0; k < n; k++) b.append(k == 0 ? "" : ",").append(js(hex(mem.getLong(v.add(8L * k)))));
        return b.append("]").toString();
    }

    String readName(Address a) throws Exception {
        StringBuilder s = new StringBuilder();
        for (int k = 0; k < 128; k++) {
            int c = mem.getByte(a.add(k)) & 0xff;
            if (c == 0) break;
            if (c < 0x20 || c > 0x7e) return null;
            s.append((char) c);
        }
        return s.toString();
    }

    String readWide(Address a) throws Exception {
        StringBuilder s = new StringBuilder();
        for (int k = 0; k < 128; k++) {
            int c = mem.getShort(a.add(2L * k)) & 0xffff;
            if (c == 0) break;
            if (c < 0x20 || c > 0x7e) return null;
            s.append((char) c);
        }
        return s.length() == 0 ? null : s.toString();
    }

    Map<String, Integer> readNameTable() throws Exception {
        Map<String, Integer> m = new LinkedHashMap<>();
        for (int i = 0; i < 1024; i++) {
            Address p = toAddr(mem.getLong(toAddr(NAME_TABLE + 8L * i)));
            MemoryBlock b = mem.getBlock(p);
            if (b == null || b.isExecute()) break;
            String s = readWide(p);
            if (s == null) break;
            m.put(s, i);
        }
        return m;
    }

    String tableJson(Map<String, Integer> m) {
        StringBuilder b = new StringBuilder("[");
        boolean first = true;
        for (String k : m.keySet()) {
            b.append(first ? "" : ",").append(js(k));
            first = false;
        }
        return b.append("]").toString();
    }

    String hex(Address a) { return "0x" + Long.toHexString(a.getOffset()); }
    String hex(long v) { return "0x" + Long.toHexString(v); }

    static String js(String s) {
        if (s == null) return "null";
        StringBuilder b = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            if (c == '"' || c == '\\') b.append('\\');
            b.append(c);
        }
        return b.append('"').toString();
    }
}
