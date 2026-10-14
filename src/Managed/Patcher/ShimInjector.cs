using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ShvdnCompat
{
    /// <summary>
    /// Copies the shims of a shim assembly into an SHVDN API assembly. Only members the API assembly lacks
    /// are added; members it already has are left untouched, so the result is always a strict superset of
    /// the original API.
    /// </summary>
    internal sealed class ShimInjector
    {
        public const string MarkerNamespace = "ShvdnLegacyCompat";
        public const string MarkerName = "PatchInfo";

        private readonly ModuleDefinition _target;
        private readonly ModuleDefinition _shims;
        private readonly string _apiAssemblyName;
        private readonly Dictionary<TypeDefinition, TypeDefinition> _typeMap = new Dictionary<TypeDefinition, TypeDefinition>();
        private readonly Dictionary<MethodDefinition, MethodDefinition> _methodMap = new Dictionary<MethodDefinition, MethodDefinition>();
        private readonly Dictionary<FieldDefinition, FieldDefinition> _fieldMap = new Dictionary<FieldDefinition, FieldDefinition>();
        private readonly HashSet<MethodDefinition> _added = new HashSet<MethodDefinition>();

        public List<string> Added { get; } = new List<string>();
        public List<string> Skipped { get; } = new List<string>();

        public ShimInjector(ModuleDefinition target, ModuleDefinition shims)
        {
            _target = target;
            _shims = shims;
            _apiAssemblyName = target.Assembly.Name.Name;
        }

        public static bool IsPatched(ModuleDefinition module)
        {
            return module.Types.Any(t => t.Namespace == MarkerNamespace && t.Name == MarkerName);
        }

        public void Run(string patchInfo)
        {
            InjectTypes();
            InjectMemberShims();
            RemoveDanglingShims();
            AddMarker(patchInfo);
        }

        // ---------------------------------------------------------------------------------------------
        // Whole types ([InjectType])
        // ---------------------------------------------------------------------------------------------

        private void InjectTypes()
        {
            foreach (TypeDefinition src in _shims.Types)
            {
                string fullName = GetAttributeString(src, "InjectTypeAttribute");
                if (fullName == null) continue;

                if (Signatures.FindType(_target, fullName) != null)
                {
                    Skipped.Add($"type {fullName}: already defined by the API");
                    continue;
                }

                int lastDot = fullName.LastIndexOf('.');
                string ns = lastDot < 0 ? string.Empty : fullName.Substring(0, lastDot);
                string name = fullName.Substring(lastDot + 1);

                var created = new List<TypeDefinition>();
                TypeDefinition dst = CreateTypeSkeleton(src, ns, name, created);
                _target.Types.Add(dst);
                try
                {
                    foreach (TypeDefinition t in created) FillTypeMembers(t);
                    foreach (TypeDefinition t in created) FillTypeBodies(t);
                    Added.Add($"type {fullName}");
                }
                catch (ShimException e)
                {
                    _target.Types.Remove(dst);
                    foreach (TypeDefinition t in created)
                    {
                        TypeDefinition key = _typeMap.First(kv => kv.Value == t).Key;
                        _typeMap.Remove(key);
                        foreach (MethodDefinition m in key.Methods) _methodMap.Remove(m);
                        foreach (FieldDefinition f in key.Fields) _fieldMap.Remove(f);
                    }
                    Skipped.Add($"type {fullName}: {e.Message}");
                }
            }
        }

        private TypeDefinition CreateTypeSkeleton(TypeDefinition src, string ns, string name, List<TypeDefinition> created)
        {
            if (src.HasGenericParameters) throw new ShimException($"generic shim types are not supported ({src.FullName})");

            var dst = new TypeDefinition(ns, name, src.Attributes);
            _typeMap[src] = dst;
            created.Add(dst);
            foreach (TypeDefinition nested in src.NestedTypes)
            {
                dst.NestedTypes.Add(CreateTypeSkeleton(nested, string.Empty, nested.Name, created));
            }
            return dst;
        }

        private void FillTypeMembers(TypeDefinition dst)
        {
            TypeDefinition src = _typeMap.First(kv => kv.Value == dst).Key;
            dst.BaseType = src.BaseType == null ? null : MapType(src.BaseType);
            foreach (InterfaceImplementation iface in src.Interfaces)
            {
                dst.Interfaces.Add(new InterfaceImplementation(MapType(iface.InterfaceType)));
            }
            dst.PackingSize = src.PackingSize;
            dst.ClassSize = src.ClassSize;

            foreach (FieldDefinition f in src.Fields)
            {
                var nf = new FieldDefinition(f.Name, f.Attributes, MapType(f.FieldType));
                if (f.HasConstant) nf.Constant = f.Constant;
                if (f.InitialValue != null && f.InitialValue.Length > 0) nf.InitialValue = f.InitialValue;
                if (f.HasLayoutInfo) nf.Offset = f.Offset;
                dst.Fields.Add(nf);
                _fieldMap[f] = nf;
            }

            foreach (MethodDefinition m in src.Methods)
            {
                MethodDefinition nm = CloneSignature(m, m.Name, m.Attributes, dropFirstParameter: false);
                dst.Methods.Add(nm);
                _methodMap[m] = nm;
            }

            foreach (PropertyDefinition p in src.Properties)
            {
                var np = new PropertyDefinition(p.Name, p.Attributes, MapType(p.PropertyType));
                if (p.GetMethod != null) np.GetMethod = _methodMap[p.GetMethod];
                if (p.SetMethod != null) np.SetMethod = _methodMap[p.SetMethod];
                foreach (ParameterDefinition ip in p.Parameters) np.Parameters.Add(new ParameterDefinition(ip.Name, ip.Attributes, MapType(ip.ParameterType)));
                dst.Properties.Add(np);
            }

            foreach (EventDefinition e in src.Events)
            {
                var ne = new EventDefinition(e.Name, e.Attributes, MapType(e.EventType));
                if (e.AddMethod != null) ne.AddMethod = _methodMap[e.AddMethod];
                if (e.RemoveMethod != null) ne.RemoveMethod = _methodMap[e.RemoveMethod];
                if (e.InvokeMethod != null) ne.InvokeMethod = _methodMap[e.InvokeMethod];
                dst.Events.Add(ne);
            }
        }

        private void FillTypeBodies(TypeDefinition dst)
        {
            TypeDefinition src = _typeMap.First(kv => kv.Value == dst).Key;
            foreach (MethodDefinition m in src.Methods)
            {
                MethodDefinition nm = _methodMap[m];
                foreach (MethodReference ov in m.Overrides) nm.Overrides.Add(MapMethod(ov));
                if (m.HasBody) CloneBody(m, nm, instanceShim: false);
                _added.Add(nm);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Member shims ([ShimType] + [Shim])
        // ---------------------------------------------------------------------------------------------

        private void InjectMemberShims()
        {
            var pending = new List<(MethodDefinition src, MethodDefinition dst, bool instance)>();

            foreach (TypeDefinition holder in _shims.Types)
            {
                string targetName = GetAttributeString(holder, "ShimTypeAttribute");
                if (targetName == null) continue;

                TypeDefinition targetType = Signatures.FindType(_target, targetName);
                if (targetType == null)
                {
                    Skipped.Add($"{targetName}: type not found in {_apiAssemblyName}");
                    continue;
                }

                foreach (MethodDefinition src in holder.Methods)
                {
                    CustomAttribute shimAttr = src.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "ShimAttribute");
                    if (shimAttr == null) continue;

                    bool instance = GetNamedBool(shimAttr, "Instance");
                    string name = GetNamedString(shimAttr, "Name") ?? src.Name;
                    string display = $"{targetName}::{name}";
                    try
                    {
                        if (!src.IsStatic) throw new ShimException("shim methods must be static");
                        if (src.HasGenericParameters) throw new ShimException("generic shims are not supported");
                        if (instance) ValidateSelfParameter(src, targetType);

                        var attrs = MethodAttributes.Public | MethodAttributes.HideBySig;
                        if (!instance) attrs |= MethodAttributes.Static;
                        if (name.StartsWith("get_") || name.StartsWith("set_") || name.StartsWith("op_")) attrs |= MethodAttributes.SpecialName;

                        MethodDefinition dst = CloneSignature(src, name, attrs, dropFirstParameter: instance);
                        dst.HasThis = instance;
                        string sig = Signatures.Of(dst);
                        if (targetType.Methods.Any(m => Signatures.Of(m) == sig))
                        {
                            Skipped.Add($"{display}: already present");
                            continue;
                        }

                        targetType.Methods.Add(dst);
                        _methodMap[src] = dst;
                        pending.Add((src, dst, instance));
                    }
                    catch (ShimException e)
                    {
                        Skipped.Add($"{display}: {e.Message}");
                    }
                }
            }

            foreach (var (src, dst, instance) in pending)
            {
                try
                {
                    CloneBody(src, dst, instance);
                    AttachProperty(dst);
                    _added.Add(dst);
                    Added.Add($"{dst.DeclaringType.FullName}::{Signatures.Of(dst)}");
                }
                catch (ShimException e)
                {
                    dst.DeclaringType.Methods.Remove(dst);
                    _methodMap.Remove(src);
                    Skipped.Add($"{dst.DeclaringType.FullName}::{dst.Name}: {e.Message}");
                }
            }
        }

        private static void ValidateSelfParameter(MethodDefinition src, TypeDefinition targetType)
        {
            if (src.Parameters.Count == 0) throw new ShimException("instance shims need the target as first parameter");
            TypeReference self = src.Parameters[0].ParameterType;
            if (targetType.IsValueType)
            {
                if (!(self is ByReferenceType br) || br.ElementType.FullName != targetType.FullName)
                    throw new ShimException($"first parameter must be 'ref {targetType.FullName}'");
            }
            else if (self.FullName != targetType.FullName)
            {
                throw new ShimException($"first parameter must be '{targetType.FullName}'");
            }
        }

        /// <summary>Exposes get_/set_ shims as properties so reflection-based scripts see them too.</summary>
        private void AttachProperty(MethodDefinition accessor)
        {
            bool isGet = accessor.Name.StartsWith("get_");
            bool isSet = accessor.Name.StartsWith("set_");
            if (!isGet && !isSet) return;
            if (isGet && accessor.Parameters.Count != 0) return;
            if (isSet && accessor.Parameters.Count != 1) return;

            string propName = accessor.Name.Substring(4);
            TypeReference propType = isGet ? accessor.ReturnType : accessor.Parameters[0].ParameterType;
            TypeDefinition owner = accessor.DeclaringType;
            string typeSig = Signatures.Of(propType);

            PropertyDefinition prop = owner.Properties.FirstOrDefault(p => p.Name == propName);
            if (prop != null && (Signatures.Of(prop.PropertyType) != typeSig || prop.HasParameters))
            {
                // A property with this name but another type already exists (e.g. an int setter that became an
                // enum setter). A second property would make Type.GetProperty(name) ambiguous, so the old
                // accessor stays a plain method; compiled callers bind to the method anyway.
                return;
            }
            if (prop == null)
            {
                prop = new PropertyDefinition(propName, PropertyAttributes.None, propType) { HasThis = accessor.HasThis };
                owner.Properties.Add(prop);
            }
            if (isGet && prop.GetMethod == null) prop.GetMethod = accessor;
            if (isSet && prop.SetMethod == null) prop.SetMethod = accessor;
        }

        /// <summary>Removes added methods whose bodies call shims that failed to inject.</summary>
        private void RemoveDanglingShims()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (MethodDefinition m in _added.ToList())
                {
                    if (m.DeclaringType == null || !m.HasBody) continue;
                    foreach (Instruction ins in m.Body.Instructions)
                    {
                        if (ins.Operand is MethodDefinition callee && callee.Module == null)
                        {
                            Skipped.Add($"{m.DeclaringType.FullName}::{m.Name}: depends on {callee.Name}, which was not injected");
                            Added.Remove($"{m.DeclaringType.FullName}::{Signatures.Of(m)}");
                            m.DeclaringType.Methods.Remove(m);
                            _added.Remove(m);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        private void AddMarker(string patchInfo)
        {
            if (IsPatched(_target)) return;
            var marker = new TypeDefinition(MarkerNamespace, MarkerName,
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                _target.TypeSystem.Object);
            marker.Fields.Add(new FieldDefinition("Info", FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, _target.TypeSystem.String)
            {
                Constant = patchInfo,
            });
            _target.Types.Add(marker);
        }

        // ---------------------------------------------------------------------------------------------
        // Cloning
        // ---------------------------------------------------------------------------------------------

        private MethodDefinition CloneSignature(MethodDefinition src, string name, MethodAttributes attrs, bool dropFirstParameter)
        {
            if (src.HasGenericParameters) throw new ShimException($"generic methods are not supported ({src.FullName})");

            var dst = new MethodDefinition(name, attrs, MapType(src.ReturnType))
            {
                ImplAttributes = src.ImplAttributes,
                HasThis = src.HasThis,
                ExplicitThis = src.ExplicitThis,
                CallingConvention = src.CallingConvention,
            };
            for (int i = dropFirstParameter ? 1 : 0; i < src.Parameters.Count; i++)
            {
                ParameterDefinition p = src.Parameters[i];
                var np = new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType));
                if (p.HasConstant) np.Constant = p.Constant;
                dst.Parameters.Add(np);
            }
            return dst;
        }

        private void CloneBody(MethodDefinition src, MethodDefinition dst, bool instanceShim)
        {
            MethodBody sb = src.Body;
            var db = new MethodBody(dst)
            {
                InitLocals = sb.InitLocals,
                MaxStackSize = sb.MaxStackSize,
            };
            dst.Body = db;

            foreach (VariableDefinition v in sb.Variables) db.Variables.Add(new VariableDefinition(MapType(v.VariableType)));

            var insMap = new Dictionary<Instruction, Instruction>();
            foreach (Instruction ins in sb.Instructions)
            {
                Instruction copy = Instruction.Create(OpCodes.Nop);
                copy.OpCode = ins.OpCode;
                copy.Offset = ins.Offset;
                insMap[ins] = copy;
                db.Instructions.Add(copy);
            }

            foreach (Instruction ins in sb.Instructions)
            {
                insMap[ins].Operand = MapOperand(ins.Operand, src, dst, instanceShim, insMap, db);
            }

            foreach (ExceptionHandler h in sb.ExceptionHandlers)
            {
                db.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType)
                {
                    TryStart = MapIns(h.TryStart, insMap),
                    TryEnd = MapIns(h.TryEnd, insMap),
                    HandlerStart = MapIns(h.HandlerStart, insMap),
                    HandlerEnd = MapIns(h.HandlerEnd, insMap),
                    FilterStart = MapIns(h.FilterStart, insMap),
                    CatchType = h.CatchType == null ? null : MapType(h.CatchType),
                });
            }
        }

        private static Instruction MapIns(Instruction ins, Dictionary<Instruction, Instruction> map)
        {
            return ins == null ? null : map[ins];
        }

        private object MapOperand(object operand, MethodDefinition src, MethodDefinition dst, bool instanceShim,
            Dictionary<Instruction, Instruction> insMap, MethodBody db)
        {
            switch (operand)
            {
                case null:
                    return null;
                case Instruction target:
                    return insMap[target];
                case Instruction[] targets:
                    return targets.Select(t => insMap[t]).ToArray();
                case VariableDefinition v:
                    return db.Variables[v.Index];
                case ParameterDefinition p:
                    return MapParameter(p, src, dst, instanceShim, db);
                case TypeReference t:
                    return MapType(t);
                case MethodReference m:
                    return MapMethod(m);
                case FieldReference f:
                    return MapField(f);
                case CallSite cs:
                    var ncs = new CallSite(MapType(cs.ReturnType)) { HasThis = cs.HasThis, ExplicitThis = cs.ExplicitThis, CallingConvention = cs.CallingConvention };
                    foreach (ParameterDefinition cp in cs.Parameters) ncs.Parameters.Add(new ParameterDefinition(MapType(cp.ParameterType)));
                    return ncs;
                default:
                    return operand; // string, numeric constants
            }
        }

        private static ParameterDefinition MapParameter(ParameterDefinition p, MethodDefinition src, MethodDefinition dst, bool instanceShim, MethodBody db)
        {
            if (src.HasThis && p == src.Body.ThisParameter) return db.ThisParameter;
            int index = p.Index;
            if (instanceShim)
            {
                // Static shim parameter 0 is the instance and becomes 'this'.
                if (index == 0) return db.ThisParameter;
                index--;
            }
            return dst.Parameters[index];
        }

        private TypeReference MapType(TypeReference type)
        {
            switch (type)
            {
                case null:
                    return null;
                case GenericParameter gp:
                    return MapGenericParameter(gp);
                case ArrayType at:
                    var na = new ArrayType(MapType(at.ElementType), at.Rank);
                    if (!at.IsVector)
                    {
                        na.Dimensions.Clear();
                        foreach (ArrayDimension d in at.Dimensions) na.Dimensions.Add(d);
                    }
                    return na;
                case ByReferenceType br:
                    return new ByReferenceType(MapType(br.ElementType));
                case PointerType pt:
                    return new PointerType(MapType(pt.ElementType));
                case PinnedType pin:
                    return new PinnedType(MapType(pin.ElementType));
                case RequiredModifierType req:
                    return new RequiredModifierType(MapType(req.ModifierType), MapType(req.ElementType));
                case OptionalModifierType opt:
                    return new OptionalModifierType(MapType(opt.ModifierType), MapType(opt.ElementType));
                case SentinelType sen:
                    return new SentinelType(MapType(sen.ElementType));
                case GenericInstanceType git:
                    var ng = new GenericInstanceType(MapType(git.ElementType));
                    foreach (TypeReference arg in git.GenericArguments) ng.GenericArguments.Add(MapType(arg));
                    return ng;
                case TypeSpecification spec:
                    throw new ShimException($"unsupported type form {spec.GetType().Name} ({spec.FullName})");
            }

            if (IsShimModuleType(type))
            {
                TypeDefinition def = type as TypeDefinition ?? type.Resolve();
                if (def != null && _typeMap.TryGetValue(def, out TypeDefinition mapped)) return mapped;
                throw new ShimException($"references shim-only type {type.FullName} (only [InjectType] types can be referenced)");
            }

            if (IsApiScope(type))
            {
                TypeDefinition def = Signatures.FindType(_target, type);
                if (def == null) throw new ShimException($"{type.FullName} does not exist in {_apiAssemblyName}");
                return def;
            }

            return _target.ImportReference(type);
        }

        private MethodReference MapMethod(MethodReference method)
        {
            if (method is GenericInstanceMethod gim)
            {
                var ng = new GenericInstanceMethod(MapMethod(gim.ElementMethod));
                foreach (TypeReference arg in gim.GenericArguments) ng.GenericArguments.Add(MapType(arg));
                return ng;
            }

            if (IsShimModuleType(method.DeclaringType))
            {
                MethodDefinition def = method as MethodDefinition ?? method.Resolve();
                if (def != null && _methodMap.TryGetValue(def, out MethodDefinition mapped)) return mapped;
                throw new ShimException($"calls {method.FullName}, which is not an injected member");
            }

            TypeReference declaring = MapType(method.DeclaringType);
            if (declaring is TypeDefinition localType && localType.Module == _target)
            {
                MethodReference probe = CreateMethodReference(method, declaring);
                MethodDefinition found = Signatures.FindMethod(localType, Signatures.Of(probe));
                if (found == null) throw new ShimException($"{localType.FullName}::{Signatures.Of(probe)} does not exist in {_apiAssemblyName}");
                return found;
            }

            return CreateMethodReference(method, declaring);
        }

        // Generic parameters inside a member signature (e.g. Func<T, TResult>::Invoke returning !1) must be
        // owned by the new reference / its declaring type, so they are mapped against this context.
        private MethodReference _genericMethodContext;
        private TypeReference _genericTypeContext;

        private MethodReference CreateMethodReference(MethodReference method, TypeReference declaring)
        {
            var reference = new MethodReference(method.Name, _target.TypeSystem.Void, declaring)
            {
                HasThis = method.HasThis,
                ExplicitThis = method.ExplicitThis,
                CallingConvention = method.CallingConvention,
            };
            foreach (GenericParameter gp in method.GenericParameters) reference.GenericParameters.Add(new GenericParameter(gp.Name, reference));

            MethodReference savedMethod = _genericMethodContext;
            TypeReference savedType = _genericTypeContext;
            _genericMethodContext = reference;
            _genericTypeContext = declaring is GenericInstanceType git ? git.ElementType : declaring;
            try
            {
                reference.ReturnType = MapType(method.ReturnType);
                foreach (ParameterDefinition p in method.Parameters) reference.Parameters.Add(new ParameterDefinition(MapType(p.ParameterType)));
            }
            finally
            {
                _genericMethodContext = savedMethod;
                _genericTypeContext = savedType;
            }
            return reference;
        }

        private TypeReference MapGenericParameter(GenericParameter gp)
        {
            IGenericParameterProvider owner = gp.Type == GenericParameterType.Method ? (IGenericParameterProvider)_genericMethodContext : _genericTypeContext;
            if (owner == null || !owner.HasGenericParameters || owner.GenericParameters.Count <= gp.Position)
                throw new ShimException($"generic parameter {gp.FullName} used outside of a generic member reference");
            return owner.GenericParameters[gp.Position];
        }

        private FieldReference MapField(FieldReference field)
        {
            if (IsShimModuleType(field.DeclaringType))
            {
                FieldDefinition def = field as FieldDefinition ?? field.Resolve();
                if (def != null && _fieldMap.TryGetValue(def, out FieldDefinition mapped)) return mapped;
                throw new ShimException($"uses field {field.FullName}, which is not injected");
            }

            TypeReference declaring = MapType(field.DeclaringType);
            if (declaring is TypeDefinition localType && localType.Module == _target)
            {
                FieldDefinition found = localType.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (found == null) throw new ShimException($"field {localType.FullName}::{field.Name} does not exist in {_apiAssemblyName}");
                return found;
            }
            TypeReference savedType = _genericTypeContext;
            _genericTypeContext = declaring is GenericInstanceType git ? git.ElementType : declaring;
            try
            {
                return new FieldReference(field.Name, MapType(field.FieldType), declaring);
            }
            finally
            {
                _genericTypeContext = savedType;
            }
        }

        private bool IsShimModuleType(TypeReference type)
        {
            if (type is TypeDefinition def) return def.Module == _shims;
            return type.Scope == _shims || (type.Scope is ModuleDefinition md && md == _shims);
        }

        private bool IsApiScope(TypeReference type)
        {
            if (type is TypeDefinition def) return def.Module == _target;
            IMetadataScope scope = type.Scope;
            if (type.DeclaringType != null) return IsApiScope(type.DeclaringType);
            return scope is AssemblyNameReference anr && string.Equals(anr.Name, _apiAssemblyName, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------------------------------
        // Attribute helpers (matched by name; the shim attributes are internal to each shim assembly)
        // ---------------------------------------------------------------------------------------------

        private static string GetAttributeString(ICustomAttributeProvider provider, string attributeName)
        {
            CustomAttribute attr = provider.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == attributeName);
            return attr == null ? null : (string)attr.ConstructorArguments[0].Value;
        }

        private static bool GetNamedBool(CustomAttribute attr, string name)
        {
            foreach (CustomAttributeNamedArgument p in attr.Properties) if (p.Name == name) return (bool)p.Argument.Value;
            return false;
        }

        private static string GetNamedString(CustomAttribute attr, string name)
        {
            foreach (CustomAttributeNamedArgument p in attr.Properties) if (p.Name == name) return (string)p.Argument.Value;
            return null;
        }
    }

    internal sealed class ShimException : Exception
    {
        public ShimException(string message) : base(message) { }
    }
}
