// Usage: analyzeHeadless ... -max-cpu N -postScript NightParamId.java <outdir> <deadlineEpochSeconds> [batch=4000] [limit=0]
// Runs Ghidra's Decompiler Parameter ID (off in default analysis, hours on the full client) in batches: prototypes the
// decompiler proves are committed, so callers stop reading as undefined8 FUN_x(). The program is saved after each batch
// and only then does <outdir>/paramid.cursor move, so a crash loses one batch. Past the deadline it stops cleanly;
// paramid.done means it ran to the end, paramid.partial that the deadline stopped it.
import ghidra.app.cmd.function.DecompilerParameterIdCmd;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.SourceType;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class NightParamId extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        File out = new File(a[0]);
        out.mkdirs();
        long deadline = Long.parseLong(a[1]);
        int batch = a.length > 2 ? Integer.parseInt(a[2]) : 4000;
        int limit = a.length > 3 ? Integer.parseInt(a[3]) : 0;
        File cursor = new File(out, "paramid.cursor");
        long from = cursor.exists() ? Long.parseUnsignedLong(Files.readString(cursor.toPath()).trim(), 16) : 0;

        List<Function> work = new ArrayList<>();
        for (Function f : currentProgram.getFunctionManager().getFunctions(true))
            if (!f.isExternal() && !f.isThunk() && Long.compareUnsigned(f.getEntryPoint().getOffset(), from) > 0) work.add(f);
        if (limit > 0 && work.size() > limit) work = work.subList(0, limit);
        println("paramid: " + work.size() + " functions left");
        long t0 = System.currentTimeMillis();
        int done = 0;
        for (int i = 0; i < work.size(); i += batch) {
            if (System.currentTimeMillis() / 1000 > deadline) {
                new FileWriter(new File(out, "paramid.partial")).close();
                println("paramid: deadline, " + (work.size() - i) + " functions left");
                return;
            }
            monitor.checkCancelled();
            List<Function> part = work.subList(i, Math.min(i + batch, work.size()));
            AddressSet set = new AddressSet();
            for (Function f : part) set.add(f.getEntryPoint());
            // The analyzer's defaults: commit data types, not void returns, 60 s per function.
            new DecompilerParameterIdCmd("NightParamId", set, SourceType.ANALYSIS, true, false, 60).applyTo(currentProgram, monitor);
            end(true);
            currentProgram.save("NightParamId", monitor);
            start();
            Address last = part.get(part.size() - 1).getEntryPoint();
            Files.writeString(cursor.toPath(), Long.toHexString(last.getOffset()));
            done += part.size();
            println(String.format("paramid: %d/%d, %.1f/s, at %s", done, work.size(), done * 1000.0 / (System.currentTimeMillis() - t0), last));
        }
        new FileWriter(new File(out, "paramid.done")).close();
    }
}
