using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PKForgeJa.Patcher;

/// <summary>
/// アセンブリの書き換え（dnlib）。
///  - 翻訳ランタイム（JaText など）を PKForge.Chrome / PKForge.App に移植
///  - 文字を描く・測る・表示する API の呼び出しを、翻訳を挟むラッパーに置き換え
/// メタデータの行番号・トークンはすべて保存する（型マップが MVID とトークンに依存するため）。
/// </summary>
public sealed class IlPatcher
{
    private readonly AssemblyStore _store;
    private readonly Action<string> _log;
    private readonly StoreResolver _resolver;
    private readonly ModuleContext _context;
    private readonly Dictionary<string, ModuleDefMD> _patched = new(StringComparer.OrdinalIgnoreCase);

    public IlPatcher(AssemblyStore store, Action<string> log)
    {
        _store = store;
        _log = log;
        _resolver = new StoreResolver(store);
        _context = new ModuleContext(_resolver, new Resolver(_resolver));
        _resolver.Context = _context;
    }

    public ModuleDefMD Load(string assembly)
    {
        if (_patched.TryGetValue(assembly, out var m)) return m;
        m = ModuleDefMD.Load(_store.Get(assembly + ".dll"), _context);
        _patched[assembly] = m;
        _resolver.Register(m.Assembly);
        return m;
    }

    // ------------------------------------------------------------------
    // ランタイムの移植
    // ------------------------------------------------------------------
    public List<TypeDef> MoveTypes(byte[] runtimeAssembly, ModuleDef target)
    {
        var src = ModuleDefMD.Load(runtimeAssembly, new ModuleContext(_resolver, new Resolver(_resolver)));
        var moved = new List<TypeDef>();
        foreach (var t in src.Types.ToList())
        {
            if (t.IsGlobalModuleType) continue;
            src.Types.Remove(t);
            if (target.Find(t.FullName, false) != null) t.Name = t.Name.String + "_ja";
            // 元のモジュールでの行番号を捨てる（移植先で新しく採番させる）
            foreach (var x in t.GetTypes().Prepend(t))
            {
                x.Rid = 0;
                foreach (var f in x.Fields) f.Rid = 0;
                foreach (var p in x.Properties) p.Rid = 0;
                foreach (var e in x.Events) e.Rid = 0;
                foreach (var m in x.Methods)
                {
                    m.Rid = 0;
                    foreach (var pd in m.ParamDefs) pd.Rid = 0;
                    foreach (var gp in m.GenericParameters) gp.Rid = 0;
                }
                foreach (var gp in x.GenericParameters) gp.Rid = 0;
                foreach (var ii in x.Interfaces) ii.Rid = 0;
            }
            target.Types.Add(t);
            moved.Add(t);
        }
        // 移植したコードが参照する型・メンバーも移植先のモジュールに取り込み直す
        var importer = new Importer(target, ImporterOptions.TryToUseDefs);
        foreach (var t in moved.SelectMany(x => x.GetTypes().Prepend(x)))
        {
            if (t.BaseType != null) t.BaseType = importer.Import(t.BaseType);
            foreach (var ii in t.Interfaces) ii.Interface = importer.Import(ii.Interface);
            foreach (var f in t.Fields) f.FieldSig = importer.Import(f.FieldSig);
            foreach (var m in t.Methods)
            {
                m.MethodSig = importer.Import(m.MethodSig);
                foreach (var o in m.Overrides.ToList())
                {
                    m.Overrides.Remove(o);
                    m.Overrides.Add(new MethodOverride((IMethodDefOrRef)importer.Import(o.MethodBody), (IMethodDefOrRef)importer.Import(o.MethodDeclaration)));
                }
                if (!m.HasBody) continue;
                foreach (var local in m.Body.Variables) local.Type = importer.Import(local.Type);
                foreach (var eh in m.Body.ExceptionHandlers)
                    if (eh.CatchType != null) eh.CatchType = importer.Import(eh.CatchType);
                foreach (var ins in m.Body.Instructions)
                {
                    ins.Operand = ins.Operand switch
                    {
                        TypeDef => ins.Operand,
                        MethodDef => ins.Operand,
                        FieldDef => ins.Operand,
                        ITypeDefOrRef tr => importer.Import(tr),
                        IMethod me => importer.Import(me),
                        IField fe => importer.Import(fe),
                        MethodSig ms => importer.Import(ms),
                        _ => ins.Operand,
                    };
                }
            }
        }
        return moved;
    }

    /// <summary>移植したコードが参照する API が、トリミング済みのアプリ内に実在するか確かめる。</summary>
    public List<string> Verify(IEnumerable<TypeDef> types)
    {
        var missing = new List<string>();
        foreach (var t in types.SelectMany(x => x.GetTypes().Prepend(x)).Distinct())
        {
            foreach (var iface in t.Interfaces)
                if (iface.Interface.ResolveTypeDef() == null) missing.Add(iface.Interface.FullName);
            if (t.BaseType != null && t.BaseType.ResolveTypeDef() == null) missing.Add(t.BaseType.FullName);
            foreach (var m in t.Methods.Where(m => m.HasBody))
            {
                foreach (var ins in m.Body.Instructions)
                {
                    switch (ins.Operand)
                    {
                        case MemberRef mr when mr.IsMethodRef:
                            if (mr.ResolveMethod() == null) missing.Add(mr.FullName);
                            break;
                        case MemberRef mr when mr.IsFieldRef:
                            if (mr.ResolveField() == null) missing.Add(mr.FullName);
                            break;
                        case MethodSpec ms when ms.Method is MemberRef gm:
                            if (gm.ResolveMethod() == null) missing.Add(gm.FullName);
                            break;
                        case ITypeDefOrRef tr when tr is not TypeDef:
                            if (tr.ResolveTypeDef() == null) missing.Add(tr.FullName);
                            break;
                    }
                }
            }
        }
        return missing.Distinct().ToList();
    }

    // ------------------------------------------------------------------
    // 呼び出しの置き換え
    // ------------------------------------------------------------------
    private static readonly HashSet<string> SkiaTypes = new()
    {
        "SkiaSharp.SKCanvas", "SkiaSharp.SKFont", "SkiaSharp.SKPaint", "SkiaSharp.SKTextBlob",
    };
    private static readonly HashSet<string> SkiaMethods = new()
    {
        "DrawText", "MeasureText", "BreakText", "DrawTextOnPath", "Create", "CreatePositioned", "CreateHorizontal",
    };
    private static readonly HashSet<string> MauiSetters = new()
    {
        "Microsoft.Maui.Controls.Label::set_Text",
        "Microsoft.Maui.Controls.Span::set_Text",
        "Microsoft.Maui.Controls.Button::set_Text",
        "Microsoft.Maui.Controls.Page::set_Title",
        "Microsoft.Maui.Controls.InputView::set_Placeholder",
        "Microsoft.Maui.Controls.MenuItem::set_Text",
        "Microsoft.Maui.Controls.Picker::set_Title",
        "Microsoft.Maui.Controls.ImageButton::set_Text",
    };
    private static readonly HashSet<string> TextHelpers = new() { "Wrap", "Fit", "Ellipsize", "Truncate", "WrapText", "FitText" };

    public sealed class HookStats
    {
        public int Calls, Bindings, Prefixes;
        public override string ToString() => $"描画呼び出し {Calls} 箇所 / バインディング {Bindings} 箇所 / 折り返し処理 {Prefixes} 箇所";
    }

    public HookStats HookModule(ModuleDef module, IMethod translate, IMethod? setBinding, IMethod? setBindingPath)
    {
        var stats = new HookStats();
        var hooks = new TypeDefUser("PKForgeJa", "Hooks", module.CorLibTypes.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                         TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
        };
        var wrappers = new Dictionary<string, MethodDef>();

        foreach (var type in module.GetTypes().ToList())
        {
            if (type.Namespace.String == "PKForgeJa") continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                // 折り返し・省略処理は入口で訳す（行に分けられた後では辞書と一致しないため）
                if (TextHelpers.Contains(method.Name.String))
                {
                    var p = method.Parameters.FirstOrDefault(x => x.IsNormalMethodParameter
                        && x.Type.ElementType == ElementType.String
                        && (x.Name == "text" || x.Name == "label" || x.Name == "s"));
                    if (p != null)
                    {
                        var body = method.Body;
                        body.SimplifyBranches();
                        body.Instructions.Insert(0, OpCodes.Ldarg.ToInstruction(p));
                        body.Instructions.Insert(1, OpCodes.Call.ToInstruction(translate));
                        body.Instructions.Insert(2, OpCodes.Starg.ToInstruction(p));
                        body.OptimizeBranches();
                        stats.Prefixes++;
                    }
                }

                var list = method.Body.Instructions;
                for (int i = 0; i < list.Count; i++)
                {
                    var ins = list[i];
                    if (ins.OpCode.Code is not (Code.Call or Code.Callvirt)) continue;
                    if (ins.Operand is not IMethod target || target is MethodSpec) continue;
                    if (i > 0 && list[i - 1].OpCode.Code == Code.Constrained) continue;
                    var declaring = target.DeclaringType?.FullName ?? "";
                    var key = declaring + "::" + target.Name.String;
                    var sig = target.MethodSig;
                    if (sig == null || sig.Generic) continue;

                    if (declaring == "Microsoft.Maui.Controls.BindableObject" && target.Name.String == "SetBinding"
                        && sig.HasThis && sig.Params.Count == 2 && setBinding != null)
                    {
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = setBinding;
                        stats.Bindings++;
                        continue;
                    }
                    if (declaring == "Microsoft.Maui.Controls.BindableObjectExtensions" && target.Name.String == "SetBinding"
                        && !sig.HasThis && sig.Params.Count == 6 && sig.Params[2].ElementType == ElementType.String
                        && setBindingPath != null)
                    {
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = setBindingPath;
                        stats.Bindings++;
                        continue;
                    }

                    bool sink = (SkiaTypes.Contains(declaring) && SkiaMethods.Contains(target.Name.String)) || MauiSetters.Contains(key);
                    if (!sink || !sig.Params.Any(t => t.ElementType == ElementType.String)) continue;

                    var full = target.FullName;
                    if (!wrappers.TryGetValue(full, out var wrapper))
                    {
                        wrapper = MakeWrapper(module, target, ins.OpCode, translate, wrappers.Count);
                        if (wrapper == null) continue;
                        hooks.Methods.Add(wrapper);
                        wrappers[full] = wrapper;
                    }
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = wrapper;
                    stats.Calls++;
                }
            }
        }
        if (hooks.Methods.Count > 0) module.Types.Add(hooks);
        return stats;
    }

    private static MethodDef? MakeWrapper(ModuleDef module, IMethod target, OpCode original, IMethod translate, int n)
    {
        var sig = target.MethodSig;
        var ps = new List<TypeSig>();
        if (sig.HasThis)
        {
            var dt = target.DeclaringType;
            if (dt == null || dt.IsValueType) return null;
            ps.Add(new ClassSig(dt));
        }
        ps.AddRange(sig.Params);
        var wsig = MethodSig.CreateStatic(sig.RetType, ps.ToArray());
        var m = new MethodDefUser($"W{n}_{target.Name.String}", wsig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig);
        var body = new CilBody();
        m.Body = body;
        m.Parameters.UpdateParameterTypes();
        for (int i = 0; i < ps.Count; i++)
        {
            body.Instructions.Add(OpCodes.Ldarg.ToInstruction(m.Parameters[i]));
            if (ps[i].ElementType == ElementType.String)
                body.Instructions.Add(OpCodes.Call.ToInstruction(translate));
        }
        body.Instructions.Add((sig.HasThis ? OpCodes.Callvirt : OpCodes.Call).ToInstruction(target));
        body.Instructions.Add(OpCodes.Ret.ToInstruction());
        body.OptimizeMacros();
        return m;
    }

    // ------------------------------------------------------------------
    // 書き出し
    // ------------------------------------------------------------------
    public byte[] Write(ModuleDefMD module)
    {
        var options = new ModuleWriterOptions(module);
        options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
        options.MetadataOptions.Flags &= ~MetadataFlags.KeepOldMaxStack;
        options.Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
        var ms = new MemoryStream();
        module.Write(ms, options);
        return ms.ToArray();
    }

    /// <summary>アプリ内の全アセンブリからリソースを読む（PKHeX のゲームテキストなど）。</summary>
    public byte[]? ReadResource(string assembly, string name)
    {
        var m = Load(assembly);
        var r = m.Resources.FindEmbeddedResource(name);
        return r?.CreateReader().ToArray();
    }

    public IEnumerable<string> ResourceNames(string assembly) => Load(assembly).Resources.Select(r => r.Name.String);

    private sealed class StoreResolver : IAssemblyResolver
    {
        private readonly AssemblyStore _store;
        private readonly Dictionary<string, AssemblyDef?> _cache = new(StringComparer.OrdinalIgnoreCase);
        public ModuleContext? Context;

        public StoreResolver(AssemblyStore store) => _store = store;

        public void Register(AssemblyDef asm) => _cache[asm.Name] = asm;

        public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule)
        {
            var name = assembly.Name.String;
            if (_cache.TryGetValue(name, out var hit)) return hit;
            AssemblyDef? result = null;
            if (_store.IndexOf(name + ".dll") >= 0)
            {
                try
                {
                    result = ModuleDefMD.Load(_store.Get(name + ".dll"), Context).Assembly;
                }
                catch
                {
                    result = null;
                }
            }
            _cache[name] = result;
            return result;
        }
    }
}
