// List every reference to each address arg with its containing function.
//   Callers.java out.txt 7709b0 7724f0
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.io.*;
public class Callers extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new FileWriter(a[0]));
        for (int i = 1; i < a.length; i++) {
            Address ad = toAddr(a[i]);
            w.println("// callers of " + a[i]);
            for (Reference r : getReferencesTo(ad)) {
                Function f = getFunctionContaining(r.getFromAddress());
                w.println("  " + r.getFromAddress() + " " + r.getReferenceType() + " in " + (f==null?"?":f.getParentNamespace().getName()+"::"+f.getName()+"@"+f.getEntryPoint()));
            }
        }
        w.close();
    }
}
