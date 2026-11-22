using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.CecilX;
using Mono.CecilX.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace MiniNet.Weaver {
    public sealed class NetworkMessageWeaver : ILPostProcessor {
        private const string RuntimeAssembly = "MiniNet";
        private const string MessageInterface = "MiniNet.INetworkMessage";
        private const string SerializerType = "MiniNet.NetworkMessageSerializer`1";
        private const string GeneratedWriter = "<MiniNet>WriteMessage";
        private const string GeneratedReader = "<MiniNet>ReadMessage";

        public override ILPostProcessor GetInstance() => new NetworkMessageWeaver();

        public override bool WillProcess(ICompiledAssembly assembly) =>
            assembly.Name == RuntimeAssembly || assembly.References.Any(IsRuntimeReference);

        public override ILPostProcessResult Process(ICompiledAssembly compiled) {
            var diagnostics = new List<DiagnosticMessage>();
            try {
                using var resolver = new DefaultAssemblyResolver();
                foreach (string reference in compiled.References) {
                    resolver.AddSearchDirectory(Path.GetDirectoryName(reference));
                }

                var input = compiled.InMemoryAssembly;
                using var dll = new MemoryStream(input.PeData);
                using var pdb = new MemoryStream(input.PdbData ?? Array.Empty<byte>());
                bool hasSymbols = pdb.Length != 0;
                using var assembly = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters {
                    AssemblyResolver = resolver,
                    ReadSymbols = hasSymbols,
                    SymbolStream = hasSymbols ? pdb : null
                });
                var module = assembly.MainModule;
                using var runtime = compiled.Name == RuntimeAssembly ? null :
                    AssemblyDefinition.ReadAssembly(compiled.References.First(IsRuntimeReference));
                var serializer = (runtime?.MainModule ?? module).GetType(SerializerType);
                if (!Weave(module, serializer, diagnostics) || diagnostics.Count != 0) {
                    return diagnostics.Count == 0 ? null : new ILPostProcessResult(input, diagnostics);
                }

                using var output = new MemoryStream();
                using var symbols = new MemoryStream();
                assembly.Write(output, new WriterParameters {
                    WriteSymbols = hasSymbols,
                    SymbolWriterProvider = hasSymbols ? new PortablePdbWriterProvider() : null,
                    SymbolStream = hasSymbols ? symbols : null
                });
                return new ILPostProcessResult(new InMemoryAssembly(output.ToArray(), symbols.ToArray()), diagnostics);
            } catch (Exception exception) {
                AddError(diagnostics, $"MiniNet Weaver failed in {compiled.Name}: {exception}");
                return new ILPostProcessResult(compiled.InMemoryAssembly, diagnostics);
            }
        }

        private static bool IsRuntimeReference(string path) =>
            Path.GetFileNameWithoutExtension(path) == RuntimeAssembly;

        private static IEnumerable<TypeDefinition> GetTypes(IEnumerable<TypeDefinition> types) {
            foreach (var type in types) {
                yield return type;
                foreach (var nested in GetTypes(type.NestedTypes)) {
                    yield return nested;
                }
            }
        }

        private static bool Weave(ModuleDefinition module, TypeDefinition serializer, List<DiagnosticMessage> diagnostics) {
            var register = serializer.Methods.Single(method => method.Name == "Register");
            var writerDelegate = (GenericInstanceType)register.Parameters[0].ParameterType;
            var readerDelegate = (GenericInstanceType)register.Parameters[1].ParameterType;
            bool modified = false;
            foreach (var type in GetTypes(module.Types)) {
                modified |= WeaveGhostFields(module, type, writerDelegate.GenericArguments[0],
                    readerDelegate.GenericArguments[0], diagnostics);
                if (!type.Interfaces.Any(entry => entry.InterfaceType.FullName == MessageInterface) ||
                    type.Methods.Any(method => method.Name == GeneratedWriter)) {
                    continue;
                }
                if (!type.IsValueType || type.HasGenericParameters || type.IsExplicitLayout ||
                    type.CustomAttributes.Any(attribute =>
                        attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute")) {
                    AddError(diagnostics, $"MiniNet message {type.FullName} must be a non-generic struct with supported layout.");
                    continue;
                }

                var fields = type.Fields.Where(field => field.IsPublic && !field.IsStatic && !field.IsNotSerialized).ToArray();
                var fieldTypes = fields.Select(field => GetWireType(field.FieldType)).ToArray();
                bool valid = true;
                for (int index = 0; index < fields.Length; index++) {
                    var field = fields[index];
                    if (field.IsInitOnly || fieldTypes[index] == null) {
                        AddError(diagnostics, $"MiniNet cannot serialize {type.FullName}.{field.Name} ({field.FieldType.FullName}). Use a writable primitive, enum or string field.");
                        valid = false;
                    }
                }
                if (!valid) {
                    continue;
                }

                var writer = EmitMethod(module, type, writerDelegate.GenericArguments[0], fields, fieldTypes, true);
                var reader = EmitMethod(module, type, readerDelegate.GenericArguments[0], fields, fieldTypes, false);
                EmitRegistration(module, type, register, writerDelegate, readerDelegate, writer, reader);
                modified = true;
            }
            return modified;
        }

        private static bool WeaveGhostFields(ModuleDefinition module, TypeDefinition type,
            TypeReference writer, TypeReference reader, List<DiagnosticMessage> diagnostics) {
            var fields = type.Fields.Where(IsGhostField).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (fields.Length == 0) return false;

            var ghostBase = type.BaseType?.Resolve();
            while (ghostBase != null && ghostBase.FullName != "MiniNet.Replicate.GhostComponent")
                ghostBase = ghostBase.BaseType?.Resolve();
            if (ghostBase == null) {
                AddError(diagnostics, $"GhostField on {type.FullName} requires a GhostComponent.");
                return false;
            }
            bool NeedsMethod(string name, TypeReference buffer) => !type.Methods.Any(m =>
                m.Name == name && m.IsVirtual && !m.IsNewSlot && m.Parameters.Count == 2 &&
                m.Parameters[0].ParameterType.FullName == buffer.FullName &&
                m.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean);
            bool write = NeedsMethod("Serialize", writer);
            bool read = NeedsMethod("Deserialize", reader);
            if (!write && !read) return false;
            foreach (var field in fields) {
                if (field.IsStatic || field.IsInitOnly || GetWireType(field.FieldType) == null) {
                    AddError(diagnostics, $"Unsupported GhostField {field.FullName}. Expected a writable primitive, enum or string instance field.");
                    return false;
                }
            }
            int offset = 0;
            bool tracked = write && read;
            for (var parent = type.BaseType.Resolve(); parent.FullName != ghostBase.FullName; parent = parent.BaseType.Resolve()) {
                offset += parent.Fields.Count(IsGhostField);
                if (parent.Methods.Any(m => m.IsVirtual && (m.Name == "Serialize" || m.Name == "Deserialize")) &&
                    !parent.Methods.Any(m => m.Name == "DetectFieldChanges")) tracked = false;
            }
            if (tracked && offset + fields.Length > 64) {
                AddError(diagnostics, $"GhostComponent {type.FullName} exceeds 64 replicated fields including inherited fields.");
                return false;
            }
            if (tracked) EmitChangeDetection(module, type, ghostBase, fields, offset);
            if (write) EmitGhostMethod(module, type, ghostBase, writer, fields, true, tracked, offset);
            if (read) EmitGhostMethod(module, type, ghostBase, reader, fields, false, tracked, offset);
            return true;
        }

        private static bool IsGhostField(FieldDefinition field) {
            var marker = field.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == "MiniNet.Replicate.GhostFieldAttribute");
            return marker != null && !marker.Fields.Any(arg =>
                arg.Name == "Direction" && Convert.ToInt32(arg.Argument.Value) != 0);
        }

        private static TypeReference SelfType(TypeDefinition type) {
            if (!type.HasGenericParameters) return type;
            var owner = new GenericInstanceType(type);
            foreach (var parameter in type.GenericParameters) owner.GenericArguments.Add(parameter);
            return owner;
        }

        private static void EmitChangeDetection(ModuleDefinition module, TypeDefinition type,
            TypeDefinition root, FieldDefinition[] fields, int offset) {
            var prototype = root.Methods.Single(m => m.Name == "DetectFieldChanges");
            var result = module.ImportReference(prototype.ReturnType);
            var method = new MethodDefinition(prototype.Name, MethodAttributes.FamORAssem | MethodAttributes.Virtual |
                MethodAttributes.HideBySig, result);
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, new MethodReference(method.Name, result, type.BaseType) { HasThis = true });
            il.Emit(OpCodes.Pop);
            var self = SelfType(type);
            var track = module.ImportReference(root.Methods.Single(m => m.Name == "TrackField"));
            for (int i = 0; i < fields.Length; i++) {
                var field = fields[i];
                var shadow = new FieldDefinition("<MiniNet>Previous_" + field.Name, FieldAttributes.Private, field.FieldType);
                type.Fields.Add(shadow);
                var call = new GenericInstanceMethod(track);
                call.GenericArguments.Add(field.FieldType);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, new FieldReference(shadow.Name, shadow.FieldType, self));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, new FieldReference(field.Name, field.FieldType, self));
                il.Emit(OpCodes.Ldc_I8, 1L << (offset + i));
                il.Emit(OpCodes.Call, call);
            }
            il.Emit(OpCodes.Ldtoken, self);
            // Use the target's System.Type scope even when Unity runs the Weaver on CoreCLR.
            var getTypeFromHandle = new MethodReference(nameof(Type.GetTypeFromHandle), result, result);
            var handle = new TypeReference("System", "RuntimeTypeHandle", module, result.Scope, true);
            getTypeFromHandle.Parameters.Add(new ParameterDefinition(handle));
            il.Emit(OpCodes.Call, getTypeFromHandle);
            il.Emit(OpCodes.Ret);
        }

        private static void EmitGhostMethod(ModuleDefinition module, TypeDefinition type, TypeDefinition root,
            TypeReference buffer, FieldDefinition[] fields, bool writing, bool tracked, int offset) {
            var bufferType = module.ImportReference(buffer);
            string name = writing ? "Serialize" : "Deserialize";
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Virtual |
                MethodAttributes.HideBySig, module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition("buffer", ParameterAttributes.None, bufferType));
            method.Parameters.Add(new ParameterDefinition("initialState", ParameterAttributes.None, module.TypeSystem.Boolean));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();

            // Call through the immediate base so inherited fields precede this type's fields.
            var parent = new MethodReference(name, module.TypeSystem.Void, type.BaseType) { HasThis = true };
            parent.Parameters.Add(new ParameterDefinition(bufferType));
            parent.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, parent);

            foreach (var field in fields) {
                var next = il.Create(OpCodes.Nop);
                if (tracked) {
                    var include = il.Create(OpCodes.Nop);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Brtrue, include);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I8, 1L << offset++);
                    il.Emit(OpCodes.Call, module.ImportReference(root.Methods.Single(m => m.Name == "IncludesField")));
                    il.Emit(OpCodes.Brfalse, next);
                    il.Append(include);
                }
                var wire = module.ImportReference(GetWireType(field.FieldType));
                FieldReference member = field;
                if (type.HasGenericParameters) {
                    var owner = new GenericInstanceType(type);
                    foreach (var parameter in type.GenericParameters) owner.GenericArguments.Add(parameter);
                    member = new FieldReference(field.Name, field.FieldType, owner);
                }
                if (!writing) il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                if (writing) {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, member);
                }
                if (wire.MetadataType == MetadataType.String) {
                    string helper = writing ? "WriteGhostString" : "ReadGhostString";
                    il.Emit(OpCodes.Call, module.ImportReference(root.Methods.Single(m => m.Name == helper)));
                } else {
                    var operation = new MethodReference(writing ? "Write" : "Read" + wire.Name,
                        writing ? module.TypeSystem.Void : wire, bufferType) { HasThis = true };
                    if (writing) operation.Parameters.Add(new ParameterDefinition(wire));
                    il.Emit(OpCodes.Callvirt, operation);
                }
                if (!writing) il.Emit(OpCodes.Stfld, member);
                if (tracked) il.Append(next);
            }
            il.Emit(OpCodes.Ret);
        }

        private static TypeReference GetWireType(TypeReference type) {
            switch (type.MetadataType) {
                case MetadataType.Boolean:
                case MetadataType.Byte:
                case MetadataType.SByte:
                case MetadataType.Int16:
                case MetadataType.UInt16:
                case MetadataType.Int32:
                case MetadataType.UInt32:
                case MetadataType.Int64:
                case MetadataType.UInt64:
                case MetadataType.Single:
                case MetadataType.Double:
                case MetadataType.String:
                    return type;
                case MetadataType.ValueType:
                    var definition = type.Resolve();
                    return definition != null && definition.IsEnum
                        ? definition.Fields.Single(field => field.Name == "value__").FieldType
                        : null;
                default:
                    return null;
            }
        }

        private static MethodDefinition EmitMethod(ModuleDefinition module, TypeDefinition type, TypeReference buffer,
            FieldDefinition[] fields, TypeReference[] fieldTypes, bool writing) {
            var bufferType = module.ImportReference(buffer);
            var method = new MethodDefinition(writing ? GeneratedWriter : GeneratedReader,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                writing ? module.TypeSystem.Void : type);
            method.Parameters.Add(new ParameterDefinition(writing ? "writer" : "reader", ParameterAttributes.None, bufferType));
            type.Methods.Add(method);
            var il = method.Body.GetILProcessor();
            VariableDefinition value = null;
            if (writing) {
                method.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, type));
            } else {
                value = new VariableDefinition(type);
                method.Body.Variables.Add(value);
                il.Emit(OpCodes.Ldloca, value);
                il.Emit(OpCodes.Initobj, type);
            }
            for (int index = 0; index < fields.Length; index++) {
                var field = fields[index];
                var wireType = module.ImportReference(fieldTypes[index]);
                var operation = new MethodReference(writing ? "Write" : "Read" + wireType.Name,
                    writing ? module.TypeSystem.Void : wireType, bufferType) { HasThis = true };
                if (writing) {
                    operation.Parameters.Add(new ParameterDefinition(wireType));
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarga, method.Parameters[1]);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Callvirt, operation);
                } else {
                    il.Emit(OpCodes.Ldloca, value);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Callvirt, operation);
                    il.Emit(OpCodes.Stfld, field);
                }
            }
            if (!writing) {
                il.Emit(OpCodes.Ldloc, value);
            }
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static void EmitRegistration(ModuleDefinition module, TypeDefinition type, MethodDefinition register,
            GenericInstanceType writerDelegate, GenericInstanceType readerDelegate, MethodDefinition writer, MethodDefinition reader) {
            var initializer = type.Methods.SingleOrDefault(method => method.IsConstructor && method.IsStatic);
            if (initializer == null) {
                initializer = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static |
                    MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
                type.Methods.Add(initializer);
                initializer.Body.GetILProcessor().Emit(OpCodes.Ret);
            }

            var serializer = new GenericInstanceType(module.ImportReference(register.DeclaringType));
            serializer.GenericArguments.Add(type);
            var registerCall = new MethodReference(register.Name, module.TypeSystem.Void, serializer);
            foreach (var parameter in module.ImportReference(register).Parameters) {
                registerCall.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            var il = initializer.Body.GetILProcessor();
            var first = initializer.Body.Instructions[0];
            InsertDelegate(module, type, writerDelegate, writer, il, first);
            InsertDelegate(module, type, readerDelegate, reader, il, first);
            il.InsertBefore(first, il.Create(OpCodes.Call, registerCall));
        }

        private static void InsertDelegate(ModuleDefinition module, TypeDefinition type, GenericInstanceType openDelegate,
            MethodDefinition target, ILProcessor il, Instruction before) {
            var closedDelegate = new GenericInstanceType(module.ImportReference(openDelegate.ElementType));
            closedDelegate.GenericArguments.Add(module.ImportReference(openDelegate.GenericArguments[0]));
            closedDelegate.GenericArguments.Add(type);

            var constructor = new MethodReference(".ctor", module.TypeSystem.Void, closedDelegate) { HasThis = true };
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            il.InsertBefore(before, il.Create(OpCodes.Ldnull));
            il.InsertBefore(before, il.Create(OpCodes.Ldftn, target));
            il.InsertBefore(before, il.Create(OpCodes.Newobj, constructor));
        }

        private static void AddError(List<DiagnosticMessage> diagnostics, string message) {
            diagnostics.Add(new DiagnosticMessage { DiagnosticType = DiagnosticType.Error, MessageData = message });
        }
    }
}
