// Usage: analyzeHeadless ... -postScript NightFragments.java <entries file, or "instructions"> <outdir>
// Creates a function at each listed address no function covers: code nothing calls directly (at-exit destructor stubs
// and the like) that auto-analysis and .pdata leave out. Writes <outdir>/fragments.log.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.nio.file.*;

public class NightFragments extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter log = new PrintWriter(new FileWriter(new File(args[1], "fragments.log"), true), true);
        FunctionManager fm = currentProgram.getFunctionManager();
        int listed = 0, covered = 0, created = 0, failed = 0;
        java.util.List<Address> entries = new java.util.ArrayList<>();
        if (args[0].equals("instructions")) {
            // Disassembled code in no function: an entry at the first instruction of each run, and after each ret or jump.
            Instruction previous = null;
            for (Instruction i : currentProgram.getListing().getInstructions(true)) {
                if (fm.getFunctionContaining(i.getAddress()) == null
                    && (previous == null || !previous.getMaxAddress().next().equals(i.getAddress())
                        || !previous.getFlowType().isFallthrough() || fm.getFunctionContaining(previous.getAddress()) != null))
                    entries.add(i.getAddress());
                previous = i;
            }
        } else {
            for (String line : Files.readAllLines(Paths.get(args[0])))
                if (!line.isBlank()) entries.add(toAddr(Long.parseLong(line.trim(), 16)));
        }
        for (Address a : entries) {
            monitor.checkCancelled();
            listed++;
            if (fm.getFunctionContaining(a) != null) { covered++; continue; }
            if (getInstructionAt(a) == null) disassemble(a);
            Function f = null;
            try { f = createFunction(a, null); } catch (Exception e) { log.println("create " + a + ": " + e); }
            if (f != null) created++;
            else { failed++; log.println("no function at " + a); }
            if (created % 2000 == 1999) {
                log.println("created " + created);
                end(true);
                currentProgram.save("NightFragments", monitor);
                start();
            }
        }
        log.println(listed + " listed, " + covered + " covered, " + created + " created, " + failed + " failed");
        println(listed + " listed, " + covered + " covered, " + created + " created, " + failed + " failed");
    }
}
