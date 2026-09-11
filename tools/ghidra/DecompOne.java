// Print the decompiled C of one or more functions, named or @address.
//   DecompOne.java out.c Scene::RenderBeams @571860
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class DecompOne extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(a[0])));
        DecompInterface ifc = new DecompInterface();
        ifc.setOptions(new DecompileOptions());
        ifc.setSimplificationStyle("decompile");
        ifc.openProgram(currentProgram);
        List<Function> todo = new ArrayList<>();
        for (int i = 1; i < a.length; i++) {
            String s = a[i];
            if (s.startsWith("@")) {
                Function f = getFunctionAt(toAddr(s.substring(1)));
                if (f != null) todo.add(f); else w.println("// NO FUNCTION AT " + s);
            } else {
                String cls = s.contains("::") ? s.substring(0, s.indexOf("::")) : null;
                String nm = s.contains("::") ? s.substring(s.indexOf("::") + 2) : s;
                FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
                while (it.hasNext()) {
                    Function f = it.next();
                    if (!f.getName().equals(nm)) continue;
                    if (cls != null && !f.getParentNamespace().getName().equals(cls)) continue;
                    todo.add(f);
                }
            }
        }
        for (Function f : todo) {
            w.println("// ===== " + f.getParentNamespace().getName() + "::" + f.getName() + " @" + f.getEntryPoint());
            DecompileResults r = ifc.decompileFunction(f, 120, monitor);
            w.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// FAILED: " + r.getErrorMessage());
        }
        w.close();
        println("DecompOne: " + todo.size() + " function(s)");
    }
}
