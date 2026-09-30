// Decompile every function of the program into a read-only listing:
//   src/<Namespace>.c  one file per class/namespace, each function headed "// ===== Ns::name @addr"
//   symbols.tsv        addr, size, name, signature, mangled, file, line, status -- the index into src/
//   callgraph.tsv      direct call edges, thunks resolved to their targets
//   types.h            the program's data types as C
//   MANIFEST           provenance (program hash, Ghidra version, counts)
//   DecompAll.java <outdir> [threads]      -- run via tools/ghidra/decomp-all.sh
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.app.decompiler.parallel.*;
import ghidra.framework.Application;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.DataTypeWriter;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.util.task.TaskMonitor;
import generic.concurrent.GThreadPool;
import java.io.*;
import java.util.*;

public class DecompAll extends GhidraScript {
    /** Functions per parallel batch; whole namespaces are kept together, so a batch can run over. */
    static final int CHUNK = 2000;
    /** Per-function decompiler timeout, seconds. */
    static final int TIMEOUT = 300;

    record Out(Function f, String c, String status) {}

    public void run() throws Exception {
        String[] a = getScriptArgs();
        File out = new File(a[0]), src = new File(out, "src");
        int threads = a.length > 1 ? Integer.parseInt(a[1]) : 8;
        if (!src.mkdirs()) throw new IOException("cannot create " + src);
        long t0 = System.currentTimeMillis();

        TreeMap<String, List<Function>> byNs = new TreeMap<>();
        int total = 0;
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            if (f.isExternal() || f.isThunk()) continue;
            Namespace ns = f.getParentNamespace();
            byNs.computeIfAbsent(ns.isGlobal() ? "_global" : ns.getName(true), k -> new ArrayList<>()).add(f);
            total++;
        }
        println("DecompAll: " + total + " functions in " + byNs.size() + " namespaces, " + threads + " threads");

        int edges = 0;
        try (PrintWriter cg = new PrintWriter(new BufferedWriter(new FileWriter(new File(out, "callgraph.tsv"))))) {
            cg.println("caller_addr\tcaller\tcallee_addr\tcallee");
            for (List<Function> l : byNs.values()) for (Function f : l) {
                TreeSet<String> seen = new TreeSet<>();
                for (Function c : f.getCalledFunctions(monitor)) {
                    if (c.isThunk()) c = c.getThunkedFunction(true);
                    seen.add(c.getEntryPoint() + "\t" + c.getName(true));
                }
                for (String s : seen) cg.println(f.getEntryPoint() + "\t" + f.getName(true) + "\t" + s);
                edges += seen.size();
            }
        }

        try (Writer w = new BufferedWriter(new FileWriter(new File(out, "types.h")))) {
            new DataTypeWriter(currentProgram.getDataTypeManager(), w).write(currentProgram.getDataTypeManager(), monitor);
        }

        DecompileOptions opts = new DecompileOptions();
        opts.grabFromProgram(currentProgram);
        DecompilerCallback<Out> cb = new DecompilerCallback<>(currentProgram, ifc -> {
            ifc.setOptions(opts);
            ifc.setSimplificationStyle("decompile");
        }) {
            public Out process(DecompileResults r, TaskMonitor m) {
                if (r.decompileCompleted()) return new Out(r.getFunction(), r.getDecompiledFunction().getC(), "ok");
                return new Out(r.getFunction(), "// FAILED: " + r.getErrorMessage() + "\n", r.isTimedOut() ? "timeout" : "fail");
            }
        };
        cb.setTimeout(TIMEOUT);
        GThreadPool.getSharedThreadPool("Parallel Decompiler").setMaxThreadCount(threads);
        ChunkingParallelDecompiler<Out> dec = ParallelDecompiler.createChunkingParallelDecompiler(cb, monitor);

        SymbolTable st = currentProgram.getSymbolTable();
        Set<String> used = new HashSet<>();
        int done = 0, failed = 0;
        try (PrintWriter sym = new PrintWriter(new BufferedWriter(new FileWriter(new File(out, "symbols.tsv"))))) {
            sym.println("addr\tsize\tname\tsignature\tmangled\tfile\tline\tstatus");
            List<String> batch = new ArrayList<>();
            List<Function> fns = new ArrayList<>();
            Iterator<String> it = byNs.keySet().iterator();
            while (it.hasNext()) {
                String ns = it.next();
                batch.add(ns);
                fns.addAll(byNs.get(ns));
                if (fns.size() < CHUNK && it.hasNext()) continue;
                Map<Address, Out> res = new HashMap<>();
                for (Out o : dec.decompileFunctions(fns)) res.put(o.f().getEntryPoint(), o);
                for (String b : batch) failed += writeNs(src, b, fileName(b, used), byNs.get(b), res, st, sym);
                done += fns.size();
                println("DecompAll: " + done + "/" + total + ", " + failed + " failed, " + (System.currentTimeMillis() - t0) / 1000 + "s");
                batch.clear();
                fns.clear();
            }
        } finally {
            dec.dispose();
            cb.dispose();
        }

        try (PrintWriter m = new PrintWriter(new FileWriter(new File(out, "MANIFEST")))) {
            m.println("program\t" + currentProgram.getName());
            m.println("executable\t" + currentProgram.getExecutablePath());
            m.println("sha256\t" + currentProgram.getExecutableSHA256());
            m.println("ghidra\t" + Application.getApplicationVersion());
            m.println("generated\t" + new Date());
            m.println("functions\t" + total);
            m.println("namespaces\t" + byNs.size());
            m.println("failed\t" + failed);
            m.println("call_edges\t" + edges);
            m.println("seconds\t" + (System.currentTimeMillis() - t0) / 1000);
        }
        println("DecompAll: done, " + total + " functions, " + failed + " failed, " + edges + " call edges");
    }

    /** Writes one namespace's file (functions by name, then address) and its symbols.tsv rows; returns failures. */
    int writeNs(File src, String ns, String file, List<Function> fns, Map<Address, Out> res, SymbolTable st, PrintWriter sym) throws IOException {
        fns.sort(Comparator.comparing((Function f) -> f.getName()).thenComparing(f -> f.getEntryPoint()));
        int failed = 0, line = 1;
        try (Writer w = new BufferedWriter(new FileWriter(new File(src, file)))) {
            w.write("// " + ns + " -- decompiled from " + currentProgram.getName() + " by tools/ghidra/DecompAll.java. Read-only.\n\n");
            line += 2;
            for (Function f : fns) {
                Out o = res.get(f.getEntryPoint());
                String c = o != null ? o.c() : "// MISSING: no decompiler result\n";
                String status = o != null ? o.status() : "missing";
                if (!status.equals("ok")) failed++;
                sym.println(f.getEntryPoint() + "\t" + f.getBody().getNumAddresses() + "\t" + f.getName(true) + "\t"
                    + f.getPrototypeString(false, false).replace('\t', ' ') + "\t" + mangled(st, f) + "\tsrc/" + file + "\t" + line + "\t" + status);
                w.write("// ===== " + f.getName(true) + " @" + f.getEntryPoint() + "\n");
                if (!c.endsWith("\n")) c += "\n";
                w.write(c);
                w.write("\n");
                line += 2 + (int) c.chars().filter(ch -> ch == '\n').count();
            }
        }
        return failed;
    }

    /** The Itanium-mangled label Ghidra keeps beside the demangled name, or "" if it kept none. */
    static String mangled(SymbolTable st, Function f) {
        for (Symbol s : st.getSymbols(f.getEntryPoint()))
            if (s.getName().startsWith("_Z")) return s.getName();
        return "";
    }

    /** A filesystem-safe, case-insensitively unique file name for a namespace path. */
    static String fileName(String ns, Set<String> used) {
        String s = ns.replace("::", ".").replaceAll("[^A-Za-z0-9_.-]+", "_");
        if (s.length() > 120) s = s.substring(0, 100) + "_" + Integer.toHexString(ns.hashCode());
        String base = s;
        for (int n = 2; !used.add(s.toLowerCase()); n++) s = base + "_" + n;
        return s + ".c";
    }
}
