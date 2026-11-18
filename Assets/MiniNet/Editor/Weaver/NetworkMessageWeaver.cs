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
