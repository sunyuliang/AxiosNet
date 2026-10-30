using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet;

// 分组设置独立成文件，嵌套类型各自放在自己的部分里。

public sealed partial class AxiosConfig
{
    /// <summary>序列化相关设置（<see cref="AxiosConfig.Serialization"/>）。</summary>
    public sealed class SerializationSettings
    {
        /// <summary>JSON 序列化选项，只对默认 JSON 序列化器生效；表单与 multipart 的字段命名见 <see cref="FormFieldNameSelector"/>。</summary>
        public JsonSerializerOptions? JsonSerializerOptions { get; set; }

        /// <summary>
        /// 内容序列化器，优先级最高：高于 <see cref="AxiosContent.Serializer"/> 与按媒体类型的自动匹配；null 表示不覆盖。
        /// 只有业务对象经过它，string / byte[] / Stream / HttpContent / <c>IAsyncEnumerable</c> 字节块请求体原样发送。
        /// </summary>
        public IContentSerializer? ContentSerializer { get; set; }

        /// <summary>
        /// 按媒体类型登记的序列化器表（每个 <see cref="AxiosClient"/> 构造时保证有一份，见 <see cref="AxiosClient.ContentSerializers"/>）。
        /// 优先级低于 <see cref="ContentSerializer"/>、高于全局 <see cref="AxiosContent.Serializer"/> 与全局注册表。
        /// 属实例级：库产出的配置（如回放 <see cref="AxiosResponse.Config"/>）合并时不带走来源实例的登记。
        /// 按请求拿到的是实例那张表本身（同一引用），直接 <c>Register</c> 会改到实例；只在本次请求生效请 new 一张表再赋值。
        /// </summary>
        public ContentSerializerRegistry? ContentSerializers { get; set; }

        /// <summary>请求体文本编码（string / 对象 / 表单），null 时使用 UTF-8；byte[] / Stream / <c>IAsyncEnumerable</c> 字节块原样透传。</summary>
        public Encoding? RequestEncoding { get; set; }

        /// <summary>响应体文本编码，null 时自动识别（Content-Type charset → BOM → UTF-8）；设置后强制按指定编码解码。</summary>
        public Encoding? ResponseEncoding { get; set; }

        /// <summary>对应 axios paramsSerializer：自定义 query string 生成方式，返回不含前导 <c>?</c> 的查询串；null 时使用内置规则。</summary>
        public Func<object?, string>? ParamsSerializer { get; set; }

        /// <summary>表单 / multipart 字段名生成规则：参数为（父级前缀, 键名），默认 <c>prefix[key]</c>；表单不走序列化器，这是字段命名的唯一入口。</summary>
        public Func<string?, string, string>? FormFieldNameSelector { get; set; }

        private AxiosCollectionFormat _collectionFormat = AxiosCollectionFormat.Brackets;

        /// <summary><see cref="AxiosConfig.Params"/> 里集合值的编码形态，默认 <see cref="AxiosCollectionFormat.Brackets"/>（<c>a[]=1&amp;a[]=2</c>）；设置了 <see cref="ParamsSerializer"/> 时本项不参与。</summary>
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

        /// <summary>把 <paramref name="incoming"/> 里有值的项覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(SerializationSettings incoming)
        {
            JsonSerializerOptions = incoming.JsonSerializerOptions ?? JsonSerializerOptions;
            ContentSerializer = incoming.ContentSerializer ?? ContentSerializer;
            ContentSerializers = incoming.ContentSerializers ?? ContentSerializers;
            RequestEncoding = incoming.RequestEncoding ?? RequestEncoding;
            ResponseEncoding = incoming.ResponseEncoding ?? ResponseEncoding;
            ParamsSerializer = incoming.ParamsSerializer ?? ParamsSerializer;
            FormFieldNameSelector = incoming.FormFieldNameSelector ?? FormFieldNameSelector;

            // CollectionFormat 是值类型，靠 *Set 标记区分「未设置」与默认值。
            if (incoming.CollectionFormatSet)
            {
                CollectionFormat = incoming.CollectionFormat;
            }
        }

        internal SerializationSettings Clone() => (SerializationSettings)MemberwiseClone();
    }
}
