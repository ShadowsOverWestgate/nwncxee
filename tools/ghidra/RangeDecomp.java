// Print the decompiled C of every function whose entry lies in [lo, hi).
//   RangeDecomp.java out.c 1406ac000 1406ac200
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class RangeDecomp extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(a[0])));
        DecompInterface ifc = new DecompInterface();
        ifc.setOptions(new DecompileOptions());
        ifc.openProgram(currentProgram);
        AddressSet set = new AddressSet(toAddr(a[1]), toAddr(a[2]).subtract(1));
        int n = 0;
        for (Function f : currentProgram.getFunctionManager().getFunctions(set, true)) {
            w.println("// ===== " + f.getName() + " @" + f.getEntryPoint());
            DecompileResults r = ifc.decompileFunction(f, 120, monitor);
            w.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// FAILED: " + r.getErrorMessage());
            n++;
        }
        w.close();
        println("RangeDecomp: " + n + " function(s)");
    }
}
