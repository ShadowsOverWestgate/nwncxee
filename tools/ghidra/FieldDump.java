// Dump, per member function, the set of field displacements reached from parameter 0 (`this`).
// Forward-slices the decompiler's p-code so `this` copied into a local is still tracked.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.*;
import ghidra.program.model.data.DataType;
import java.io.*;
import java.util.*;

public class FieldDump extends GhidraScript {

    static class Acc {
        long off; int size; boolean write; long addr; int order;
        Acc(long o, int s, boolean w, long a, int r) { off=o; size=s; write=w; addr=a; order=r; }
    }

    public void run() throws Exception {
        String[] a = getScriptArgs();
        String out = a[0];
        Set<String> want = new HashSet<>(Arrays.asList(a).subList(1, a.length));
        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(out)));
        w.println("# program=" + currentProgram.getName());

        DecompInterface ifc = new DecompInterface();
        DecompileOptions opt = new DecompileOptions();
        ifc.setOptions(opt);
        ifc.setSimplificationStyle("decompile");
        ifc.openProgram(currentProgram);

        List<Function> todo = new ArrayList<>();
        Set<String> addrs = new HashSet<>();
        for (String s : want) if (s.startsWith("@")) addrs.add(s.substring(1));
        want.removeIf(s -> s.startsWith("@"));
        for (String s : addrs) {
            ghidra.program.model.address.Address ad = toAddr(s);
            Function f = getFunctionAt(ad);
            if (f == null) f = createFunction(ad, "sub_" + s);
            if (f != null) todo.add(f); else println("NO FUNCTION AT " + s);
        }
        if (!want.isEmpty()) {
            FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
            while (it.hasNext()) {
                Function f = it.next();
                if (want.contains(f.getParentNamespace().getName())) todo.add(f);
            }
        }
        println("functions to decompile: " + todo.size());

        int n = 0;
        for (Function f : todo) {
            n++;
            if (n % 50 == 0) println("  " + n + "/" + todo.size());
            String cls = f.getParentNamespace().getName();
            String nm = f.getName();
            String sig;
            try { sig = f.getSignature(true).getPrototypeString(true); } catch (Exception e) { sig = "?"; }
            String ret = "?";
            try { DataType d = f.getReturnType(); ret = d == null ? "void" : (d.getName() + "/" + d.getLength()); } catch (Exception e) {}
            StringBuilder sb = new StringBuilder();
            sb.append("FN\t").append(cls).append('\t').append(nm).append('\t')
              .append(f.getEntryPoint()).append('\t').append(f.getCallingConventionName()).append('\t')
              .append(ret).append('\t').append(sig).append('\t');
            try {
                List<Acc> acc = accesses(ifc, f);
                if (acc == null) { sb.append("ERR"); }
                else {
                    acc.sort((x,y) -> x.addr != y.addr ? Long.compare(x.addr,y.addr) : Integer.compare(x.order,y.order));
                    for (Acc c : acc) sb.append(Long.toHexString(c.off)).append(':').append(c.size)
                                        .append(c.write ? "w" : "r").append(' ');
                }
            } catch (Exception e) {
                sb.append("EXC ").append(e.getClass().getSimpleName());
            }
            w.println(sb.toString());
        }
        w.close();
        ifc.dispose();
        println("done " + n);
    }

    private List<Acc> accesses(DecompInterface ifc, Function f) {
        DecompileResults res = ifc.decompileFunction(f, 90, monitor);
        if (res == null || !res.decompileCompleted()) return null;
        HighFunction hf = res.getHighFunction();
        if (hf == null) return null;
        FunctionPrototype proto = hf.getFunctionPrototype();
        if (proto == null || proto.getNumParams() < 1) return null;
        HighSymbol ps = proto.getParam(0);
        if (ps == null) return null;
        HighVariable hv = ps.getHighVariable();
        if (hv == null) return null;

        Map<Varnode, Long> off = new HashMap<>();
        ArrayDeque<Varnode> q = new ArrayDeque<>();
        for (Varnode v : hv.getInstances()) { off.put(v, 0L); q.add(v); }

        List<Acc> outl = new ArrayList<>();
        int guard = 0;
        while (!q.isEmpty() && guard++ < 20000) {
            Varnode v = q.poll();
            long base = off.get(v);
            Iterator<PcodeOp> di = v.getDescendants();
            while (di != null && di.hasNext()) {
                PcodeOp op = di.next();
                int oc = op.getOpcode();
                Varnode o = op.getOutput();
                long addr = op.getSeqnum().getTarget().getOffset();
                int ord = op.getSeqnum().getOrder();
                switch (oc) {
                    case PcodeOp.LOAD:
                        if (op.getInput(1).equals(v) && o != null)
                            outl.add(new Acc(base, o.getSize(), false, addr, ord));
                        break;
                    case PcodeOp.STORE:
                        if (op.getInput(1).equals(v))
                            outl.add(new Acc(base, op.getInput(2).getSize(), true, addr, ord));
                        break;
                    case PcodeOp.COPY: case PcodeOp.CAST: case PcodeOp.MULTIEQUAL:
                        push(off, q, o, base);
                        break;
                    case PcodeOp.INT_ADD: case PcodeOp.PTRSUB: {
                        Varnode other = op.getInput(0).equals(v) ? op.getInput(1) : op.getInput(0);
                        if (other.isConstant()) push(off, q, o, base + signed(other));
                        break;
                    }
                    case PcodeOp.PTRADD: {
                        if (op.getInput(0).equals(v) && op.getInput(1).isConstant() && op.getInput(2).isConstant())
                            push(off, q, o, base + signed(op.getInput(1)) * op.getInput(2).getOffset());
                        break;
                    }
                    default:
                        break;
                }
            }
        }
        return outl;
    }

    private static long signed(Varnode c) {
        long x = c.getOffset();
        int bits = c.getSize() * 8;
        if (bits < 64) { long m = 1L << (bits - 1); x = (x ^ m) - m; }
        return x;
    }

    private static void push(Map<Varnode, Long> off, ArrayDeque<Varnode> q, Varnode o, long val) {
        if (o == null) return;
        Long prev = off.get(o);
        if (prev != null) return;
        off.put(o, val);
        q.add(o);
    }
}
