namespace AxiosNet.Tests.Infrastructure;

/// <summary>
/// 会改写进程级序列化状态（<see cref="AxiosNet.AxiosContent.Serializer"/> / 全局注册表）的用例的独占集合：
/// 这类用例必须与 <c>Assert.Null(AxiosContent.Serializer)</c> 之类的只读用例串行，否则会随并行调度随机失败。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSerializationCollection
{
    public const string Name = "GlobalSerialization";
}
