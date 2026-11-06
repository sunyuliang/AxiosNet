using System.Text;
using System.Text.Json;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.Serialization;

namespace AxiosNet;

// 分组设置独立成文件，嵌套类型各自放在自己的部分里。

public sealed partial class AxiosConfig
{
    /// <summary>序列化相关设置（<see cref="AxiosConfig.Serialization"/>）。</summary>
    public sealed class SerializationSettings
    {
        /// <summary>JSON 序列化选项，只对默认 JSON 序列化器生效；表单与 multipart 的字段命名见 <see cref="FormFieldName"/>。</summary>
        public JsonSerializerOptions? JsonOptions { get; set; }

        /// <summary>
        /// 按媒体类型登记的序列化器表（每个 <see cref="AxiosClient"/> 构造时保证有一份）。
        /// 查找顺序固定为「按媒体类型精确登记 → 表内默认项」；未命中再到进程注册表（按媒体类型登记优先，<see cref="AxiosContent.Default"/> 兜底），最后内置 JSON。
        /// 属实例级：库产出的配置（如回放 <see cref="AxiosResponse.Config"/>）合并时不带走来源实例的登记。
        /// 按请求拿到的是实例那张表本身（同一引用），直接 <c>Register</c> 会改到实例；只在本次请求生效请 new 一张表再赋值。
        /// </summary>
        public ContentSerializerRegistry BodySerializers
        {
            get => _bodySerializers ??= new ContentSerializerRegistry();
            set => _bodySerializers = value ?? new ContentSerializerRegistry();
        }

        private ContentSerializerRegistry? _bodySerializers;

        /// <summary>表是否被显式创建过；合并层用它判断「这一组有没有按请求设置」。</summary>
        internal ContentSerializerRegistry? BodySerializersOrNull => _bodySerializers;

        private Encoding? _requestEncoding;
        private Encoding? _responseEncoding;
        private bool _textEncodingSet;

        /// <summary>
        /// 请求与响应共用的文本编码；设置后两个方向都按它解码/编码，<c>null</c> 表示回到自动识别。
        /// 只影响字符串与表单文本，不影响 byte[] / Stream 透传。
        /// </summary>
        public Encoding? TextEncoding
        {
            get => _responseEncoding ?? _requestEncoding;
            set
            {
                _requestEncoding = value;
                _responseEncoding = value;
                _textEncodingSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool TextEncodingSet => _textEncodingSet;

        /// <summary>对应 axios paramsSerializer：自定义 query string 生成方式，返回不含前导 <c>?</c> 的查询串；null 时使用内置规则。</summary>
        public Func<object?, string>? QueryEncoder { get; set; }

        /// <summary>表单 / multipart 字段名生成规则：参数为（父级前缀，键名），默认 <c>prefix[key]</c>；表单不走序列化器，这是字段命名的唯一入口。</summary>
        public Func<string?, string, string>? FormFieldName { get; set; }

        private AxiosCollectionFormat _collectionFormat = AxiosCollectionFormat.Brackets;

        /// <summary><see cref="AxiosConfig.Params"/> 里集合值的编码形态，默认 <see cref="AxiosCollectionFormat.Brackets"/>（<c>a[]=1&amp;a[]=2</c>）；设置了 <see cref="QueryEncoder"/> 时本项不参与。</summary>
        public AxiosCollectionFormat CollectionFormat
        {
            get => _collectionFormat;
            set
            {
                _collectionFormat = value;
                CollectionFormatSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
        internal bool CollectionFormatSet { get; set; }

        /// <summary>
        /// 实例级值格式化钩子：值转线上文本时调用（路径 / 查询 / 请求头（含头集合的字典值）/ 表单字段 / multipart 文本部件 / 裸标量正文），
        /// 第二个参数是值所在的 <see cref="ValueTextChannel"/>，同一个钩子可以按位置给出不同文本（如请求头用 RFC 1123、查询串用 ISO 8601）。
        /// 返回的字符串直接作为该值的线上文本（路径段与查询 / 表单值随后仍会被转义）；返回 <c>null</c> 表示本次不处理，回落到默认规则。
        /// </summary>
        /// <remarks>
        /// 设在 <see cref="AxiosClient.Defaults"/> 上对整个客户端生效；直接调用时也可在单次请求的配置里覆盖。
        /// 回放 <see cref="AxiosResponse.Config"/> 时按实例级项处理：取目标实例的默认值，来源实例的钩子不跟过来。
        /// </remarks>
        public Func<object?, ValueTextChannel, string?>? ValueText
        {
            get => _valueText;
            set
            {
                _valueText = value;
                ValueTextSet = true;
            }
        }

        private Func<object?, ValueTextChannel, string?>? _valueText;

        /// <summary>是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
        internal bool ValueTextSet { get; private set; }

        /// <summary>对应 axios transformRequest：拦截器之后、请求体编码之前依次作用于 <see cref="Body"/>。列表线程安全，枚举走快照。</summary>
        public IList<Func<object?, AxiosConfig, object?>> TransformRequest
        {
            get => _transformRequest ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                // 已是包装类型时不重复包装（保留同一实例语义）。
                _transformRequest = value as SynchronizedList<Func<object?, AxiosConfig, object?>>
                    ?? new SynchronizedList<Func<object?, AxiosConfig, object?>>(value);
            }
        }

        private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformRequest;

        /// <summary>对应 axios transformResponse：响应体反序列化之后依次作用于数据（如统一拆包），流式响应不适用；可读 <see cref="Response"/> 拿响应头。</summary>
        public IList<Func<object?, AxiosConfig, object?>> TransformResponse
        {
            get => _transformResponse ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                // 已是包装类型时不重复包装（保留同一实例语义）。
                _transformResponse = value as SynchronizedList<Func<object?, AxiosConfig, object?>>
                    ?? new SynchronizedList<Func<object?, AxiosConfig, object?>>(value);
            }
        }

        private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformResponse;

        internal bool HasTransformRequest => _transformRequest is { Count: > 0 };

        internal Func<object?, AxiosConfig, object?>[]? TransformRequestSnapshot
            => _transformRequest is { Count: > 0 } ? _transformRequest.Snapshot() : null;

        internal int TransformRequestCount => _transformRequest?.Count ?? 0;

        internal bool HasTransformResponse => _transformResponse is { Count: > 0 };

        internal Func<object?, AxiosConfig, object?>[]? TransformResponseSnapshot
            => _transformResponse is { Count: > 0 } ? _transformResponse.Snapshot() : null;

        internal int TransformResponseCount => _transformResponse?.Count ?? 0;

        /// <summary>把 <paramref name="incoming"/> 里有值的项覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(SerializationSettings incoming)
        {
            JsonOptions = incoming.JsonOptions ?? JsonOptions;
            if (incoming._bodySerializers is not null)
            {
                BodySerializers = incoming._bodySerializers;
            }

            if (incoming.TextEncodingSet)
            {
                TextEncoding = incoming.TextEncoding;
            }

            QueryEncoder = incoming.QueryEncoder ?? QueryEncoder;
            FormFieldName = incoming.FormFieldName ?? FormFieldName;

            // CollectionFormat 是值类型，靠 *Set 标记区分「未设置」与默认值。
            if (incoming.CollectionFormatSet)
            {
                CollectionFormat = incoming.CollectionFormat;
            }
        }

        /// <summary>浅拷贝（合并层用）：策略与委托共用，不深克隆；transform 链写时复制共享。</summary>
        internal SerializationSettings Clone()
        {
            var clone = new SerializationSettings
            {
                JsonOptions = JsonOptions,
                _bodySerializers = _bodySerializers,
                _requestEncoding = _requestEncoding,
                _responseEncoding = _responseEncoding,
                _textEncodingSet = _textEncodingSet,
                QueryEncoder = QueryEncoder,
                FormFieldName = FormFieldName,
                _collectionFormat = _collectionFormat,
                CollectionFormatSet = CollectionFormatSet,
                _valueText = _valueText,
                ValueTextSet = ValueTextSet,
            };
            clone._transformRequest = _transformRequest?.ShareIfNotEmpty();
            clone._transformResponse = _transformResponse?.ShareIfNotEmpty();
            return clone;
        }
    }
}
