// Usage: analyzeHeadless <projdir> <proj> -process <program> -noanalysis -readOnly -scriptPath <dir> -postScript FindAo.java <needle> <outfile>
// Finds the ASCII string <needle> in the program, then decompiles every function that references it.
// The .ao paths of the effects are strings in the binary, so this is how one finds the code that creates an effect.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class FindAo extends GhidraScript {
    @Override
    public void run() throws Exception {
        String needle = getScriptArgs()[0];
        String outPath = getScriptArgs()[1];
        PrintWriter out = new PrintWriter(new FileWriter(outPath));
        Memory mem = currentProgram.getMemory();
        byte[] pat = needle.getBytes("UTF-8");
        List<Address> hits = new ArrayList<>();
        Address a = mem.findBytes(mem.getMinAddress(), pat, null, true, null);
        while (a != null) {
            hits.add(a);
            a = mem.findBytes(a.add(1), pat, null, true, null);
        }
        out.println("// program: " + currentProgram.getName());
        out.println("// string hits for '" + needle + "': " + hits.size());
        ReferenceManager rm = currentProgram.getReferenceManager();
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Function> fns = new LinkedHashSet<>();
        for (Address h : hits) {
            out.println("//   " + h);
            for (Reference r : rm.getReferencesTo(h)) {
                Function f = getFunctionContaining(r.getFromAddress());
                if (f != null) fns.add(f);
                else out.println("//     ref from " + r.getFromAddress() + " (no function)");
            }
        }
        for (Function f : fns) {
            out.println("// ===== " + f.getName() + " @ " + f.getEntryPoint());
            DecompileResults r = di.decompileFunction(f, 90, monitor);
            if (r != null && r.decompileCompleted()) out.println(r.getDecompiledFunction().getC());
            else out.println("// decompile failed");
        }
        out.close();
    }
}
