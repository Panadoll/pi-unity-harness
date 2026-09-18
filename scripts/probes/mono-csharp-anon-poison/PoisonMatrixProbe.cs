using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class AnonPoisonProbe
{
    private static Type s_evalType, s_ctxType, s_setType, s_printerType, s_cmType;
    private static MethodInfo s_compile;
    private static StringWriter s_writer;

    private static object NewEvaluator()
    {
        object settings = Activator.CreateInstance(s_setType);
        s_writer = new StringWriter();
        object printer = Activator.CreateInstance(s_printerType, new object[] { s_writer });
        object ctx = Activator.CreateInstance(s_ctxType, new object[] { settings, printer });
        object ev = Activator.CreateInstance(s_evalType, new object[] { ctx });
        s_compile.Invoke(ev, new object[] { "using System;\n", null });
        s_writer.GetStringBuilder().Clear();
        return ev;
    }

    private static string Compile(object ev, string code)
    {
        s_writer.GetStringBuilder().Clear();
        object[] args = { code + "\n", null };
        try
        {
            string partial = (string)s_compile.Invoke(ev, args);
            string diag = s_writer.ToString().Trim().Replace("\r", "").Replace("\n", " | ");
            return "ok compiled=" + (args[1] != null) + (partial == null ? "" : " partial=" + partial)
                + (diag.Length == 0 ? "" : " diag=[" + diag + "]");
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            return "THROW " + inner.GetType().Name + ": " + inner.Message;
        }
    }

    private static object Field(object o, string name)
    {
        Type t = o.GetType();
        while (t != null)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f.GetValue(o);
            t = t.BaseType;
        }
        return null;
    }

    private static IEnumerable<object> Items(object collection)
    {
        if (collection is IEnumerable e && !(collection is string))
            foreach (object item in e) yield return item;
    }

    private static int ClearAnonCache(object ev)
    {
        object module = Field(ev, "module");
        object cache = module == null ? null : Field(module, "anonymous_types");
        if (cache is IDictionary dict)
        {
            int n = dict.Count;
            dict.Clear();
            return n;
        }
        return -1;
    }

    private static void Scenario(string title, string[] steps, string probe)
    {
        Console.WriteLine("=== " + title);
        object ev = NewEvaluator();
        foreach (string step in steps)
            Console.WriteLine("   step  : " + step.Replace("\n", "\\n") + " -> " + Compile(ev, step));
        string afterFailure = Compile(ev, "1 + 1");
        Console.WriteLine("   after : " + afterFailure);
        if (afterFailure.StartsWith("THROW"))
        {
            Console.WriteLine("   clear anon cache entries removed = " + ClearAnonCache(ev));
            Console.WriteLine("   retry : " + Compile(ev, "1 + 1"));
        }
        Console.WriteLine("   probe : " + probe.Replace("\n", "\\n") + " -> " + Compile(ev, probe));
        Console.WriteLine();
    }

    private static void Main(string[] args)
    {
        Assembly asm = Assembly.LoadFrom(args[0]);
        s_evalType = asm.GetType("Mono.CSharp.Evaluator");
        s_ctxType = asm.GetType("Mono.CSharp.CompilerContext");
        s_setType = asm.GetType("Mono.CSharp.CompilerSettings");
        s_printerType = asm.GetType("Mono.CSharp.StreamReportPrinter");
        s_cmType = asm.GetType("Mono.CSharp.CompiledMethod");
        s_compile = s_evalType.GetMethod("Compile", new[] { typeof(string), s_cmType.MakeByRefType() });

        Scenario("A: anon type declared OK first, then a failing statement",
            new[] { "var doomed = new { a = 1 };", "noSuchName;" }, "doomed");

        Scenario("B: one text, failing statement after anon type",
            new[] { "var x = new { a = 1 };\nnoSuchName;" }, "x");

        Scenario("C: state kept, anon type, then failing statement",
            new[] { "var keep = 7;", "var t = new { k = keep };", "noSuchName;" }, "keep");

        Scenario("D: failing text whose anon type references a missing name",
            new[] { "var bad = new { a = noSuchName };", "1 + 1" }, "bad");
    }
}
