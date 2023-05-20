﻿using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Core.Meta;
using Core.Meta.Extensions;
using Core.Parser;

namespace Core.Generators.TypeScript
{
    public class TypeScriptGenerator : BaseGenerator
    {
        const int indentStep = 2;

        public TypeScriptGenerator(BebopSchema schema) : base(schema) { }

        private static string FormatDocumentation(string documentation, string deprecationReason, int spaces)
        {
            var builder = new IndentedStringBuilder();
            builder.Indent(spaces);
            builder.AppendLine("/**");
            builder.Indent(1);
            foreach (var line in documentation.GetLines())
            {
                builder.AppendLine($"* {line}");
            }
            if (!string.IsNullOrWhiteSpace(deprecationReason))
            {
                builder.AppendLine($"* @deprecated {deprecationReason}");
            }
            builder.AppendLine("*/");
            return builder.ToString();
        }

        private static string FormatDeprecationDoc(string deprecationReason, int spaces)
        {
            var builder = new IndentedStringBuilder();
            builder.Indent(spaces);
            builder.AppendLine("/**");
            builder.Indent(1);
            builder.AppendLine($"* @deprecated {deprecationReason}");
            builder.AppendLine("*/");
            return builder.ToString();
        }

        /// <summary>
        /// Generate the body of the <c>encode</c> function for the given <see cref="Definition"/>.
        /// </summary>
        /// <param name="definition">The definition to generate code for.</param>
        /// <returns>The generated TypeScript <c>encode</c> function body.</returns>
        public string CompileEncode(Definition definition)
        {
            return definition switch
            {
                MessageDefinition d => CompileEncodeMessage(d),
                StructDefinition d => CompileEncodeStruct(d),
                UnionDefinition d => CompileEncodeUnion(d),
                _ => throw new InvalidOperationException($"invalid CompileEncode value: {definition}"),
            };
        }

        private string CompileEncodeMessage(MessageDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            builder.AppendLine($"const pos = view.reserveMessageLength();");
            builder.AppendLine($"const start = view.length;");
            foreach (var field in definition.Fields)
            {
                if (field.DeprecatedAttribute != null)
                {
                    continue;
                }
                builder.AppendLine($"if (record.{field.Name.ToCamelCase()} != null) {{");
                builder.AppendLine($"  view.writeByte({field.ConstantValue});");
                builder.AppendLine($"  {CompileEncodeField(field.Type, $"record.{field.Name.ToCamelCase()}")}");
                builder.AppendLine($"}}");
            }
            builder.AppendLine("view.writeByte(0);");
            builder.AppendLine("const end = view.length;");
            builder.AppendLine("view.fillMessageLength(pos, end - start);");
            return builder.ToString();
        }

        private string CompileEncodeStruct(StructDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            foreach (var field in definition.Fields)
            {
                builder.AppendLine(CompileEncodeField(field.Type, $"record.{field.Name.ToCamelCase()}"));
            }
            return builder.ToString();
        }

        private string CompileEncodeUnion(UnionDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            builder.AppendLine($"const pos = view.reserveMessageLength();");
            builder.AppendLine($"const start = view.length + 1;");
            builder.AppendLine($"view.writeByte(record.data.discriminator);");
            builder.AppendLine($"switch (record.data.discriminator) {{");
            foreach (var branch in definition.Branches)
            {
                builder.AppendLine($"  case {branch.Discriminator}:");
                builder.AppendLine($"    {branch.ClassName()}.encodeInto(record.data.value, view);");
                builder.AppendLine($"    break;");
            }
            builder.AppendLine("}");
            builder.AppendLine("const end = view.length;");
            builder.AppendLine("view.fillMessageLength(pos, end - start);");
            return builder.ToString();
        }

        private string CompileEncodeField(TypeBase type, string target, int depth = 0, int indentDepth = 0)
        {
            var tab = new string(' ', indentStep);
            var nl = "\n" + new string(' ', indentDepth * indentStep);
            var i = GeneratorUtils.LoopVariable(depth);
            return type switch
            {
                ArrayType at when at.IsBytes() => $"view.writeBytes({target});",
                ArrayType at =>
                    $"{{" + nl +
                    $"{tab}const length{depth} = {target}.length;" + nl +
                    $"{tab}view.writeUint32(length{depth});" + nl +
                    $"{tab}for (let {i} = 0; {i} < length{depth}; {i}++) {{" + nl +
                    $"{tab}{tab}{CompileEncodeField(at.MemberType, $"{target}[{i}]", depth + 1, indentDepth + 2)}" + nl +
                    $"{tab}}}" + nl +
                    $"}}",
                MapType mt =>
                    $"view.writeUint32({target}.size);" + nl +
                    $"for (const [k{depth}, v{depth}] of {target}) {{" + nl +
                    $"{tab}{CompileEncodeField(mt.KeyType, $"k{depth}", depth + 1, indentDepth + 1)}" + nl +
                    $"{tab}{CompileEncodeField(mt.ValueType, $"v{depth}", depth + 1, indentDepth + 1)}" + nl +
                    $"}}",
                ScalarType st => st.BaseType switch
                {
                    BaseType.Bool => $"view.writeByte(Number({target}));",
                    BaseType.Byte => $"view.writeByte({target});",
                    BaseType.UInt16 => $"view.writeUint16({target});",
                    BaseType.Int16 => $"view.writeInt16({target});",
                    BaseType.UInt32 => $"view.writeUint32({target});",
                    BaseType.Int32 => $"view.writeInt32({target});",
                    BaseType.UInt64 => $"view.writeUint64({target});",
                    BaseType.Int64 => $"view.writeInt64({target});",
                    BaseType.Float32 => $"view.writeFloat32({target});",
                    BaseType.Float64 => $"view.writeFloat64({target});",
                    BaseType.String => $"view.writeString({target});",
                    BaseType.Guid => $"view.writeGuid({target});",
                    BaseType.Date => $"view.writeDate({target});",
                    _ => throw new ArgumentOutOfRangeException(st.BaseType.ToString())
                },
                DefinedType dt when Schema.Definitions[dt.Name] is EnumDefinition ed =>
                    CompileEncodeField(ed.ScalarType, target, depth, indentDepth),
                DefinedType dt => $"{dt.Name}.encodeInto({target}, view)",
                _ => throw new InvalidOperationException($"CompileEncodeField: {type}")
            };
        }

        /// <summary>
        /// Generate the body of the <c>decode</c> function for the given <see cref="FieldsDefinition"/>.
        /// </summary>
        /// <param name="definition">The definition to generate code for.</param>
        /// <returns>The generated TypeScript <c>decode</c> function body.</returns>
        public string CompileDecode(Definition definition)
        {
            return definition switch
            {
                MessageDefinition d => CompileDecodeMessage(d),
                StructDefinition d => CompileDecodeStruct(d),
                UnionDefinition d => CompileDecodeUnion(d),
                _ => throw new InvalidOperationException($"invalid CompileDecode value: {definition}"),
            };
        }

        /// <summary>
        /// Generate the body of the <c>decode</c> function for the given <see cref=MessageDefinition"/>,
        /// </summary>
        /// <param name="definition">The message definition to generate code for.</param>
        /// <returns>The generated TypeScript <c>decode</c> function body.</returns>
        private string CompileDecodeMessage(MessageDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            var discString = string.Empty;
            builder.AppendLine($"let message: I{definition.Name} = {{}};");
            builder.AppendLine("const length = view.readMessageLength();");
            builder.AppendLine("const end = view.index + length;");
            builder.AppendLine("while (true) {");
            builder.Indent(2);
            builder.AppendLine("switch (view.readByte()) {");
            builder.AppendLine("  case 0:");
            builder.AppendLine("    return new this(message);");
            builder.AppendLine("");
            foreach (var field in definition.Fields)
            {
                builder.AppendLine($"  case {field.ConstantValue}:");
                builder.AppendLine($"    {CompileDecodeField(field.Type, $"message.{field.Name.ToCamelCase()}")}");
                builder.AppendLine("    break;");
                builder.AppendLine("");
            }
            builder.AppendLine("  default:");
            builder.AppendLine("    view.index = end;");
            builder.AppendLine("    return new this(message);");
            builder.AppendLine("}");
            builder.Dedent(2);
            builder.AppendLine("}");
            return builder.ToString();
        }

        private string CompileDecodeStruct(StructDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            int i = 0;
            foreach (var field in definition.Fields)
            {
                builder.AppendLine($"let field{i}: {TypeName(field.Type)};");
                builder.AppendLine(CompileDecodeField(field.Type, $"field{i}"));
                i++;
            }
            builder.AppendLine($"let message: I{definition.ClassName()} = {{");
            i = 0;
            foreach (var field in definition.Fields)
            {
                builder.AppendLine($"  {field.Name.ToCamelCase()}: field{i},");
                i++;
            }
            builder.AppendLine("};");
            builder.AppendLine("return new this(message);");
            return builder.ToString();
        }

        private string CompileDecodeUnion(UnionDefinition definition)
        {
            var builder = new IndentedStringBuilder(0);
            builder.AppendLine("const length = view.readMessageLength();");
            builder.AppendLine("const end = view.index + 1 + length;");
            builder.AppendLine("switch (view.readByte()) {");
            foreach (var branch in definition.Branches)
            {
                builder.AppendLine($"  case {branch.Discriminator}:");
                builder.AppendLine($"    return this.from{branch.Definition.ClassName()}({branch.Definition.ClassName()}.readFrom(view));");
            }
            builder.AppendLine("  default:");
            builder.AppendLine("    view.index = end;");
            builder.AppendLine($"    throw new BebopRuntimeError(\"Unrecognized discriminator while decoding {definition.Name}\");");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private string ReadBaseType(BaseType baseType)
        {
            return baseType switch
            {
                BaseType.Bool => "!!view.readByte()",
                BaseType.Byte => "view.readByte()",
                BaseType.UInt32 => "view.readUint32()",
                BaseType.Int32 => "view.readInt32()",
                BaseType.Float32 => "view.readFloat32()",
                BaseType.String => "view.readString()",
                BaseType.Guid => "view.readGuid()",
                BaseType.UInt16 => "view.readUint16()",
                BaseType.Int16 => "view.readInt16()",
                BaseType.UInt64 => "view.readUint64()",
                BaseType.Int64 => "view.readInt64()",
                BaseType.Float64 => "view.readFloat64()",
                BaseType.Date => "view.readDate()",
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private string CompileDecodeField(TypeBase type, string target, int depth = 0)
        {
            var tab = new string(' ', indentStep);
            var nl = "\n" + new string(' ', depth * 2 * indentStep);
            var i = GeneratorUtils.LoopVariable(depth);
            return type switch
            {
                ArrayType at when at.IsBytes() => $"{target} = view.readBytes();",
                ArrayType at =>
                    $"{{" + nl +
                    $"{tab}let length{depth} = view.readUint32();" + nl +
                    $"{tab}{target} = new {TypeName(at)}(length{depth});" + nl +
                    $"{tab}for (let {i} = 0; {i} < length{depth}; {i}++) {{" + nl +
                    $"{tab}{tab}let x{depth}: {TypeName(at.MemberType)};" + nl +
                    $"{tab}{tab}{CompileDecodeField(at.MemberType, $"x{depth}", depth + 1)}" + nl +
                    $"{tab}{tab}{target}[{i}] = x{depth};" + nl +
                    $"{tab}}}" + nl +
                    $"}}",
                MapType mt =>
                    $"{{" + nl +
                    $"{tab}let length{depth} = view.readUint32();" + nl +
                    $"{tab}{target} = new {TypeName(mt)}();" + nl +
                    $"{tab}for (let {i} = 0; {i} < length{depth}; {i}++) {{" + nl +
                    $"{tab}{tab}let k{depth}: {TypeName(mt.KeyType)};" + nl +
                    $"{tab}{tab}let v{depth}: {TypeName(mt.ValueType)};" + nl +
                    $"{tab}{tab}{CompileDecodeField(mt.KeyType, $"k{depth}", depth + 1)}" + nl +
                    $"{tab}{tab}{CompileDecodeField(mt.ValueType, $"v{depth}", depth + 1)}" + nl +
                    $"{tab}{tab}{target}.set(k{depth}, v{depth});" + nl +
                    $"{tab}}}" + nl +
                    $"}}",
                ScalarType st => $"{target} = {ReadBaseType(st.BaseType)};",
                DefinedType dt when Schema.Definitions[dt.Name] is EnumDefinition ed =>
                    $"{target} = {ReadBaseType(ed.BaseType)} as {dt.Name};",
                DefinedType dt => $"{target} = {dt.Name}.readFrom(view);",
                _ => throw new InvalidOperationException($"CompileDecodeField: {type}")
            };
        }

        /// <summary>
        /// Generate a TypeScript type name for the given <see cref="TypeBase"/>.
        /// </summary>
        /// <param name="type">The field type to generate code for.</param>
        /// <returns>The TypeScript type name.</returns>
        private string TypeName(in TypeBase type)
        {
            switch (type)
            {
                case ScalarType st:
                    return st.BaseType switch
                    {
                        BaseType.Bool => "boolean",
                        BaseType.Byte or BaseType.UInt16 or BaseType.Int16 or BaseType.UInt32 or BaseType.Int32 or
                            BaseType.Float32 or BaseType.Float64 => "number",
                        BaseType.UInt64 or BaseType.Int64 => "bigint",
                        BaseType.String or BaseType.Guid => "string",
                        BaseType.Date => "Date",
                        _ => throw new ArgumentOutOfRangeException(st.BaseType.ToString())
                    };
                case ArrayType at when at.IsBytes():
                    return "Uint8Array";
                case ArrayType at:
                    return $"Array<{TypeName(at.MemberType)}>";
                case MapType mt:
                    return $"Map<{TypeName(mt.KeyType)}, {TypeName(mt.ValueType)}>";
                case DefinedType dt:
                    var skipPrefix = Schema.Definitions[dt.Name] is EnumDefinition or UnionDefinition;

                    return (skipPrefix ? string.Empty : "I") + dt.Name.ToPascalCase();
            }
            throw new InvalidOperationException($"GetTypeName: {type}");
        }

        private static string EscapeStringLiteral(string value)
        {
            // TypeScript accepts \u0000 style escape sequences, so we can escape the string JSON-style.
            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return JsonSerializer.Serialize(value, options);
        }

        private string EmitLiteral(Literal literal)
        {
            return literal switch
            {
                BoolLiteral bl => bl.Value ? "true" : "false",
                IntegerLiteral il when il.Type is ScalarType st && st.Is64Bit => $"BigInt(\"{il.Value}\")",
                IntegerLiteral il => il.Value,
                FloatLiteral fl when fl.Value == "inf" => "Number.POSITIVE_INFINITY",
                FloatLiteral fl when fl.Value == "-inf" => "Number.NEGATIVE_INFINITY",
                FloatLiteral fl when fl.Value == "nan" => "Number.NaN",
                FloatLiteral fl => fl.Value,
                StringLiteral sl => EscapeStringLiteral(sl.Value),
                GuidLiteral gl => EscapeStringLiteral(gl.Value.ToString("D")),
                _ => throw new ArgumentOutOfRangeException(literal.ToString()),
            };
        }

        /// <summary>
        /// Generate code for a Bebop schema.
        /// </summary>
        /// <returns>The generated code.</returns>
        public override string Compile(Version? languageVersion, TempoServices services = TempoServices.Both, bool writeGeneratedNotice = true)
        {
            var builder = new IndentedStringBuilder();
            if (writeGeneratedNotice)
            {
                builder.AppendLine(GeneratorUtils.GetXmlAutoGeneratedNotice());
            }
            builder.AppendLine("import { BebopView, BebopRuntimeError, BebopRecord } from \"bebop\";");
            if (Schema.Definitions.Values.OfType<ServiceDefinition>().Any())
            {
                if (services is TempoServices.Client or TempoServices.Both)
                {
                    builder.AppendLine("import { Metadata, MethodType } from \"@tempojs/common\";");
                    builder.AppendLine("import {  BaseClient, MethodInfo, CallOptions } from \"@tempojs/client\";");
                }
                if (services is TempoServices.Server or TempoServices.Both)
                {
                    builder.AppendLine("import { ServiceRegistry, BaseService, ServerContext, BebopMethodAny, BebopMethod } from \"@tempojs/server\";");
                }
            }

            builder.AppendLine("");
            if (!string.IsNullOrWhiteSpace(Schema.Namespace))
            {
                builder.AppendLine($"export namespace {Schema.Namespace} {{");
                builder.Indent(2);
            }

            foreach (var definition in Schema.Definitions.Values)
            {
                if (!string.IsNullOrWhiteSpace(definition.Documentation))
                {
                    builder.AppendLine(FormatDocumentation(definition.Documentation, string.Empty, 0));
                }
                if (definition is EnumDefinition ed)
                {
                    var is64Bit = ed.ScalarType.Is64Bit;
                    if (is64Bit)
                    {
                        builder.AppendLine($"export type {ed.Name} = bigint;");
                        builder.AppendLine($"export const {ed.Name} = {{");
                    }
                    else
                    {
                        builder.AppendLine($"export enum {ed.Name} {{");
                    }
                    for (var i = 0; i < ed.Members.Count; i++)
                    {
                        var field = ed.Members.ElementAt(i);
                        var deprecationReason = field.DeprecatedAttribute?.Value ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(field.Documentation))
                        {
                            builder.AppendLine(FormatDocumentation(field.Documentation, deprecationReason, 2));
                        }
                        else if (string.IsNullOrWhiteSpace(field.Documentation) && !string.IsNullOrWhiteSpace(deprecationReason))
                        {
                            builder.AppendLine(FormatDeprecationDoc(deprecationReason, 2));
                        }
                        if (is64Bit)
                        {
                            builder.AppendLine($"  {field.Name}: {field.ConstantValue}n,");
                            builder.AppendLine($"  {EscapeStringLiteral(field.ConstantValue.ToString())}: {EscapeStringLiteral(field.Name)},");
                        }
                        else
                        {
                            builder.AppendLine($"  {field.Name} = {field.ConstantValue},");
                        }
                    }
                    builder.AppendLine(is64Bit ? "};" : "}");
                    builder.AppendLine("");
                }
                else if (definition is RecordDefinition td)
                {
                    if (definition is FieldsDefinition fd)
                    {
                        builder.AppendLine($"export interface I{fd.ClassName()} extends BebopRecord {{");
                        for (var i = 0; i < fd.Fields.Count; i++)
                        {
                            var field = fd.Fields.ElementAt(i);
                            var type = TypeName(field.Type);
                            var deprecationReason = field.DeprecatedAttribute?.Value ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(field.Documentation))
                            {
                                builder.AppendLine(FormatDocumentation(field.Documentation, deprecationReason, 2));
                            }
                            else if (string.IsNullOrWhiteSpace(field.Documentation) && !string.IsNullOrWhiteSpace(deprecationReason))
                            {
                                builder.AppendLine(FormatDeprecationDoc(deprecationReason, 2));
                            }
                            builder.AppendLine($"  {(fd is StructDefinition { IsReadOnly: true } ? "readonly " : "")}{field.Name.ToCamelCase()}{(fd is MessageDefinition ? "?" : "")}: {type};");
                        }
                        builder.AppendLine("}");
                        builder.AppendLine();
                        builder.CodeBlock($"export class {td.ClassName()} implements I{td.ClassName()}", indentStep, () =>
                        {
                            if (td.OpcodeAttribute is not null)
                            {
                                builder.AppendLine($"public static readonly opcode: number = {td.OpcodeAttribute.Value} as {td.OpcodeAttribute.Value};");
                            }
                            if (td.DiscriminatorInParent != null)
                            {
                                // We codegen "1 as 1", "2 as 2"... because TypeScript otherwise infers the type "number" for this field, whereas a literal type is necessary to discriminate unions.
                                builder.AppendLine($"public readonly discriminator: number = {td.DiscriminatorInParent} as {td.DiscriminatorInParent};");
                            }
                            for (var i = 0; i < fd.Fields.Count; i++)
                            {
                                var field = fd.Fields.ElementAt(i);
                                var type = TypeName(field.Type);
                                builder.AppendLine($"public {(fd is StructDefinition { IsReadOnly: true } ? "readonly " : "")}{field.Name.ToCamelCase()}{(fd is MessageDefinition ? "?" : "")}: {type};");

                            }
                            builder.AppendLine();
                            const string paramaterName = "record";
                            builder.CodeBlock($"constructor({paramaterName}: I{td.ClassName()})", indentStep, () =>
                            {
                                for (var i = 0; i < fd.Fields.Count; i++)
                                {
                                    var field = fd.Fields.ElementAt(i);
                                    var fieldName = field.Name.ToCamelCase();
                                    builder.AppendLine($"this.{fieldName} = {paramaterName}.{fieldName};");
                                }
                            });
                        }, close: string.Empty);
                    }
                    else if (definition is UnionDefinition ud)
                    {
                        var expression = string.Join("\n  | ", ud.Branches.Select(b => $"{{ discriminator: {b.Discriminator}, value: I{b.Definition.Name} }}"));
                        if (string.IsNullOrWhiteSpace(expression)) expression = "never";
                        builder.AppendLine($"export type I{ud.Name}Type\n  = {expression};");

                        builder.AppendLine();


                        builder.CodeBlock($"export interface I{ud.ClassName()} extends BebopRecord", indentStep, () =>
                        {
                            builder.AppendLine($"readonly data: I{ud.Name}Type;");
                        });

                        builder.CodeBlock($"export class {ud.ClassName()} implements I{ud.ClassName()}", indentStep, () =>
                        {
                            builder.AppendLine();
                            builder.AppendLine($"public readonly data: I{ud.Name}Type;");
                            builder.AppendLine();
                            builder.CodeBlock($"private constructor(data: I{ud.ClassName()}Type)", indentStep, () =>
                            {
                                builder.AppendLine($"this.data = data;");
                            });
                            builder.AppendLine();
                            builder.CodeBlock($"public get discriminator()", indentStep, () =>
                            {
                                builder.AppendLine($"return this.data.discriminator;");
                            });
                            builder.AppendLine();
                            builder.CodeBlock($"public get value()", indentStep, () =>
                            {
                                builder.AppendLine($"return this.data.value;");
                            });
                            builder.AppendLine();
                            foreach (var b in ud.Branches)
                            {
                                builder.CodeBlock($"public static from{b.ClassName()}(value: I{b.ClassName()})", indentStep, () =>
                                {
                                    builder.AppendLine($"return new this({{ discriminator: {b.Discriminator}, value: new {b.ClassName()}(value)}});");
                                });
                                builder.AppendLine();
                            }
                        }, close: string.Empty);
                    }

                    builder.Indent(indentStep);
                    builder.CodeBlock($"public encode(): Uint8Array", indentStep, () =>
                    {
                        builder.AppendLine($"return {td.ClassName()}.encode(this);");
                    });
                    builder.AppendLine();

                    builder.CodeBlock($"public static encode(record: I{td.ClassName()}): Uint8Array", indentStep, () =>
                    {
                        builder.AppendLine("const view = BebopView.getInstance();");
                        builder.AppendLine("view.startWriting();");
                        builder.AppendLine($"{td.ClassName()}.encodeInto(record, view);");
                        builder.AppendLine("return view.toArray();");
                    });

                    builder.AppendLine();

                    builder.CodeBlock($"public static encodeInto(record: I{td.ClassName()}, view: BebopView): number", indentStep, () =>
                    {
                        builder.AppendLine("const before = view.length;");
                        builder.AppendLine(CompileEncode(td));
                        builder.AppendLine("const after = view.length;");
                        builder.AppendLine("return after - before;");
                    });
                    builder.AppendLine();
                    var targetType = td is UnionDefinition ? td.ClassName() : $"I{td.ClassName()}";
                    builder.CodeBlock($"public static decode(buffer: Uint8Array): {targetType}", indentStep, () =>
                    {
                        builder.AppendLine($"const view = BebopView.getInstance();");
                        builder.AppendLine($"view.startReading(buffer);");
                        builder.AppendLine($"return {td.ClassName()}.readFrom(view);");
                    });
                    builder.AppendLine();
                    builder.CodeBlock($"public static readFrom(view: BebopView): {targetType}", indentStep, () =>
                    {
                        builder.AppendLine(CompileDecode(td));
                    });

                    builder.Dedent(indentStep);
                    builder.AppendLine("}");
                    builder.AppendLine("");
                }
                else if (definition is ConstDefinition cd)
                {
                    builder.AppendLine($"export const {cd.Name}: {TypeName(cd.Value.Type)} = {EmitLiteral(cd.Value)};");
                    builder.AppendLine("");
                }
                else if (definition is ServiceDefinition)
                {
                    // noop

                }
                else
                {
                    throw new InvalidOperationException($"Unsupported definition {definition}");
                }
            }
            var serviceDefinitions = Schema.Definitions.Values.OfType<ServiceDefinition>();
            if (serviceDefinitions is not null && serviceDefinitions.Any() && services is not TempoServices.None)
            {
                if (services is TempoServices.Server or TempoServices.Both)
                {
                    foreach (var service in serviceDefinitions)
                    {
                        builder.CodeBlock($"export abstract class {service.BaseClassName()} extends BaseService", indentStep, () =>
                        {
                            builder.AppendLine($"public static readonly serviceName = '{service.ClassName()}';");
                            foreach (var method in service.Methods)
                            {
                                var methodType = method.Definition.Type;
                                if (methodType is MethodType.Unary)
                                {
                                    builder.AppendLine($"public abstract {method.Definition.Name.ToCamelCase()}(record: I{method.Definition.RequestDefinition}, context: ServerContext): Promise<I{method.Definition.ReturnDefintion}>;");
                                } else if (methodType is MethodType.ClientStream) {
                                    builder.AppendLine($"public abstract {method.Definition.Name.ToCamelCase()}(records: () => AsyncGenerator<I{method.Definition.RequestDefinition}, void, undefined>, context: ServerContext): Promise<I{method.Definition.ReturnDefintion}>;");
                                } else if (methodType is MethodType.ServerStream) {
                                    builder.AppendLine($"public abstract {method.Definition.Name.ToCamelCase()}(record: I{method.Definition.RequestDefinition}, context: ServerContext): AsyncGenerator<I{method.Definition.ReturnDefintion}, void, undefined>;");
                                } else if (methodType is MethodType.DuplexStream) {
                                    builder.AppendLine($"public abstract {method.Definition.Name.ToCamelCase()}(records: () => AsyncGenerator<I{method.Definition.RequestDefinition}, void, undefined>, context: ServerContext): AsyncGenerator<I{method.Definition.ReturnDefintion}, void, undefined>;");
                                } else {
                                    throw new InvalidOperationException($"Unsupported method type {methodType}");
                                }

                            }
                        });
                        builder.AppendLine();
                    }

                    builder.CodeBlock("export class TempoServiceRegistry extends ServiceRegistry", indentStep, () =>
                    {
                        builder.AppendLine("private static readonly staticServiceInstances: Map<string, BaseService> = new Map<string, BaseService>();");
                        builder.CodeBlock("public static register(serviceName: string)", indentStep, () =>
                        {
                            builder.CodeBlock("return (constructor: Function) =>", indentStep, () =>
                            {
                                builder.AppendLine("const service = Reflect.construct(constructor, [undefined]);");
                                builder.CodeBlock("if (TempoServiceRegistry.staticServiceInstances.has(serviceName))", indentStep, () =>
                                {
                                    builder.AppendLine("throw new Error(`Duplicate service registered: ${serviceName}`);");
                                });
                                builder.AppendLine("TempoServiceRegistry.staticServiceInstances.set(serviceName, service);");
                            });

                        });
                        builder.CodeBlock("public static tryGetService(serviceName: string): BaseService", indentStep, () =>
                        {
                            builder.AppendLine("const service = TempoServiceRegistry.staticServiceInstances.get(serviceName);");
                            builder.CodeBlock("if (service === undefined)", indentStep, () =>
                            {
                                builder.AppendLine("throw new Error(`Unable to retreive service '${serviceName}' - it is not registered.`);");
                            });
                            builder.AppendLine("return service;");
                        });

                        builder.AppendLine();

                        builder.CodeBlock("public init(): void", indentStep, () =>
                        {
                            builder.AppendLine("let service: BaseService;");
                            builder.AppendLine("let serviceName: string;");
                            foreach (var service in serviceDefinitions)

                            {

                                builder.AppendLine($"serviceName = '{service.ClassName()}';");
                                builder.AppendLine($"service = TempoServiceRegistry.tryGetService(serviceName);");
                                builder.CodeBlock($"if (!(service instanceof {service.BaseClassName()}))", indentStep, () =>
                                {
                                    builder.AppendLine("throw new Error('todo');");
                                });
                                builder.AppendLine($"service.setLogger(this.logger.clone(serviceName));");
                                builder.AppendLine("TempoServiceRegistry.staticServiceInstances.delete(serviceName);");
                                builder.AppendLine("this.serviceInstances.push(service);");
                                foreach (var method in service.Methods)
                                {
                                    var methodType = method.Definition.Type;
                                    var methodName = method.Definition.Name.ToCamelCase();
                                    builder.CodeBlock($"if (this.methods.has({method.Id}))", indentStep, () =>
                                    {
                                        builder.AppendLine($"const conflictService = this.methods.get({method.Id})!;");
                                        builder.AppendLine($"throw new Error(`{service.ClassName()}.{methodName} collides with ${{conflictService.service}}.${{conflictService.name}}`)");
                                    });
                                    builder.CodeBlock($"this.methods.set({method.Id},", indentStep, () =>
                                    {
                                        builder.AppendLine($"name: '{methodName}',");
                                        builder.AppendLine($"service: serviceName,");
                                        builder.AppendLine($"invoke: service.{methodName},");
                                        builder.AppendLine($"serialize: {method.Definition.ReturnDefintion}.encode,");
                                        builder.AppendLine($"deserialize: {method.Definition.RequestDefinition}.decode,");
                                        builder.AppendLine($"type: MethodType.{RpcSchema.GetMethodTypeName(methodType)},");
                                    }, close: $"}} as BebopMethod<I{method.Definition.RequestDefinition}, I{method.Definition.ReturnDefintion}>);");
                                }
                            }

                        });

                        builder.AppendLine();
                        builder.CodeBlock("getMethod(id: number): BebopMethodAny | undefined", indentStep, () =>
                        {
                            builder.AppendLine("return this.methods.get(id);");
                        });
                    });


                }

                if (services is TempoServices.Client or TempoServices.Both)
                {
                    static (string RequestType, string ResponseType) GetFunctionTypes(FunctionDefinition definition)
                    {
                        return definition.Type switch
                        {
                            MethodType.Unary => ($"I{definition.RequestDefinition}", $"Promise<I{definition.ReturnDefintion}>"),
                            MethodType.ServerStream => ($"I{definition.RequestDefinition}", $"Promise<AsyncGenerator<I{definition.ReturnDefintion}, void, undefined>>"),
                            MethodType.ClientStream => ($"() => AsyncGenerator<I{definition.RequestDefinition}, void, undefined>", $"Promise<I{definition.ReturnDefintion}>"),
                            MethodType.DuplexStream => ($"() => AsyncGenerator<I{definition.RequestDefinition}, void, undefined>", $"Promise<AsyncGenerator<I{definition.ReturnDefintion}, void, undefined>>"),
                            _ => throw new InvalidOperationException($"Unsupported function type {definition.Type}")
                        };
                    }

                    foreach (var service in serviceDefinitions)
                    {
                        var clientName = service.ClassName().ReplaceLastOccurrence("Service", "Client");
                        builder.CodeBlock($"export interface I{clientName}", indentStep, () =>
                        {
                            foreach (var method in service.Methods)
                            {
                                var (requestType, responseType) = GetFunctionTypes(method.Definition);
                                builder.AppendLine($"{method.Definition.Name.ToCamelCase()}(request: {requestType}): {responseType};");
                                builder.AppendLine($"{method.Definition.Name.ToCamelCase()}(request: {requestType}, metadata: Metadata): {responseType};");
                            }
                        });
                        builder.AppendLine();
                        builder.CodeBlock($"export class {clientName} extends BaseClient implements I{clientName}", indentStep, () =>
                        {
                            foreach (var method in service.Methods)
                            {
                                var methodInfoName = $"{method.Definition.Name.ToCamelCase()}MethodInfo";
                                var methodName = method.Definition.Name.ToCamelCase();
                                var (requestType, responseType) = GetFunctionTypes(method.Definition);
                                var methodType = method.Definition.Type;
                                builder.CodeBlock($"private static readonly {methodInfoName}: MethodInfo<I{method.Definition.RequestDefinition}, I{method.Definition.ReturnDefintion}> =", indentStep, () =>
                                {
                                    builder.AppendLine($"name: '{methodName}',");
                                    builder.AppendLine($"service: '{service.ClassName()}',");
                                    builder.AppendLine($"id: {method.Id},");
                                    builder.AppendLine($"serialize: {method.Definition.RequestDefinition}.encode,");
                                    builder.AppendLine($"deserialize: {method.Definition.ReturnDefintion}.decode,");
                                    builder.AppendLine($"type: MethodType.{RpcSchema.GetMethodTypeName(methodType)},");
                                });

                                builder.AppendLine($"async {methodName}(request: {requestType}): {responseType};");
                                builder.AppendLine($"async {methodName}(request: {requestType}, options: CallOptions): {responseType};");

                                builder.CodeBlock($"async {methodName}(request: {requestType}, options?: CallOptions): {responseType}", indentStep, () =>
                                {
                                    if (methodType is MethodType.Unary)
                                    {
                                        builder.AppendLine($"return await this.channel.startUnary(request, this.getContext(), {clientName}.{methodInfoName}, options);");
                                    }
                                    else if (methodType is MethodType.ServerStream)
                                    {
                                        builder.AppendLine($"return await this.channel.startServerStream(request, this.getContext(), {clientName}.{methodInfoName}, options);");
                                    }
                                    else if (methodType is MethodType.ClientStream)
                                    {
                                        builder.AppendLine($"return await this.channel.startClientStream(request, this.getContext(), {clientName}.{methodInfoName}, options);");
                                    }
                                    else if (methodType is MethodType.DuplexStream)
                                    {
                                        builder.AppendLine($"return await this.channel.startDuplexStream(request, this.getContext(), {clientName}.{methodInfoName}, options);");
                                    }
                                    else throw new InvalidOperationException($"Unsupported method type {methodType}");
                                });
                            }

                        });
                    }
                }
            }


            if (!string.IsNullOrWhiteSpace(Schema.Namespace))
            {
                builder.Dedent(2);
                builder.AppendLine("}");
            }
            return builder.ToString();
        }

        public override void WriteAuxiliaryFiles(string outputPath)
        {
            // There is nothing to do here now that BebopView.ts is an npm package.
        }
    }
}
