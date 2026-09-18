// Print every symbol whose name contains any given substring (case-sensitive), with RVA.
//   FindSym.java out.txt LoadBar AreaLoaded
import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;
import java.io.*;
public class FindSym extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter w = new PrintWriter(new FileWriter(a[0]));
        long base = currentProgram.getImageBase().getOffset();
        w.println("# program=" + currentProgram.getName() + " imagebase=0x" + Long.toHexString(base));
        SymbolIterator it = currentProgram.getSymbolTable().getAllSymbols(true);
        while (it.hasNext()) {
            Symbol s = it.next();
            String n = s.getName(true);
            for (int i = 1; i < a.length; i++) {
                if (n.contains(a[i])) {
                    long off = s.getAddress().getOffset();
                    w.println(s.getSymbolType() + "\t0x" + Long.toHexString(off) + "\tRVA=0x" + Long.toHexString(off - base) + "\t" + n);
                    break;
                }
            }
        }
        w.close();
    }
}
