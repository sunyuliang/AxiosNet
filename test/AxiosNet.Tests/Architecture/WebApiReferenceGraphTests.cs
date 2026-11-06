using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AxiosNet.Tests.Architecture;

/// <summary>
/// 产物级引用图校验：直接读编译出来的 <c>AxiosNet.dll</c> 元数据，只要「非 <c>AxiosNet.WebAPI</c> 命名空间的类型」
/// 引用了 WebAPI 命名空间下的类型就失败。这是三条护栏里最硬的一条——名字黑名单能绕过，元数据引用绕不过。
/// <para>
/// 覆盖面：TypeReference / MemberReference / CustomAttribute / 类型定义的基类与接口 / 字段、方法、属性、事件签名 /
/// 泛型约束。WebAPI 自身类型之间的引用不参与判定（它们本来就在同一个程序集里）。
/// 方法体里的 <c>ldtoken</c> 等 IL 引用不在这里扫描——那条由「无 WebAPI 构建」（类型消失即编译失败）
/// 与 BannedApiAnalyzers（符号级拒绝）覆盖。
/// </para>
/// </summary>
public sealed class WebApiReferenceGraphTests
{
    private const string WebApiNamespace = "AxiosNet.WebAPI";

    [Fact]
    public void CoreTypesDoNotReferenceWebApiTypesInAssemblyMetadata()
    {
        var assemblyPath = typeof(AxiosConfig).Assembly.Location;
        Assert.True(File.Exists(assemblyPath), $"找不到编译产物：{assemblyPath}");

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var offenders = new List<string>();

        // ① TypeReference：本程序集里出现对 WebAPI 类型的引用（跨模块/转发类型时会走到这里）。
        foreach (var handle in reader.TypeReferences)
        {
            var name = FullReferenceName(reader, handle);
            if (IsWebApi(name))
            {
                offenders.Add($"TypeReference → {name}");
            }
        }

        // ② MemberReference：成员引用挂在 WebAPI 类型上。
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            var owner = DescribeType(reader, member.Parent);
            if (IsWebApi(owner))
            {
                offenders.Add($"MemberReference {owner}::{reader.GetString(member.Name)}");
            }
        }

        // ③④ 程序集 / 模块级特性：内核程序集不得用 WebAPI 的注入点。
        CheckAttributes(reader, reader.GetAssemblyDefinition().GetCustomAttributes(), "assembly", offenders);
        CheckAttributes(reader, reader.GetModuleDefinition().GetCustomAttributes(), "module", offenders);

        var provider = new SignatureCollector(reader);

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var fullName = FullDefinitionName(reader, handle);

            // WebAPI 自己的类型互相引用是正常的，整段跳过。
            if (IsWebApi(fullName))
            {
                continue;
            }

            // ③ 类型定义上的特性（特性构造器属于 WebAPI 即算引用）。
            CheckAttributes(reader, type.GetCustomAttributes(), fullName, offenders);

            // ④ 基类 / 接口 / 签名 / 泛型约束里的类型引用。
            provider.Reset();
            if (!type.BaseType.IsNil)
            {
                provider.Add(type.BaseType);
            }
            foreach (var implementationHandle in type.GetInterfaceImplementations())
            {
                var implementation = reader.GetInterfaceImplementation(implementationHandle);
                provider.Add(implementation.Interface);
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                CheckAttributes(reader, field.GetCustomAttributes(), $"{fullName}.{reader.GetString(field.Name)}", offenders);
                field.DecodeSignature(provider, genericContext: null);
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var methodName = $"{fullName}.{reader.GetString(method.Name)}";
                CheckAttributes(reader, method.GetCustomAttributes(), methodName, offenders);
                method.DecodeSignature(provider, genericContext: null);

                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = reader.GetParameter(parameterHandle);
                    CheckAttributes(reader, parameter.GetCustomAttributes(), $"{methodName}(param {parameter.SequenceNumber})", offenders);
                }

                CheckGenericConstraints(reader, method.GetGenericParameters(), methodName, offenders);
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var propertyName = $"{fullName}.{reader.GetString(property.Name)}";
                CheckAttributes(reader, property.GetCustomAttributes(), propertyName, offenders);
                property.DecodeSignature(provider, genericContext: null);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var @event = reader.GetEventDefinition(eventHandle);
                var eventName = $"{fullName}.{reader.GetString(@event.Name)}";
                CheckAttributes(reader, @event.GetCustomAttributes(), eventName, offenders);
                provider.Add(@event.Type);
            }

            CheckGenericConstraints(reader, type.GetGenericParameters(), fullName, offenders);

            foreach (var seen in provider.Drain())
            {
                if (IsWebApi(seen))
                {
                    offenders.Add($"{fullName} 的签名/基类/接口 → {seen}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "内核（非 AxiosNet.WebAPI 命名空间）不得在元数据层引用 WebAPI 类型：" + Environment.NewLine
                + string.Join(Environment.NewLine, offenders.Distinct(StringComparer.Ordinal)));
    }

    private static void CheckAttributes(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string owner,
        List<string> offenders)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var constructorOwner = DescribeType(reader, attribute.Constructor);
            if (IsWebApi(constructorOwner))
            {
                offenders.Add($"{owner} 上的特性 → {constructorOwner}");
            }
        }
    }

    private static void CheckGenericConstraints(
        MetadataReader reader,
        GenericParameterHandleCollection parameters,
        string owner,
        List<string> offenders)
    {
        foreach (var parameterHandle in parameters)
        {
            var parameter = reader.GetGenericParameter(parameterHandle);
            foreach (var constraintHandle in parameter.GetConstraints())
            {
                var constraint = reader.GetGenericParameterConstraint(constraintHandle);
                var constraintType = DescribeType(reader, constraint.Type);
                if (IsWebApi(constraintType))
                {
                    offenders.Add($"{owner} 的泛型约束 → {constraintType}");
                }
            }
        }
    }

    private static bool IsWebApi(string fullName)
        => fullName.Equals(WebApiNamespace, StringComparison.Ordinal)
            || fullName.StartsWith(WebApiNamespace + ".", StringComparison.Ordinal);

    private static string DescribeType(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullDefinitionName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => FullReferenceName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => string.Empty,
            HandleKind.MemberReference => DescribeType(reader, reader.GetMemberReference((MemberReferenceHandle)handle).Parent),
            HandleKind.MethodDefinition => FullDefinitionName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()),
            HandleKind.FieldDefinition => FullDefinitionName(reader, reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType()),
            _ => string.Empty,
        };
    }

    private static string FullDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var @namespace = reader.GetString(type.Namespace);
        if (@namespace.Length > 0)
        {
            return $"{@namespace}.{name}";
        }

        var declaring = type.GetDeclaringType();
        return declaring.IsNil ? name : $"{FullDefinitionName(reader, declaring)}.{name}";
    }

    private static string FullReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var @namespace = reader.GetString(type.Namespace);
        if (@namespace.Length > 0)
        {
            return $"{@namespace}.{name}";
        }

        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{FullReferenceName(reader, (TypeReferenceHandle)type.ResolutionScope)}.{name}"
            : name;
    }

    /// <summary>把签名里出现的每个类型名记下来；只用于事后过滤 WebAPI 前缀。</summary>
    private sealed class SignatureCollector : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _reader;
        private readonly List<string> _seen = new();

        public SignatureCollector(MetadataReader reader)
        {
            _reader = reader;
        }

        public void Reset() => _seen.Clear();

        public IEnumerable<string> Drain()
        {
            foreach (var name in _seen)
            {
                yield return name;
            }
        }

        public void Add(EntityHandle handle)
        {
            if (handle.IsNil)
            {
                return;
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    Record(FullDefinitionName(_reader, (TypeDefinitionHandle)handle));
                    break;
                case HandleKind.TypeReference:
                    Record(FullReferenceName(_reader, (TypeReferenceHandle)handle));
                    break;
                case HandleKind.TypeSpecification:
                    _reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, genericContext: null);
                    break;
            }
        }

        private string Record(string name)
        {
            _seen.Add(name);
            return name;
        }

        public string GetArrayType(string elementType, ArrayShape shape) => elementType;

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => signature.ReturnType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => Record(FullDefinitionName(reader, handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => Record(FullReferenceName(reader, handle));

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
