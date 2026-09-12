// For each string arg: find it in memory, print every reference and the containing function.
//   StringRefs.java out.txt "pnl_compass" "GuiCompass"
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
public class StringRefs extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new FileWriter(a[0]));
        for (int i = 1; i < a.length; i++) {
            w.println("// string \"" + a[i] + "\"");
            Address cur = currentProgram.getMinAddress();
            byte[] pat = (a[i] + "\0").getBytes("ASCII");
            while (cur != null) {
                Address hit = currentProgram.getMemory().findBytes(cur, pat, null, true, monitor);
                if (hit == null) break;
                w.println("  @" + hit);
                for (Reference r : getReferencesTo(hit)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    w.println("    " + r.getFromAddress() + " " + r.getReferenceType() + " in " + (f==null?"?":f.getName()+"@"+f.getEntryPoint()));
                }
                cur = hit.add(1);
            }
        }
        w.close();
    }
}
