using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PKForgeJa.Patcher;

/// <summary>
/// 別アセンブリ（翻訳ランタイム）の型を、移植先モジュールの「新規」型として複製する。
/// 移植先はトークンを保存して書き出すので、元モジュールの行番号を持つ *MD オブジェクトを
/// そのまま移すことはできない（行番号が衝突する）。
/// 対応範囲はランタイムが使う機能（クラス・入れ子クラス・フィールド・メソッド・インターフェイス）のみ。
/// </summary>
public sealed class TypeCloner
{
    private readonly ModuleDef _target;
    private readonly Importer _importer;
    private readonly Dictionary<TypeDef, TypeDef> _types = new();
    private readonly Dictionary<MethodDef, MethodDef> _methods = new();
    private readonly Dictionary<FieldDef, FieldDef> _fields = new();

    public TypeCloner(ModuleDef target)
    {
        _target = target;
        _importer = new Importer(target, ImporterOptions.TryToUseDefs);
    }

    public List<TypeDef> Clone(ModuleDef source)
    {
        var tops = source.Types.Where(t => !t.IsGlobalModuleType).ToList();
        var result = new List<TypeDef>();
        // 1. 殻（型・フィールド・メソッド）を作る
        foreach (var t in tops)
        {
            var c = Shell(t);
            if (_target.Find(t.FullName, false) != null) c.Name = t.Name.String + "_ja";
            result.Add(c);
        }
        // 2. 継承・シグネチャ・本体を埋める
        foreach (var pair in _types) FillType(pair.Key, pair.Value);
        foreach (var pair in _methods) FillMethod(pair.Key, pair.Value);
        foreach (var c in result) _target.Types.Add(c);
        return result;
    }

    private TypeDef Shell(TypeDef src)
    {
        var t = new TypeDefUser(src.Namespace, src.Name) { Attributes = src.Attributes, ClassLayout = null };
        _types[src] = t;
        foreach (var f in src.Fields)
        {
            var nf = new FieldDefUser(f.Name, null, f.Attributes);
            if (f.HasConstant) nf.Constant = new ConstantUser(f.Constant.Value, f.Constant.Type);
            if (f.HasFieldRVA) throw new NotSupportedException($"ランタイムに RVA 付きフィールドがあります: {f.FullName}");
            _fields[f] = nf;
            t.Fields.Add(nf);
        }
        foreach (var m in src.Methods)
        {
            var nm = new MethodDefUser(m.Name, null, m.ImplAttributes, m.Attributes);
            _methods[m] = nm;
            t.Methods.Add(nm);
        }
        if (src.HasProperties || src.HasEvents)
            throw new NotSupportedException($"ランタイムにプロパティ/イベントがあります（未対応）: {src.FullName}");
        foreach (var n in src.NestedTypes) t.NestedTypes.Add(Shell(n));
        return t;
    }

    private void FillType(TypeDef src, TypeDef dst)
    {
        if (src.BaseType != null) dst.BaseType = MapType(src.BaseType);
        foreach (var ii in src.Interfaces) dst.Interfaces.Add(new InterfaceImplUser(MapType(ii.Interface)));
        foreach (var f in src.Fields) _fields[f].FieldSig = new FieldSig(MapSig(f.FieldSig.Type));
    }

    private void FillMethod(MethodDef src, MethodDef dst)
    {
        dst.MethodSig = MapMethodSig(src.MethodSig);
        dst.Parameters.UpdateParameterTypes();
        foreach (var pd in src.ParamDefs)
            dst.ParamDefs.Add(new ParamDefUser(pd.Name, pd.Sequence, pd.Attributes));
        foreach (var o in src.Overrides)
            dst.Overrides.Add(new MethodOverride((IMethodDefOrRef)MapMethod(o.MethodBody), (IMethodDefOrRef)MapMethod(o.MethodDeclaration)));
        if (!src.HasBody) return;

        var sb = src.Body;
        var body = new CilBody { InitLocals = sb.InitLocals, MaxStack = sb.MaxStack, KeepOldMaxStack = false };
        dst.Body = body;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in sb.Variables)
        {
            var nl = new Local(MapSig(l.Type), l.Name, l.Index);
            locals[l] = nl;
            body.Variables.Add(nl);
        }
        var ins = new Dictionary<Instruction, Instruction>();
        foreach (var i in sb.Instructions)
        {
            var n = new Instruction(i.OpCode, i.Operand);
            ins[i] = n;
            body.Instructions.Add(n);
        }
        foreach (var n in body.Instructions)
        {
            n.Operand = n.Operand switch
            {
                Instruction target => ins[target],
                Instruction[] targets => targets.Select(x => ins[x]).ToArray(),
                Local l => locals[l],
                Parameter p => dst.Parameters[p.Index],
                MemberRef { IsFieldRef: true } fr => MapField(fr),
                ITypeDefOrRef t => MapType(t),
                IMethod m => MapMethod(m),
                IField f => MapField(f),
                MethodSig ms => MapMethodSig(ms),
                _ => n.Operand,
            };
        }
        foreach (var eh in sb.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
            {
                TryStart = Map(ins, eh.TryStart),
                TryEnd = Map(ins, eh.TryEnd),
                HandlerStart = Map(ins, eh.HandlerStart),
                HandlerEnd = Map(ins, eh.HandlerEnd),
                FilterStart = Map(ins, eh.FilterStart),
                CatchType = eh.CatchType == null ? null : MapType(eh.CatchType),
            });
        }
    }

    private static Instruction? Map(Dictionary<Instruction, Instruction> map, Instruction? i) => i == null ? null : map[i];

    // ---- 参照の付け替え ------------------------------------------------
    private ITypeDefOrRef MapType(ITypeDefOrRef t)
    {
        switch (t)
        {
            case TypeDef td when _types.TryGetValue(td, out var c):
                return c;
            case TypeSpec ts:
                return new TypeSpecUser(MapSig(ts.TypeSig));
            default:
                return _importer.Import(t);
        }
    }

    private TypeSig MapSig(TypeSig sig)
    {
        switch (sig)
        {
            case null:
                return null!;
            case ClassSig cs:
                return new ClassSig(MapType(cs.TypeDefOrRef));
            case ValueTypeSig vs:
                return new ValueTypeSig(MapType(vs.TypeDefOrRef));
            case GenericInstSig gi:
                return new GenericInstSig((ClassOrValueTypeSig)MapSig(gi.GenericType), gi.GenericArguments.Select(MapSig).ToList());
            case SZArraySig sz:
                return new SZArraySig(MapSig(sz.Next));
            case ByRefSig br:
                return new ByRefSig(MapSig(br.Next));
            case PtrSig p:
                return new PtrSig(MapSig(p.Next));
            case GenericVar or GenericMVar:
                return sig;
            default:
                return _importer.Import(sig);
        }
    }

    private MethodSig MapMethodSig(MethodSig sig)
    {
        var n = new MethodSig(sig.CallingConvention, sig.GenParamCount, MapSig(sig.RetType), sig.Params.Select(MapSig).ToList());
        return n;
    }

    private IMethod MapMethod(IMethod m)
    {
        switch (m)
        {
            case MethodDef md when _methods.TryGetValue(md, out var c):
                return c;
            case MemberRef mr:
                return new MemberRefUser(_target, mr.Name, MapMethodSig(mr.MethodSig), MapParent(mr.Class));
            case MethodSpec ms:
                return new MethodSpecUser((IMethodDefOrRef)MapMethod(ms.Method),
                    new GenericInstMethodSig(ms.GenericInstMethodSig.GenericArguments.Select(MapSig).ToList()));
            default:
                return _importer.Import(m);
        }
    }

    private IField MapField(IField f)
    {
        switch (f)
        {
            case FieldDef fd when _fields.TryGetValue(fd, out var c):
                return c;
            case MemberRef mr:
                return new MemberRefUser(_target, mr.Name, new FieldSig(MapSig(mr.FieldSig.Type)), MapParent(mr.Class));
            default:
                return _importer.Import(f);
        }
    }

    private IMemberRefParent MapParent(IMemberRefParent p) => p switch
    {
        ITypeDefOrRef t => MapType(t),
        _ => p,
    };
}
