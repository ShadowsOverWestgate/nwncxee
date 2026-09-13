// Functions whose instructions reference EVERY given scalar (e.g. struct offsets), no decompile.
//   ScanOffsets.java out.txt 16c 168 f8
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;

public class ScanOffsets extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        Set<Long> want = new HashSet<>();
        for (int i = 1; i < a.length; i++) want.add(Long.parseLong(a[i], 16));
        Map<Function, Set<Long>> hits = new HashMap<>();
        for (Instruction ins : currentProgram.getListing().getInstructions(true)) {
            for (int op = 0; op < ins.getNumOperands(); op++)
                for (Object o : ins.getOpObjects(op))
                    if (o instanceof Scalar && want.contains(((Scalar) o).getUnsignedValue())) {
                        Function f = getFunctionContaining(ins.getAddress());
                        if (f != null) hits.computeIfAbsent(f, k -> new HashSet<>()).add(((Scalar) o).getUnsignedValue());
                    }
        }
        PrintWriter w = new PrintWriter(new FileWriter(a[0]));
        int n = 0;
        for (Map.Entry<Function, Set<Long>> e : hits.entrySet())
            if (e.getValue().size() == want.size()) { w.println(e.getKey().getName() + " @" + e.getKey().getEntryPoint()); n++; }
        w.close();
        println("ScanOffsets: " + n + " function(s) hit all " + want.size());
    }
}
